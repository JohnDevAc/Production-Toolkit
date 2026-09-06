using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static class Program
{
    private static int count;
    private static readonly string Temporary = Path.Combine(Path.GetTempPath(), "ToolkitTests-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Temporary);
        try
        {
            RunAsync(args.Contains("--live")).GetAwaiter().GetResult();
            if (args.Contains("--ui")) ReviewUi(Path.GetFullPath(args.SkipWhile(a => a != "--ui").Skip(1).FirstOrDefault() ?? "artifacts/ui-review"));
            Console.WriteLine($"PASS: {count} assertions.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(Temporary, true); }
    }
    private static void Check(bool value, string label)
    { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS " + label); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    { try { await action(); } catch (T) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
    private static Release Release(string tag, bool dev = false, int day = 1) => new() { Tag = tag, Prerelease = dev, PublishedAt = new(2026, 9, day, 0, 0, 0, TimeSpan.Zero) };
    private static async Task RunAsync(bool live)
    {
        var stable = Release("v1.3.2"); var dev = Release("v1.4.0-dev.10", true);
        Check(AppVersion.Parse("1.3.2.0")!.CompareTo(AppVersion.Parse("v1.3.2")) == 0, "Windows version trailing zero matches release");
        Check(AppVersion.Parse("1.4.0-dev.10+abc123")!.CompareTo(AppVersion.Parse("1.4.0-dev.9")) > 0, "Numeric dev version ordering");
        Check(AppVersion.Parse("1.4.0")!.CompareTo(AppVersion.Parse("1.4.0-dev.10")) > 0, "Stable follows prerelease");
        Check(AppVersion.Parse("garbage") is null, "Unknown version stays unknown");
        Check(AppVersion.Parse("999999999999999.1.2") is null, "Version overflow rejected");
        Check(VersionStatus.Evaluate(true, "1.3.1", stable, ReleaseChannel.Stable) == UpdateState.UpdateAvailable, "Older installed offers update");
        Check(VersionStatus.Evaluate(true, "1.3.2.0", stable, ReleaseChannel.Stable) == UpdateState.Current, "Equal installed is current");
        Check(VersionStatus.Evaluate(true, "1.4.0", stable, ReleaseChannel.Stable) == UpdateState.NewerInstalled, "Newer installed is not downgraded silently");
        Check(VersionStatus.Evaluate(true, "1.3.2", dev, ReleaseChannel.Development) == UpdateState.SwitchChannel, "Stable to development is explicit switch");
        Check(VersionStatus.Evaluate(true, "1.4.0-dev.10", stable, ReleaseChannel.Stable) == UpdateState.SwitchChannel, "Development to stable is explicit switch");
        Check(VersionStatus.Evaluate(true, "unknown", stable, ReleaseChannel.Stable) == UpdateState.Unknown, "Unknown installed version never reports current");
        Check(VersionStatus.Evaluate(false, null, stable, ReleaseChannel.Stable) == UpdateState.NotInstalled, "Missing installation offers install");
        Check(VersionStatus.Evaluate(true, "1.3.2", null, ReleaseChannel.Development) == UpdateState.NoRelease, "No development release does not fall back to stable");
        var legacyDev = Release("v0.1.0-dev.7", false, 6);
        var draft = Release("v99.0.0", false, 6); draft.Draft = true;
        Check(ReleaseSelection.Latest([legacyDev, draft, stable], ReleaseChannel.Stable) == stable, "Stable excludes mislabeled dev tags and drafts");
        Check(ReleaseSelection.Latest([stable, legacyDev], ReleaseChannel.Development) == legacyDev, "Mislabeled prerelease recognized as development");
        Check(ReleaseSelection.Latest([Release("v1.0.0", day: 1), Release("v1.1.0", day: 4), Release("v1.0.5", day: 2)], ReleaseChannel.Stable)!.Tag == "v1.1.0", "Release ordering uses publication time");
        var pc = Catalog.Apps[3];
        var pcRelease = Release("v0.6.1");
        pcRelease.Assets = [new() { Name = "NDI-Configurator-PC-Agent-win-x64-framework-dependent.zip" }, new() { Name = "NDI-Configurator-PC-Agent-win-x64.zip" }];
        Check(ReleaseSelection.Installer(pc, pcRelease)!.Name == "NDI-Configurator-PC-Agent-win-x64.zip", "PC agent chooses complete self-contained package");
        Check(ReleaseSelection.Installer(Catalog.Apps[2], new Release { Assets = [new() { Name = "Resolume-Arena-Configurator-v0.3.5-win-x64-Setup.exe" }] }) is not null, "Resolume versioned installer recognized");
        Check(ReleaseSelection.Installer(Catalog.Apps[2], new Release { Assets = [new() { Name = "Resolume-Arena-Configurator-v0.3.4-win-x64.zip" }] }) is null, "Unsupported older portable release not mistaken for installer");
        var equalStable = Release("v0.3.5"); var equalDev = Release("v0.3.5-dev", true);
        equalStable.Assets = [new() { Name = "Resolume-Arena-Configurator-v0.3.5-win-x64-Setup.exe", Digest = "sha256:" + new string('a', 64) }];
        equalDev.Assets = [new() { Name = equalStable.Assets[0].Name, Digest = equalStable.Assets[0].Digest }];
        Check(VersionStatus.UsesEquivalentPackage(Catalog.Apps[2], "0.3.5", equalDev, [equalStable, equalDev]), "Identical stable/dev packages avoid a needless reinstall loop");
        equalDev.Assets[0].Digest = "sha256:" + new string('b', 64);
        Check(!VersionStatus.UsesEquivalentPackage(Catalog.Apps[2], "0.3.5", equalDev, [equalStable, equalDev]), "Different channel packages are not treated as equivalent");

        var pages = 0;
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            pages++;
            Check(request.Headers.UserAgent.Any(), "GitHub requests identify the application");
            var data = pages == 1 ? Enumerable.Range(0, 100).Select(_ => dev).ToArray() : [stable];
            return JsonResponse(data);
        })))
        {
            var result = await new GitHubClient(http).GetReleasesAsync(Catalog.Apps[0], default);
            Check(pages == 2 && ReleaseSelection.Latest(result.Releases, ReleaseChannel.Stable)?.Tag == stable.Tag, "Pagination finds stable behind 100 prereleases");
        }
        using (var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.Forbidden))))
            await ThrowsAsync<HttpRequestException>(() => new GitHubClient(http).GetReleasesAsync(Catalog.Apps[0], default), "GitHub rate limit becomes actionable error");

        var bytes = Encoding.UTF8.GetBytes("test installer payload");
        var asset = new ReleaseAsset { Id = 1, Name = "Kiloview-Environment-Setup.exe", Size = bytes.Length,
            DownloadUrl = "https://github.com/JohnDevAc/Kiloview-Environment-Setup/releases/download/v1.0.0/Kiloview-Environment-Setup.exe",
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) };
        var calls = 0;
        using (var http = new HttpClient(new FakeHandler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; })))
        {
            var service = new PackageService(http, Path.Combine(Temporary, "downloads"));
            var prepared = await service.PrepareAsync(Catalog.Apps[0], asset, null, default);
            Check(File.Exists(prepared.SetupPath), "Verified executable prepared without launching");
            await service.PrepareAsync(Catalog.Apps[0], asset, null, default);
            Check(calls == 1, "Verified download cache reused");
            File.WriteAllText(prepared.DownloadPath, "tampered");
            await service.PrepareAsync(Catalog.Apps[0], asset, null, default);
            Check(calls == 2 && await PackageService.VerifyAsync(prepared.DownloadPath, asset, default), "Tampered cache is downloaded and reverified");
        }
        var bad = new ReleaseAsset { Name = asset.Name, Size = asset.Size, DownloadUrl = asset.DownloadUrl, Digest = "sha256:" + new string('0', 64) };
        using (var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })))
        {
            var service = new PackageService(http, Path.Combine(Temporary, "bad-download"));
            await ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(Catalog.Apps[0], bad, null, default), "Checksum mismatch blocks installer");
            Check(!Directory.GetFiles(Path.Combine(Temporary, "bad-download"), "*.partial", SearchOption.AllDirectories).Any(), "Failed verification removes partial files");
            var cancel = new CancellationToken(true);
            await ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(Catalog.Apps[0], asset, null, cancel), "Cancellation stops download");
        }
        bad.DownloadUrl = "https://example.com/setup.exe";
        await ThrowsAsync<InvalidDataException>(() => { PackageService.ValidateSource(Catalog.Apps[0], bad); return Task.CompletedTask; }, "Untrusted download source rejected");

        foreach (var unsafeName in new[] { "../escape.exe", "nested/../../escape.exe", "C:/escape.exe", "dir/evil:stream", "dir/CON.exe", "dir/space /bad.exe" })
        {
            var archive = Path.Combine(Temporary, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(unsafeName).Open()); writer.Write("bad"); }
            await ThrowsAsync<InvalidDataException>(() => { PackageService.ExtractSafely(archive, Path.Combine(Temporary, Guid.NewGuid().ToString()), default); return Task.CompletedTask; }, "Unsafe ZIP entry rejected: " + unsafeName);
        }
        var complete = Path.Combine(Temporary, "complete.zip");
        using (var zip = ZipFile.Open(complete, ZipArchiveMode.Create))
        foreach (var name in new[] { "package/NDI Configurator PC Agent Setup.exe", "package/Agent/NDI Configurator PC Agent.exe", "package/LICENSE.md" })
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture"); }
        var zipped = File.ReadAllBytes(complete);
        var zipAsset = new ReleaseAsset { Name = "NDI-Configurator-PC-Agent-win-x64.zip", Size = zipped.Length,
            DownloadUrl = pc.RepositoryUrl + "/releases/download/v0.6.1/NDI-Configurator-PC-Agent-win-x64.zip", Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(zipped)) };
        using (var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(zipped) })))
        {
            var prepared = await new PackageService(http, Path.Combine(Temporary, "zip-download")).PrepareAsync(pc, zipAsset, null, default);
            Check(File.Exists(prepared.SetupPath) && File.Exists(Path.Combine(Path.GetDirectoryName(prepared.SetupPath)!, "Agent", "NDI Configurator PC Agent.exe")), "ZIP setup keeps its complete agent payload");
        }
        var local = new LocalState(Path.Combine(Temporary, "state"));
        local.Preferences.Channels["job"] = ReleaseChannel.Development; local.Save();
        Check(new LocalState(local.Root).Preferences.Channels["job"] == ReleaseChannel.Development, "Channel selection persists");
        local.SaveCache(Catalog.Apps[0], new([stable, dev], DateTimeOffset.Now));
        Check(local.LoadCache(Catalog.Apps[0])!.Releases.Count == 2, "Release cache works offline");
        var customApp = Catalog.Apps[2] with { KnownPaths = [], RegistryNames = [] };
        var executable = typeof(MainWindow).Assembly.Location;
        local.Preferences.LaunchPaths[customApp.Id] = executable;
        var finder = new InstallationService(local);
        Check(AppVersion.Parse(finder.Find(customApp)?.Version)?.CompareTo(AppVersion.Parse("1.0.0")) == 0, "Installed version read from binary metadata");
        local.Preferences.LaunchPaths[customApp.Id] = Path.Combine(Temporary, "missing.exe");
        Check(finder.Find(customApp) is null, "Missing saved path does not report installed");
        File.WriteAllText(Path.Combine(local.Root, "settings.json"), "{broken");
        Check(new LocalState(local.Root).Warning is not null && Directory.GetFiles(local.Root, "settings.json.*.bak").Length == 1, "Damaged settings preserved and recovered");
        if (live) await LiveAsync();
    }

    private static async Task LiveAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var github = new GitHubClient(http);
        var packages = new PackageService(http, Path.Combine(Temporary, "live"));
        foreach (var app in Catalog.Apps)
        {
            var snapshot = await github.GetReleasesAsync(app, default);
            foreach (var channel in new[] { ReleaseChannel.Stable, ReleaseChannel.Development })
            {
                var release = ReleaseSelection.Latest(snapshot.Releases, channel);
                Check(release is not null, $"Live {app.Name}: {channel} release found ({release?.Tag})");
                var asset = ReleaseSelection.Installer(app, release!);
                Check(asset is not null, $"Live {app.Name}: {channel} installer recognized");
                PackageService.ValidateSource(app, asset!);
                if (channel == ReleaseChannel.Stable)
                {
                    var package = await packages.PrepareAsync(app, asset!, null, default);
                    Check(File.Exists(package.SetupPath), $"Live {app.Name}: installer downloaded, verified and prepared; NOT executed");
                    Console.WriteLine("  Binary version: " + InstallationService.ReadVersion(package.SetupPath));
                }
            }
        }
    }

    private static void ReviewUi(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App(); app.InitializeComponent();
        var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        var window = new MainWindow(Path.Combine(Temporary, "ui"), true);
        var tags = new[] { "v1.3.2", "v0.8.7", "v0.3.5", "v0.6.1-dev.1" };
        var installed = new string?[] { "1.3.1.0", "0.8.7", null, "0.6.1-dev.1" };
        for (var i = 0; i < window.Cards.Count; i++)
        {
            var card = window.Cards[i];
            var release = Release(tags[i], i == 3);
            release.Assets = [new() { Name = i switch { 0 => "Kiloview-Environment-Setup.exe", 1 => "NDI-Job-Configurator.exe", 2 => "Resolume-Arena-Configurator-v0.3.5-win-x64-Setup.exe", _ => "NDI-Configurator-PC-Agent-win-x64.zip" }, Size = i == 0 ? 1275904 : 147000000 }];
            card.Snapshot = new([release], new(2026, 9, 6, 12, 30, 0, TimeSpan.Zero));
            card.Installed = installed[i] is null ? null : new("C:\\Example\\app.exe", installed[i]);
            card.Offline = false; card.Channel = i == 3 ? ReleaseChannel.Development : ReleaseChannel.Stable;
        }
        var root = (FrameworkElement)window.Content;
        window.Content = null; root.DataContext = window;
        var widerFit = WindowSizing.Select(new Size(1400, 900), new Size(16, 40), w => w >= 1180 ? 800 : 1000);
        Check(widerFit.Width == 1200 && widerFit.Height <= 900, "Startup sizing widens the window when that avoids scrolling");
        Check(typeof(MainWindow).Assembly.GetManifestResourceNames().Contains("ToolkitLauncher.LICENSE.md"), "Non-commercial licence embedded in application");
        foreach (var (area, scale, label, mustFit) in new[]
        {
            (new Size(1920, 1040), 1d, "1080p-100", true),
            (new Size(2560, 1400), 1.5, "4k-150", true),
            (new Size(1920, 1040), 2d, "4k-200", true),
            (new Size(1536, 832), 2.5, "4k-250", false),
            (new Size(800, 560), 2d, "small-desktop-200", false)
        })
        {
            var chrome = new Size(16, 40);
            var launchSize = window.MeasureStartupSize(area, chrome);
            var client = new Size(launchSize.Width - chrome.Width, launchSize.Height - chrome.Height);
            root.Measure(client); root.Arrange(new Rect(client)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var scroll = Descendants<ScrollViewer>(root).First();
            Console.WriteLine($"  Startup bounds {launchSize.Width:0} × {launchSize.Height:0} DIP; client {client}; scroll extent {scroll.ScrollableHeight:0.0}; root desired {root.DesiredSize}");
            Check(launchSize.Width <= area.Width && launchSize.Height <= area.Height, "Startup " + label + ": window fits work area");
            if (mustFit) Check(scroll.ScrollableHeight < 1 && scroll.ScrollableWidth < 1, "Startup " + label + ": all four apps visible without scrollbars");
            else Check(scroll.ScrollableHeight > 0 && scroll.ScrollableWidth < 1, "Startup " + label + ": scrolling retained only for limited desktop space");
            Check(!Descendants<Button>(root).Any(b => b.Content?.ToString()?.Contains("Downloads folder") == true), "Startup " + label + ": downloads-folder link removed");
            Check(Descendants<TextBlock>(root).Any(t => t.Text == "© 2026 John Lightfoot · Free for non-commercial use."), "Startup " + label + ": copyright and licence visible");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.Width * scale), (int)Math.Ceiling(client.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, "startup-" + label + ".png")); encoder.Save(file);
        }
        foreach (var (width, height, scale, label) in new[] { (1120, 1120, 1d, "100"), (1120, 1120, 1.5, "150"), (1120, 1120, 2d, "200"), (1120, 1120, 2.5, "250"), (720, 780, 1.5, "narrow-150"), (640, 480, 2.5, "small-250"), (640, 640, 2d, "busy-offline-200") })
        {
            if (label.StartsWith("busy"))
            {
                window.Cards[0].Busy = true; window.Cards[0].Progress = 48;
                window.Cards[0].Activity = "Downloading · 48.0 / 100.0 MB";
                window.Cards[1].Offline = true;
                window.Cards[1].Activity = "Could not check releases. GitHub is limiting release checks. Try again after 14:00.";
                foreach (var card in window.Cards) card.Recompute();
            }
            window.SetLayoutWidth(width);
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var cards = Descendants<Border>(root).Where(b => b.DataContext is AppCard && b.CornerRadius.TopLeft == 10).ToArray();
            Check(cards.Length == 4, "UI " + label + ": four cards rendered");
            Check(cards.Select(c => Math.Round(c.ActualWidth, 2)).Distinct().Count() == 1, "UI " + label + ": equal card widths");
            foreach (var card in cards)
            {
                var buttons = Descendants<Button>(card).Where(b => b.IsVisible || b.Visibility == Visibility.Visible).Where(b => b.ActualWidth > 0).ToArray();
                for (var i = 0; i < buttons.Length; i++)
                {
                    var bounds = buttons[i].TransformToAncestor(card).TransformBounds(new Rect(buttons[i].RenderSize));
                    if (bounds.Left < -1 || bounds.Right > card.ActualWidth + 1) throw new Exception("Button outside card at " + label);
                    for (var j = i + 1; j < buttons.Length; j++)
                    {
                        var other = buttons[j].TransformToAncestor(card).TransformBounds(new Rect(buttons[j].RenderSize));
                        var intersection = Rect.Intersect(bounds, other);
                        if (!intersection.IsEmpty && intersection.Width > 1 && intersection.Height > 1) throw new Exception("Overlapping buttons at " + label);
                    }
                }
            }
            Check(true, "UI " + label + ": action buttons stay within cards without overlaps");
            foreach (var image in Descendants<Image>(root))
                if (image.Source is not BitmapSource bitmapSource || bitmapSource.PixelWidth < 64) throw new Exception("Missing or low resolution app icon");
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, "toolkit-" + label + ".png")); encoder.Save(file);
            var scroll = Descendants<ScrollViewer>(root).First();
            if (width < 1000)
            {
                scroll.ScrollToEnd(); root.UpdateLayout();
                Check(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "UI " + label + ": last card remains reachable by scrolling");
                var bottom = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bottom.Render(root);
                var bottomEncoder = new PngBitmapEncoder(); bottomEncoder.Frames.Add(BitmapFrame.Create(bottom));
                using var bottomFile = File.Create(Path.Combine(output, "toolkit-" + label + "-bottom.png")); bottomEncoder.Save(bottomFile);
                scroll.ScrollToTop(); root.UpdateLayout();
            }
        }
        PresentationTraceSources.DataBindingSource.Flush();
        Check(!bindingErrors.ToString().Contains("Error:"), "UI has no WPF binding errors");
        window.Close();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static HttpResponseMessage JsonResponse(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(data, GitHubClient.JsonOptions), Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
