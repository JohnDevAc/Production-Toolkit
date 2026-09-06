using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed class IconService(HttpClient http, string cacheDirectory)
{
    private const int MaximumBytes = 5 * 1024 * 1024;

    // Extract resources afresh on each explicit check. No shell icon cache, executable
    // loading or lingering file handles, so in-place updates are visible immediately.
    public static BitmapSource? ReadInstalled(string path)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out handle, IntPtr.Zero, 256) != 0 || handle == IntPtr.Zero) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException) { return null; }
        finally { if (handle != IntPtr.Zero) DestroyIcon(handle); }
    }

    public async Task<BitmapSource?> ReadRemoteAsync(AppDefinition app, string reference, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var url = $"https://raw.githubusercontent.com/JohnDevAc/{app.Repository}/{Uri.EscapeDataString(reference)}/{app.RepositoryIconPath}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var path = Path.Combine(cacheDirectory, key + ".ico");
        BitmapSource? cached = null;
        try { if (File.Exists(path) && new FileInfo(path).Length <= MaximumBytes) cached = Decode(await File.ReadAllBytesAsync(path, cancellationToken)); }
        catch (Exception e) when (IsIconFailure(e)) { }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Production-Toolkit/1.1");
            if (cached is not null && File.Exists(path + ".etag"))
            {
                var etag = await File.ReadAllTextAsync(path + ".etag", token);
                if (System.Net.Http.Headers.EntityTagHeaderValue.TryParse(etag, out var parsed)) request.Headers.IfNoneMatch.Add(parsed);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) return cached;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Icon is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + read > MaximumBytes) throw new InvalidDataException("Icon is too large.");
                memory.Write(buffer, 0, read);
            }
            var bytes = memory.ToArray();
            var icon = Decode(bytes);
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, token);
                File.Move(path + ".tmp", path, true);
                await File.WriteAllTextAsync(path + ".etag", response.Headers.ETag?.ToString() ?? "", token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return icon;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return cached; }
        catch (Exception e) when (IsIconFailure(e)) { return cached; }
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Icon is too large.");
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames.Where(f => f.PixelWidth <= 1024 && f.PixelHeight <= 1024)
            .OrderByDescending(f => f.PixelWidth).FirstOrDefault() ?? throw new InvalidDataException("Icon dimensions are unsupported.");
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsIconFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or
        HttpRequestException or ArgumentException or NotSupportedException or COMException or System.IO.FileFormatException;

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, IntPtr small, uint size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
