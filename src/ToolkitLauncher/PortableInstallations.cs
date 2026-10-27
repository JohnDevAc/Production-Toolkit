using System.Diagnostics;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed record PortableReceipt(int SchemaVersion, string ProductId, string ActiveExecutable, string[] Copies, ReleaseAsset? Package)
{
    public Dictionary<string, long>? Files { get; init; }
}
public sealed record PortableOwnership(int SchemaVersion, string ProductId, string Executable);

public sealed class PortableInstallations(LocalState state)
{
    private string ReceiptPath(AppDefinition app) => OwnedPaths.Resolve(Path.Combine(state.Root, "Receipts"), app.Id + ".json", false);

    public IDisposable AcquireLock(AppDefinition app)
    {
        var root = Path.Combine(state.Root, "PortableLocks");
        if (!Directory.Exists(root)) OwnedPaths.CreatePrivateDirectory(root);
        OwnedPaths.ValidatePrivateDirectory(root);
        return new FileStream(OwnedPaths.Resolve(root, app.Id + ".lock", false), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PortableReceipt? Read(AppDefinition app)
    {
        var path = ReceiptPath(app);
        if (!File.Exists(path)) return null;
        var receipt = OwnedPaths.Read<PortableReceipt>(path);
        if (receipt.SchemaVersion != 1 || receipt.ProductId != app.Id || receipt.Copies is null || receipt.Copies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Copies.Length)
            throw new InvalidDataException("Portable installation receipt is invalid. Review the retained files before retrying.");
        var productRoot = OwnedPaths.Resolve(state.ApplicationsRoot, app.Id, false);
        if (Directory.Exists(productRoot))
        {
            var discovered = new List<string>(receipt.Copies);
            foreach (var directory in Directory.EnumerateDirectories(productRoot, "package-*"))
            {
                var copy = Path.GetRelativePath(state.ApplicationsRoot, directory);
                ValidateCopy(app, copy);
                var markerPath = OwnedPaths.Resolve(directory, ".toolkit-owned.json", false);
                if (!File.Exists(markerPath)) continue;
                var marker = OwnedPaths.Read<PortableOwnership>(markerPath);
                if (marker.SchemaVersion != 1 || marker.ProductId != app.Id) throw new InvalidDataException("Portable ownership marker differs.");
                OwnedPaths.Resolve(directory, marker.Executable, false);
                if (!discovered.Contains(copy, StringComparer.OrdinalIgnoreCase)) discovered.Add(copy);
            }
            receipt = receipt with { Copies = discovered.ToArray() };
        }
        foreach (var copy in receipt.Copies) ValidateCopy(app, copy);
        if (receipt.ActiveExecutable.Length > 0)
        {
            var executable = OwnedPaths.Resolve(state.ApplicationsRoot, receipt.ActiveExecutable, false);
            if (!receipt.Copies.Any(copy => executable.StartsWith(OwnedPaths.Resolve(state.ApplicationsRoot, copy, false) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The active portable app is outside its owned copies.");
        }
        if (receipt.Files is not null)
            foreach (var (file, length) in receipt.Files)
            {
                if (length < 0 || receipt.ActiveExecutable.Length == 0) throw new InvalidDataException("Invalid portable file inventory.");
                OwnedPaths.Resolve(Path.GetDirectoryName(OwnedPaths.Resolve(state.ApplicationsRoot, receipt.ActiveExecutable, false))!, file, false);
            }
        return receipt;
    }

    private string ValidateCopy(AppDefinition app, string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Length != 2 || segments[0] != app.Id || !segments[1].StartsWith("package-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(segments[1][8..], "N", out _)) throw new InvalidDataException("Portable ownership is outside its expected package directory.");
        return OwnedPaths.Resolve(state.ApplicationsRoot, relative, false);
    }

    public PortableReceipt Activate(AppDefinition app, string executable, ReleaseAsset? package)
    {
        var relative = Path.GetRelativePath(state.ApplicationsRoot, executable);
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < 3) throw new InvalidDataException("Portable app is outside Toolkit's application directory.");
        var copy = Path.Combine(parts[0], parts[1]);
        var directory = ValidateCopy(app, copy);
        OwnedPaths.CheckTree(directory);
        if (!File.Exists(executable) || Readiness(app, executable).Length > 0) throw new InvalidDataException("Portable app is incomplete.");
        var previous = Read(app);
        OwnedPaths.Write(Path.Combine(directory, ".toolkit-owned.json"), new PortableOwnership(1, app.Id, Path.GetRelativePath(directory, executable)));
        var files = Directory.EnumerateFiles(Path.GetDirectoryName(executable)!, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != ".toolkit-owned.json")
            .ToDictionary(path => Path.GetRelativePath(Path.GetDirectoryName(executable)!, path), path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);
        var receipt = new PortableReceipt(1, app.Id, relative, (previous?.Copies ?? []).Append(copy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), package) { Files = files };
        // This atomic receipt is the activation point. UI preferences are not the installation inventory.
        OwnedPaths.Write(ReceiptPath(app), receipt);
        return receipt;
    }

    public string? ActivePath(AppDefinition app) => Read(app) is { ActiveExecutable.Length: > 0 } receipt
        ? OwnedPaths.Resolve(state.ApplicationsRoot, receipt.ActiveExecutable, false) : null;

    public static string Readiness(AppDefinition app, string executable)
    {
        try
        {
            OwnedPaths.CheckNoReparse(executable);
            var root = Path.GetDirectoryName(executable)!;
            var missing = app.RequiredPortableFiles.Prepend(Path.GetFileName(executable)).Where(name => !File.Exists(OwnedPaths.Resolve(root, name, false))).ToArray();
            return missing.Length == 0 ? "" : "Repair required: missing " + string.Join(", ", missing) + ".";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return "Portable files could not be verified. " + error.Message; }
    }

    public string InventoryReadiness(AppDefinition app, string executable)
    {
        var receipt = Read(app);
        if (receipt?.Files is null) return "";
        foreach (var (relative, length) in receipt.Files)
        {
            var path = OwnedPaths.Resolve(Path.GetDirectoryName(executable)!, relative, false);
            if (!File.Exists(path) || new FileInfo(path).Length != length) return "Repair required: a packaged file is missing or damaged (" + relative + ").";
        }
        return "";
    }

    public void AdoptLegacy(AppDefinition app, string? executable)
    {
        if (Read(app) is not null || string.IsNullOrWhiteSpace(executable)) return;
        // Only an existing saved launch path in our exact package-GUID layout is eligible.
        // Other folders are never adopted or deleted.
        var relative = Path.GetRelativePath(state.ApplicationsRoot, executable);
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < 3) throw new InvalidDataException("Legacy portable copy is outside Toolkit's owned package layout.");
        var copy = Path.Combine(parts[0], parts[1]);
        var directory = ValidateCopy(app, copy);
        OwnedPaths.CheckTree(directory);
        if (!app.ExecutableNames.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unrecognized portable executable.");
        OwnedPaths.Write(Path.Combine(directory, ".toolkit-owned.json"), new PortableOwnership(1, app.Id, Path.GetRelativePath(directory, executable)));
        OwnedPaths.Write(ReceiptPath(app), new PortableReceipt(1, app.Id, relative, [copy], null));
    }

    public void Inspect(AppDefinition app)
    {
        var receipt = Read(app);
        if (receipt is null) return;
        foreach (var copy in receipt.Copies)
        {
            var directory = ValidateCopy(app, copy);
            if (!Directory.Exists(directory)) continue;
            ValidateRemoval(app, directory);
        }
    }

    private static void ValidateRemoval(AppDefinition app, string directory)
    {
        OwnedPaths.CheckTree(directory);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) return; // Interrupted after the ownership marker was removed.
        var marker = OwnedPaths.Read<PortableOwnership>(Path.Combine(directory, ".toolkit-owned.json"));
        if (marker.SchemaVersion != 1 || marker.ProductId != app.Id) throw new InvalidDataException("Portable ownership marker differs.");
        OwnedPaths.Resolve(directory, marker.Executable, false);
    }

    private static void DeleteOwnedTree(string directory, bool packageRoot = false)
    {
        OwnedPaths.CheckNoReparse(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => Path.GetFileName(path) == ".toolkit-owned.json" ? 1 : 0))
        {
            if (packageRoot && Path.GetFileName(entry) == ".toolkit-owned.json") continue;
            OwnedPaths.CheckNoReparse(entry);
            if (Directory.Exists(entry)) DeleteOwnedTree(entry);
            else File.Delete(entry);
        }
        if (packageRoot)
        {
            var marker = Path.Combine(directory, ".toolkit-owned.json");
            OwnedPaths.CheckNoReparse(marker);
            if (File.Exists(marker)) File.Delete(marker);
        }
        Directory.Delete(directory, false);
    }

    public void Remove(AppDefinition app, Func<string, bool?>? isRunning = null)
    {
        var receipt = Read(app) ?? throw new InvalidOperationException("No owned portable receipt. Repair the legacy copy to register it before removal.");
        isRunning ??= IsRunning;
        var copies = receipt.Copies.Select(copy => (Relative: copy, Path: ValidateCopy(app, copy))).ToArray();
        // Validate every owned tree and running process before deleting any copy.
        foreach (var copy in copies.Where(c => Directory.Exists(c.Path)))
        {
            ValidateRemoval(app, copy.Path);
            if (isRunning(copy.Path) != false) throw new IOException("Close Test Card Generator and its worker before uninstalling. No process will be stopped automatically.");
        }
        foreach (var copy in copies)
        {
            if (Directory.Exists(copy.Path)) DeleteOwnedTree(copy.Path, true);
            var activeRemoved = receipt.ActiveExecutable.StartsWith(copy.Relative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            receipt = receipt with
            {
                Copies = receipt.Copies.Where(c => c != copy.Relative).ToArray(),
                ActiveExecutable = activeRemoved ? "" : receipt.ActiveExecutable,
                Files = activeRemoved ? null : receipt.Files
            };
            OwnedPaths.Write(ReceiptPath(app), receipt); // Partial removal remains retryable; unrelated settings stay intact.
        }
    }

    private static bool? IsRunning(string directory)
    {
        foreach (var name in new[] { "TestCard", "TestCard.Worker" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null) return null;
                    if (path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
            }
        }
        return false;
    }
}
