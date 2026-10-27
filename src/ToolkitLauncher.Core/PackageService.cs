using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ToolkitLauncher.Core;

public sealed record TransferProgress(string Message, double Percent);
public sealed record PreparedPackage(string DownloadPath, string SetupPath)
{
    public LifecycleCapabilities? Capabilities { get; init; }
    public string? PayloadVersion { get; init; }
}

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
        OwnedPaths.CheckNoReparse(directory);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, asset.Name);
        if (!File.Exists(target) || !await VerifyAsync(target, asset, cancellationToken))
        {
            var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                progress?.Report(new("Checking required download…", 0));
                await NdiSuite.Installation.DownloadReadiness.CheckAsync(http, new Uri(asset.DownloadUrl), cancellationToken);
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
                    while ((read = await NdiSuite.Installation.DownloadReadiness.ReadAsync(source, buffer, cancellationToken)) != 0)
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
        if (app.PackageKind == PackageKind.PortableZip)
            throw new InvalidOperationException("Portable packages must be installed with InstallPortableAsync.");
        // A fresh extraction prevents reuse of modified or incomplete extracted binaries.
        var extractDirectory = PreparedDirectory(app);
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

    public async Task<string> InstallPortableAsync(AppDefinition app, ReleaseAsset asset, string applicationsRoot,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (app.PackageKind != PackageKind.PortableZip) throw new ArgumentException("This application is not portable.", nameof(app));
        var package = await PrepareAsync(app, asset, progress, cancellationToken, extract: false);
        // Each verified extraction gets a new folder. Updates never overwrite a running
        // copy or delete the prior release; preferences switch only after validation.
        var directory = Path.Combine(applicationsRoot, app.Id, "package-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new("Unpacking application…", 96));
            await Task.Run(() => ExtractSafely(package.DownloadPath, directory, cancellationToken), cancellationToken);
            var matches = app.ExecutableNames.SelectMany(name => Directory.GetFiles(directory, name, SearchOption.AllDirectories)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The portable package must contain one recognized application executable.");
            var applicationDirectory = Path.GetDirectoryName(matches[0])!;
            foreach (var required in app.RequiredPortableFiles)
                if (!File.Exists(Path.Combine(applicationDirectory, required)))
                    throw new InvalidDataException("The portable package is incomplete: " + required);
            cancellationToken.ThrowIfCancellationRequested();
            return matches[0];
        }
        catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
    }

    public async Task<PreparedPackage> PrepareManagedAsync(AppDefinition app, Release release, string action,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var asset = ReleaseSelection.ManagedPackage(app, release) ?? throw new InvalidOperationException("No supported package in this release.");
        var downloaded = await PrepareAsync(app, asset, progress, cancellationToken, extract: false);
        if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var directory = PreparedDirectory(app);
            try
            {
                await Task.Run(() => ExtractSafely(downloaded.DownloadPath, directory, cancellationToken), cancellationToken);
                var descriptorPath = Path.Combine(directory, "toolbox-lifecycle.json");
                if (!File.Exists(descriptorPath))
                {
                    var wrappers = Directory.GetDirectories(directory);
                    descriptorPath = wrappers.Length == 1 ? Path.Combine(wrappers[0], "toolbox-lifecycle.json") : "";
                }
                // Companion products and retained maintenance may have independent nested
                // descriptors. Only this package's root (or sole wrapper root) is authoritative.
                if (File.Exists(descriptorPath))
                {
                    var capabilities = LifecycleCapabilities.Load(Path.GetDirectoryName(descriptorPath)!, app.Id)!;
                    return new(downloaded.DownloadPath, capabilities.ResolveEntry(action).Path)
                    { Capabilities = capabilities, PayloadVersion = ReadPayloadVersion(app, capabilities, action) };
                }
                if (Directory.GetFiles(directory, "toolbox-lifecycle.json", SearchOption.AllDirectories)
                    .Any(path => OwnedPaths.Read<LifecycleCapabilities>(path).ProductId == app.Id))
                    throw new InvalidDataException("Package has no unambiguous top-level lifecycle descriptor.");
                var matches = app.SetupNames.SelectMany(name => Directory.GetFiles(directory, name, SearchOption.AllDirectories)).ToArray();
                if (matches.Length == 1) return new(downloaded.DownloadPath, matches[0]);
                if (matches.Length > 1) throw new InvalidDataException("Package contains ambiguous legacy setup executables.");
                // ZIP filenames predate lifecycle v1. Absent metadata is a legacy
                // release, not an invalid protocol advertisement; verify its official EXE.
                var legacy = ReleaseSelection.Installer(app, release);
                if (legacy is not null && legacy.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return await PrepareManagedAsync(app with { LifecycleAssetPatterns = [] }, release, action, progress, cancellationToken);
                throw new InvalidDataException("This package has no supported maintenance entry point. Open the project's releases.");
            }
            catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
        }
        var descriptorAssets = release.Assets.Where(a => a.Name == "toolbox-lifecycle.json").ToArray();
        if (descriptorAssets.Length == 0) return downloaded;
        if (descriptorAssets.Length != 1) throw new InvalidDataException("Release has ambiguous capability metadata.");
        var descriptorAsset = descriptorAssets[0];
        if (descriptorAsset.Size > 1024 * 1024) throw new InvalidDataException("Capability metadata exceeds its size limit.");
        var metadata = await PrepareAsync(app, descriptorAsset, null, cancellationToken, extract: false);
        var staged = PreparedDirectory(app);
        Directory.CreateDirectory(staged);
        File.Copy(downloaded.DownloadPath, Path.Combine(staged, asset.Name));
        File.Copy(metadata.DownloadPath, Path.Combine(staged, "toolbox-lifecycle.json"));
        var descriptor = LifecycleCapabilities.Load(staged, app.Id)!;
        var entry = descriptor.ResolveEntry(action);
        if (!string.Equals(entry.Path, Path.Combine(staged, asset.Name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The capability entry point is not the separately verified installer. Use the complete lifecycle ZIP.");
        return new(downloaded.DownloadPath, entry.Path)
        { Capabilities = descriptor, PayloadVersion = ReadPayloadVersion(app, descriptor, action) };
    }

    // Call only for the freshly extracted, digest-verified primary package. A release
    // tag or a companion application's version cannot establish the requested payload.
    private static string? ReadPayloadVersion(AppDefinition app, LifecycleCapabilities capabilities, string action)
    {
        if (action is not ("install" or "update")) return null;
        var payloads = app.ExecutableNames.SelectMany(name => Directory.GetFiles(capabilities.Root, name, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entry = capabilities.ResolveEntry(action);
        var path = payloads.Length == 1 ? payloads[0] : payloads.Length == 0 && !entry.PowerShell ? entry.Path : null;
        if (path is null) return null;
        OwnedPaths.CheckNoReparse(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        return AppVersion.Parse(version) is null ? null : version!.Trim().TrimStart('v', 'V').Split('+')[0];
    }

    private string PreparedDirectory(AppDefinition app) => Path.Combine(root, "Prepared", app.Id + "-" + Guid.NewGuid().ToString("N"));

    public static async Task<bool> VerifyAsync(string path, ReleaseAsset asset, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != asset.Size || asset.Digest is null) return false;
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        return string.Equals("sha256:" + hash, asset.Digest, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExtractSafely(string zipPath, string destination, CancellationToken cancellationToken)
    {
        OwnedPaths.CheckNoReparse(destination);
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
