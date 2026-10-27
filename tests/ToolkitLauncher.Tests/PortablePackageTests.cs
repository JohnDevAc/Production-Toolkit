using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static readonly AppDefinition TestCardDefinition = Catalog.Apps.Single(a => a.Id == "testcard");

    private static byte[] TestCardZip(string? missing = null, bool duplicate = false, bool traversal = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var name in TestCardDefinition.RequiredPortableFiles.Prepend("TestCard.exe").Append("LICENSE"))
            {
                if (name == missing) continue;
                using var entry = zip.CreateEntry("TestCard-win-x64/" + name).Open();
                entry.Write(name == "TestCard.exe" ? File.ReadAllBytes(typeof(MainWindow).Assembly.Location) : [1, 2, 3]);
            }
            if (duplicate) zip.CreateEntry("another/TestCard.exe");
            if (traversal) zip.CreateEntry("../escaped.exe");
        }
        return memory.ToArray();
    }

    private static ReleaseAsset TestCardAsset(byte[] bytes) => new()
    {
        Name = "TestCard-1.0.0-win-x64.zip", Size = bytes.Length,
        Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)),
        DownloadUrl = "https://github.com/JohnDevAc/TestCardGen/releases/download/v1.0.0/TestCard-1.0.0-win-x64.zip"
    };

    private static async Task NewApplicationPackageTestsAsync()
    {
        var udp = Catalog.Apps.Single(a => a.Id == "udp-to-ndi");
        Check(udp.PackageKind == PackageKind.Executable && ReleaseSelection.Installer(udp,
            new Release { Assets = [new() { Name = "UDP-to-NDI-Setup-1.0.3-x64.exe.sha256" }, new() { Name = "UDP-to-NDI-Setup-1.0.3-x64.exe" }] })?.Name == "UDP-to-NDI-Setup-1.0.3-x64.exe",
            "UDP to NDI selects the published x64 installer rather than its checksum");
        Check(ReleaseSelection.Installer(udp, new Release { Assets = [new() { Name = "UDP-to-NDI-Setup-1.0.3-arm64.exe" }] }) is null,
            "UDP to NDI rejects other architectures");
        Check(TestCardDefinition.PackageKind == PackageKind.PortableZip && TestCardDefinition.SetupNames.Length == 0 &&
            ReleaseSelection.Installer(TestCardDefinition, new Release { Assets = [new() { Name = "TestCard-1.0.0-win-x64.zip.sha256" }, new() { Name = "TestCard-1.0.0-win-x64.zip" }] })?.Name == "TestCard-1.0.0-win-x64.zip",
            "TestCard selects its full portable ZIP and has no setup executable");
        var state = new LocalState(Path.Combine(Temporary, "portable-state"));
        var bytes = TestCardZip();
        using var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var packages = new PackageService(http, state.DownloadRoot);
        var asset = TestCardAsset(bytes);
        var first = await packages.InstallPortableAsync(TestCardDefinition, asset, state.ApplicationsRoot, null, default);
        var folder = Path.GetDirectoryName(first)!;
        Check(File.Exists(Path.Combine(folder, ".runtime", "dotnet.exe")) && File.Exists(Path.Combine(folder, "TestCard.Worker.exe")) && File.Exists(Path.Combine(folder, "LICENSE")),
            "Portable extraction keeps the hidden runtime, worker and accompanying files together");
        var service = new InstallationService(state);
        var installed = service.RegisterPortable(TestCardDefinition, first);
        Check(new InstallationService(new LocalState(state.Root)).Find(TestCardDefinition)?.Path == first && installed.Version is not null,
            "Portable launch location and executable version survive Toolkit restart");
        var second = await packages.InstallPortableAsync(TestCardDefinition, asset, state.ApplicationsRoot, null, default);
        Check(first != second && File.Exists(first), "Portable update stages a fresh folder without replacing a previous or running copy");
        var settings = Path.Combine(state.Root, "Receipts", TestCardDefinition.Id + ".json");
        var before = File.ReadAllBytes(settings);
        var saveFailed = false;
        using (var locked = File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { service.RegisterPortable(TestCardDefinition, second); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveFailed = true; }
        }
        Check(saveFailed, "Portable receipt-write failure is reported");
        Check(service.Find(TestCardDefinition)?.Path == first && File.ReadAllBytes(settings).SequenceEqual(before),
            "Failed activation preserves the previous launch path and durable inventory");
        foreach (var broken in new[] { TestCardZip(".runtime/dotnet.exe"), TestCardZip("TestCard.Worker.exe"), TestCardZip(duplicate: true), TestCardZip(traversal: true) })
        {
            var prior = Directory.GetDirectories(Path.Combine(state.ApplicationsRoot, TestCardDefinition.Id));
            using var brokenHttp = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(broken) }));
            await ThrowsAsync<InvalidDataException>(() => new PackageService(brokenHttp, state.DownloadRoot)
                .InstallPortableAsync(TestCardDefinition, TestCardAsset(broken), state.ApplicationsRoot, null, default),
                "Incomplete, ambiguous or unsafe portable ZIP is rejected");
            Check(Directory.GetDirectories(Path.Combine(state.ApplicationsRoot, TestCardDefinition.Id)).SequenceEqual(prior) && service.Find(TestCardDefinition)?.Path == first,
                "Failed extraction removes its incomplete folder and preserves the installed copy");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => packages.InstallPortableAsync(TestCardDefinition, asset, state.ApplicationsRoot, null, cancelled.Token),
            "Cancelled portable install never activates a replacement");
    }

    private static void PortableInstallFlowTests()
    {
        var root = Path.Combine(Temporary, "portable-ui");
        var bytes = TestCardZip();
        var asset = TestCardAsset(bytes);
        var state = new LocalState(root);
        // Feed the real UI path a verified cached package; any network access fails.
        var cache = Path.Combine(state.DownloadRoot, TestCardDefinition.Id, asset.Digest![7..].ToLowerInvariant());
        Directory.CreateDirectory(cache); File.WriteAllBytes(Path.Combine(cache, asset.Name), bytes);
        using var http = new HttpClient(new FakeHandler(_ => throw new IOException("Unexpected network access")));
        var window = new MainWindow(root, true);
        typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new PackageService(http, state.DownloadRoot));
        var card = window.Cards.Single(c => c.Definition.Id == TestCardDefinition.Id);
        var version = InstallationService.ReadVersion(typeof(MainWindow).Assembly.Location);
        var release = Release("v" + version, AppVersion.Parse(version)?.IsPrerelease == true);
        card.Channel = release.IsDevelopment ? ReleaseChannel.Development : ReleaseChannel.Stable;
        release.Assets = [asset]; card.Snapshot = new([release], DateTimeOffset.UtcNow);
        try
        {
            var task = (Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!;
            var frame = new DispatcherFrame(); task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            Check(card.Installed is not null && card.CanLaunch && !card.ShowPrimary && !card.InstallerRunning && card.Activity == "",
                "Portable Install registers the app and enables Launch without executing it as an installer");
            Check(card.Installed!.Path.StartsWith(state.ApplicationsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Portable applications are kept separately from the download cache");
        }
        finally { window.Close(); }
    }
}
