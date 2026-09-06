using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ToolkitLauncher.Core;

public sealed record TransferProgress(string Message, double Percent);
public sealed record PreparedPackage(string DownloadPath, string SetupPath);

public sealed class PackageService(HttpClient http, string root)
{
    public static void ValidateSource(AppDefinition app, ReleaseAsset asset)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.AbsolutePath.StartsWith($"/JohnDevAc/{app.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer URL does not belong to this project's GitHub releases.");
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The release has an invalid installer filename.");
        if (asset.Size <= 0 || asset.Size > 4L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The release has an invalid package size.");
        if (asset.Digest is null || !Regex.IsMatch(asset.Digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("This release has no GitHub SHA-256 digest. Open Releases to obtain it manually.");
    }

    public async Task<PreparedPackage> PrepareAsync(AppDefinition app, ReleaseAsset asset,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, bool extract = true)
    {
        ValidateSource(app, asset);
        var digest = asset.Digest![7..].ToLowerInvariant();
        var directory = Path.Combine(root, app.Id, digest);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, asset.Name);
        if (!File.Exists(target) || !await VerifyAsync(target, asset, cancellationToken))
        {
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                progress?.Report(new("Downloading installer…", 0));
                using var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                    throw new InvalidDataException("The download size does not match the GitHub release.");
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072]; long total = 0; var lastReport = Environment.TickCount64;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        total += read;
                        if (total > asset.Size) throw new InvalidDataException("The download exceeds the expected size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        if (Environment.TickCount64 - lastReport >= 100)
                        {
                            progress?.Report(new($"Downloading · {total / 1048576d:0.0} / {asset.Size / 1048576d:0.0} MB", 90d * total / asset.Size));
                            lastReport = Environment.TickCount64;
                        }
                    }
                }
                progress?.Report(new("Verifying SHA-256…", 92));
                if (!await VerifyAsync(partial, asset, cancellationToken)) throw new InvalidDataException("Installer verification failed. The file was discarded; retry the download.");
                File.Move(partial, target, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        progress?.Report(new("Package verified", 95));
        if (app.PackageKind == PackageKind.Executable || !extract) return new(target, target);
        // A fresh extraction prevents reuse of modified or incomplete extracted binaries.
        var extractDirectory = Path.Combine(directory, "package-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new("Unpacking complete setup package…", 96));
            await Task.Run(() => ExtractSafely(target, extractDirectory, cancellationToken), cancellationToken);
            foreach (var name in app.SetupNames)
            {
                var matches = Directory.GetFiles(extractDirectory, name, SearchOption.AllDirectories);
                if (matches.Length == 1) return new(target, matches[0]);
                if (matches.Length > 1) throw new InvalidDataException("The package contains multiple setup programs with the same name.");
            }
            throw new InvalidDataException("The complete package does not contain a recognized setup program.");
        }
        catch { if (Directory.Exists(extractDirectory)) Directory.Delete(extractDirectory, true); throw; }
    }

    public static async Task<bool> VerifyAsync(string path, ReleaseAsset asset, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != asset.Size || asset.Digest is null) return false;
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        return string.Equals("sha256:" + hash, asset.Digest, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExtractSafely(string zipPath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var segments = relative.Split('/');
            if (Path.IsPathRooted(relative) || segments.Any(s => s == ".." || s == "." ||
                s.Contains(':') || s.EndsWith(' ') || s.EndsWith('.') ||
                Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe archive entry: " + entry.FullName);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !names.Add(target))
                throw new InvalidDataException("Invalid or duplicate archive path.");
            total += entry.Length;
            if (total > 4L * 1024 * 1024 * 1024 || zip.Entries.Count > 30000) throw new InvalidDataException("Archive exceeds extraction limits.");
            if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[131072]; int read; long written = 0;
            while ((read = input.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += read;
                if (written > entry.Length) throw new InvalidDataException("Archive entry exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("Archive entry is incomplete.");
        }
    }
}
