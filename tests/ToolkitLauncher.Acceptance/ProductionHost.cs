using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using ToolkitLauncher.Core;

namespace ToolkitLauncher.Acceptance;

// Own Application startup without deriving from the production x:Class. Calling the inherited
// App.InitializeComponent on a subclass loads BAML against the wrong component assembly.
internal sealed class QaApplication : Application
{
    public QaApplication() => Resources = ProductionResources.Load();
    public bool StartupReached { get; private set; }
    protected override void OnStartup(StartupEventArgs e) { StartupReached = true; base.OnStartup(e); }
}

internal static class ProductionHost
{
    internal static QaApplication CreateApplication()
    {
        return new QaApplication { ShutdownMode = ShutdownMode.OnMainWindowClose };
    }
    private static readonly (string Name, Type Type)[] Seams =
    [
        ("apiHttp", typeof(HttpClient)), ("iconHttp", typeof(HttpClient)), ("downloadHttp", typeof(HttpClient)),
        ("discoveryHttp", typeof(HttpClient)), ("github", typeof(GitHubClient)), ("packages", typeof(PackageService)),
        ("icons", typeof(IconService)), ("updater", typeof(SelfUpdateService)), ("lifecycle", typeof(LifecycleCoordinator))
    ];

    public static void CheckSeams()
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version != "1.4.0+" + CandidateManifest.SourceCommit)
            throw new InvalidDataException("QA host requires production source " + CandidateManifest.SourceCommit + " / 1.4.0; actual: " + version);
        foreach (var (name, type) in Seams)
            if (Field(name).FieldType != type) throw new InvalidDataException("Production test seam changed: " + name);
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidDataException("Missing production test seam: " + name);

    private static void Set(MainWindow window, string name, object value)
    {
        var field = Field(name);
        var old = field.GetValue(window);
        field.SetValue(window, value);
        if (!ReferenceEquals(field.GetValue(window), value)) throw new InvalidDataException("Could not isolate QA service: " + name);
        if (old is HttpClient client) client.Dispose();
    }

    internal static void ConfigureTransports(MainWindow window, LocalState local, HttpClient api, Action<string> log)
    {
        var download = new HttpClient(new RefuseNetwork(log));
        var icon = new HttpClient(new RefuseNetwork(log));
        var discovery = new HttpClient(new RefuseNetwork(log));
        Set(window, "apiHttp", api); Set(window, "downloadHttp", download);
        Set(window, "iconHttp", icon); Set(window, "discoveryHttp", discovery);
        var github = new GitHubClient(api); // Never reuse stale public snapshots or shared rate limits.
        Set(window, "github", github);
        Set(window, "packages", new PackageService(download, local.DownloadRoot));
        Set(window, "icons", new IconService(icon, Path.Combine(local.Root, "Icons")));
        Set(window, "updater", new SelfUpdateService(github, download, Path.Combine(local.Root, "Updates")));
    }

    public static int Run(CandidateManifest manifest, string manifestPath, VmEvidence guard)
    {
        CheckSeams();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Production Toolkit QA", manifest.Id.ToString("D"));
        OwnedPaths.CheckNoReparse(root);
        Directory.CreateDirectory(root);
        using var hostLock = new FileStream(Path.Combine(root, ".qa-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var evidence = Path.Combine(root, "Evidence", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        File.Copy(manifestPath, Path.Combine(evidence, "acceptance.json"));
        using var logFile = new StreamWriter(Path.Combine(evidence, "qa-host.log")) { AutoFlush = true };
        var logLock = new object();
        void Log(string text) { lock (logLock) { logFile.WriteLine(DateTimeOffset.UtcNow.ToString("o") + " " + text); } }
        Console.WriteLine("QA host: real OS actions on " + guard.Computer + ". Evidence: " + evidence);
        Log("Guard passed. Real OS actions enabled. Production binary acceptance remains separate.");
        var stage = "prepare QA state and pinned cache";
        return LaunchFailure.Capture(evidence, () => stage, Log, () =>
        {
        var local = new LocalState(root);
        manifest.SeedAsync(Path.GetDirectoryName(manifestPath)!, local.DownloadRoot).GetAwaiter().GetResult();
        foreach (var package in manifest.Packages)
            local.Preferences.Channels[package.Product] = AppVersion.Parse(package.Version)!.IsPrerelease ? ReleaseChannel.Development : ReleaseChannel.Stable;
        local.Save();
        var sessionPath = Path.Combine(evidence, "session.json");
        File.WriteAllText(sessionPath, JsonSerializer.Serialize(new
        {
            Harness = "Toolkit.QA 1.4.0-qa.12", ProductionSourceCommit = CandidateManifest.SourceCommit,
            ProductionBinaryAcceptance = false, StartedUtc = DateTimeOffset.UtcNow, Guard = guard, StateRoot = root,
            ManifestSha256 = Hash(manifestPath),
            Assemblies = new[] { typeof(ProductionHost).Assembly, typeof(MainWindow).Assembly, typeof(PackageService).Assembly }
                .Select(a => new { Name = a.GetName().Name, Sha256 = Hash(a.Location) }),
            Packages = manifest.Packages
        }, OwnedPaths.Json));

        stage = "load production WPF resources into QA Application";
        var app = CreateApplication();
        stage = "construct production MainWindow";
        var window = new MainWindow(root, false, false);
        if (window.PreviewMode) throw new InvalidOperationException("Preview mode is forbidden for acceptance.");
        stage = "isolate manifest feed and network transports";
        ConfigureTransports(window, local, new HttpClient(new ManifestFeed(manifest, Log)), Log);
        foreach (var card in window.Cards.ToArray())
        {
            var package = manifest.Packages.SingleOrDefault(p => p.Product == card.Definition.Id);
            if (package is null) { window.Cards.Remove(card); continue; }
            card.Snapshot = new([CandidateManifest.Release(package)], DateTimeOffset.UtcNow);
            card.Offline = true;
            card.Recompute();
        }
        window.Title = "Production Toolkit — QA acceptance";
        app.MainWindow = window;
        void Snapshot() => File.WriteAllText(Path.Combine(evidence, "cards.json"), JsonSerializer.Serialize(window.Cards.Select(c => new
        {
            Product = c.Definition.Id, c.Status, c.Activity, c.OperationStatus, c.OperationPending,
            InstalledPath = c.Installed?.Path, InstalledVersion = c.Installed?.Version,
            SelectedVersion = c.SelectedRelease?.Tag, c.CanInstall, c.CanLaunch, c.CanUninstall, c.CanRecheck
        }), OwnedPaths.Json));
        var lifecycle = (LifecycleCoordinator)Field("lifecycle").GetValue(window)!;
        var previousOperations = "";
        lifecycle.Changed += () =>
        {
            var status = JsonSerializer.Serialize(lifecycle.Operations.Select(o => new { o.Request.OperationId, o.Request.ProductId, o.Request.Action, o.Message, o.Pending }));
            if (status != previousOperations) { previousOperations = status; Log("Lifecycle: " + status); Snapshot(); }
        };
        window.Loaded += async (_, _) => { await window.RefreshAsync(); Snapshot(); Log("Initial production refresh completed using the pinned manifest feed."); };
        window.Closed += (_, _) => { Snapshot(); Log("Window closed. Retain Operations for any pending installer or reboot recovery."); };
        app.DispatcherUnhandledException += (_, e) =>
        {
            LaunchFailure.Save(evidence, "WPF dispatcher", e.Exception);
            Log("Unhandled UI failure: " + e.Exception);
        };
        using var bindingLog = new TextWriterTraceListener(Path.Combine(evidence, "wpf-bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        stage = "run production window on QA dispatcher";
        try { return app.Run(window); }
        finally { PresentationTraceSources.DataBindingSource.Flush(); PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog); }
        });
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
