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
    private static readonly Dictionary<string, BitmapSource> LiveIcons = [];
    private static EnvironmentSnapshot? LiveEnvironment;
    private static readonly string Temporary = Path.Combine(Path.GetTempPath(), "ToolkitTests-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Temporary);
        try
        {
            RunAsync(args.Contains("--live")).GetAwaiter().GetResult();
            GitHubCheckTestsAsync().GetAwaiter().GetResult();
            IconTestsAsync(args.Contains("--live-icons")).GetAwaiter().GetResult();
            ToolkitUpdateTestsAsync().GetAwaiter().GetResult();
            EnvironmentTests();
            if (args.Contains("--environment"))
            {
                LiveEnvironment = EnvironmentStatusService.ReadAsync(default).GetAwaiter().GetResult();
                Console.WriteLine("Local environment: " + JsonSerializer.Serialize(LiveEnvironment, GitHubClient.JsonOptions));
            }
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
            await ThrowsAsync<HttpRequestException>(() => new GitHubClient(http).GetReleasesAsync(Catalog.Apps[0], default), "GitHub access errors remain visible");

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
        Check(AppVersion.Parse(finder.Find(customApp)?.Version)?.CompareTo(AppVersion.Parse(SelfUpdateService.CurrentVersion)) == 0, "Installed version read from binary metadata");
        var replaceable = Path.Combine(Temporary, "internally-updated.exe");
        File.Copy(executable, replaceable);
        local.Preferences.LaunchPaths[customApp.Id] = replaceable;
        var before = finder.Find(customApp);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), replaceable, true);
        var after = finder.Find(customApp);
        Check(before?.Path == after?.Path && before?.Version != after?.Version && after?.Version == InstallationService.ReadVersion(replaceable),
            "An internal update replacing the same executable is detected on the next check");
        var environment = Catalog.Apps[0] with { KnownPaths = [replaceable], RegistryNames = [] };
        var saved = new SetupRecord(executable, "v1.0.0", "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))));
        Check(InstallationService.Find(environment, null, saved)?.Path == replaceable,
            "Older retained setup does not mask an externally updated persistent Environment launcher");
        File.Delete(replaceable);
        Check(InstallationService.Find(environment, null, saved)?.IsSavedSetup == true, "Verified Environment setup remains a launch fallback");
        Check(finder.Find(customApp) is null, "An externally removed app is no longer marked installed");
        local.Preferences.LaunchPaths[customApp.Id] = Path.Combine(Temporary, "missing.exe");
        Check(finder.Find(customApp) is null, "Missing saved path does not report installed");
        File.WriteAllText(Path.Combine(local.Root, "settings.json"), "{broken");
        Check(new LocalState(local.Root).Warning is not null && Directory.GetFiles(local.Root, "settings.json.*.bak").Length == 1, "Damaged settings preserved and recovered");
        if (live) await LiveAsync();
    }

    private static async Task IconTestsAsync(bool live)
    {
        static BitmapSource Solid(byte red, byte green, byte blue, byte alpha = 255)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, green, red, alpha }, 4);
            bitmap.Freeze(); return bitmap;
        }
        static byte[] Png(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        var red = Solid(230, 40, 60); var blue = Solid(20, 130, 235);
        var redTheme = IconTheme.FromIcon(red); var blueTheme = IconTheme.FromIcon(blue);
        Check(redTheme.Primary.Color.R > redTheme.Primary.Color.B && blueTheme.Primary.Color.B > blueTheme.Primary.Color.R,
            "Card palettes follow the icon's dominant colour");
        foreach (var icon in new[] { red, blue, Solid(250, 220, 30), Solid(255, 255, 255), Solid(255, 0, 0, 0) })
        {
            var palette = IconTheme.FromIcon(icon);
            Check(IconTheme.Contrast(palette.Primary.Color, Colors.White) >= 7 &&
                IconTheme.Contrast(palette.Primary.Color, palette.Background.Color) >= 4.5,
                "Icon palette keeps primary and secondary button text readable");
        }
        Check(IconTheme.FromIcon(Solid(255, 0, 0, 0)).Primary.Color == IconTheme.FromIcon(Solid(255, 255, 255)).Primary.Color,
            "Transparent and monochrome icons use a consistent neutral fallback");
        var card = new AppCard(Catalog.Apps[0]);
        var changes = new List<string?>(); card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        card.UpdateIcons(red, blue);
        Check(ReferenceEquals(card.Icon, red), "Installed icon takes precedence over the repository icon");
        var previous = card.Theme.Primary.Color;
        card.UpdateIcons(blue, red);
        Check(card.Theme.Primary.Color != previous && changes.Contains("Icon") && changes.Contains("Theme"),
            "A replaced icon refreshes both image and colour bindings");
        card.UpdateIcons(null, red);
        Check(ReferenceEquals(card.Icon, red), "Removing an installation restores its repository icon");

        var nativePath = Path.Combine(Temporary, "icon-update.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), nativePath);
        var native = IconService.ReadInstalled(nativePath);
        Check(native is { IsFrozen: true, PixelWidth: > 0 }, "Installed executable icon extracted without launching it");
        File.Copy(typeof(MainWindow).Assembly.Location, nativePath, true);
        var replacement = IconService.ReadInstalled(nativePath);
        Check(replacement is null || !Png(native!).SequenceEqual(Png(replacement)), "Icon reread detects a binary replaced at the same path");
        File.Delete(nativePath);
        Check(IconService.ReadInstalled(nativePath) is null, "Removed executable icon is not retained by a shell cache");

        var requests = 0;
        var redBytes = Png(red); var blueBytes = Png(blue);
        using var http = new HttpClient(new FakeHandler(request =>
        {
            requests++;
            Check(request.RequestUri!.AbsoluteUri.Contains("raw.githubusercontent.com/JohnDevAc/Kiloview-Environment-Setup/v1.0.0/assets/setup.ico"),
                "Remote icon comes from the selected upstream release");
            if (requests == 2)
            {
                Check(request.Headers.IfNoneMatch.Any(t => t.Tag == "\"first\""), "Cached icon revalidated with its ETag");
                return new(HttpStatusCode.NotModified);
            }
            if (requests == 4) return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            if (requests == 5) return new(HttpStatusCode.ServiceUnavailable);
            if (requests == 6)
            {
                var oversized = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                oversized.Content.Headers.ContentLength = 6 * 1024 * 1024; return oversized;
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(requests == 1 ? redBytes : blueBytes) };
            response.Headers.ETag = new(requests == 1 ? "\"first\"" : "\"second\""); return response;
        }));
        var service = new IconService(http, Path.Combine(Temporary, "icons"));
        for (var i = 1; i <= 6; i++)
        {
            // Recreate the service to verify cache survival across application sessions.
            if (i == 5) service = new(http, Path.Combine(Temporary, "icons"));
            var icon = await service.ReadRemoteAsync(Catalog.Apps[0], "v1.0.0", default);
            Check(icon is { IsFrozen: true } && IconTheme.FromIcon(icon).Primary.Color == (i <= 2 ? redTheme : blueTheme).Primary.Color,
                "Remote icon refresh/cache survives response " + i + " (new, unchanged, changed, corrupt, offline, oversized)");
        }
        if (live)
        {
            using var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var liveIcons = new IconService(liveHttp, Path.Combine(Temporary, "live-icons"));
            foreach (var definition in Catalog.Apps)
            {
                var snapshot = await new GitHubClient(liveHttp).GetReleasesAsync(definition, default);
                foreach (var channel in new[] { ReleaseChannel.Stable, ReleaseChannel.Development })
                {
                    var release = ReleaseSelection.Latest(snapshot.Releases, channel)!;
                    var icon = await liveIcons.ReadRemoteAsync(definition, release.Tag, default);
                    Check(icon is { PixelWidth: >= 64 }, $"Live {definition.Name}: {channel} {release.Tag} icon is available at full resolution");
                    LiveIcons[definition.Id + channel] = icon!;
                }
            }
        }
    }

    private static async Task ToolkitUpdateTestsAsync()
    {
        var payload = File.ReadAllBytes(typeof(MainWindow).Assembly.Location);
        var version = SelfUpdateService.CurrentVersion;
        var release = Release("v" + version);
        var asset = new ReleaseAsset
        {
            Name = $"Production-Toolkit-{version}-win-x64-Setup.exe", Size = payload.Length,
            DownloadUrl = $"https://github.com/JohnDevAc/Production-Toolkit/releases/download/v{version}/Production-Toolkit-{version}-win-x64-Setup.exe",
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload))
        };
        release.Assets = [asset];
        Check(ToolkitUpdates.Available("1.1.0", [release])?.Installer == asset, "Toolkit updater selects the version-matched Windows installer");
        Check(ToolkitUpdates.Available(version + "+build", [release]) is null, "Same toolkit version never prompts for reinstallation");
        Check(ToolkitUpdates.Available("99.0.0", [release]) is null, "Toolkit updater never downgrades a newer installation");
        release.Prerelease = true;
        Check(ToolkitUpdates.Available("1.1.0", [release]) is null, "Toolkit updater excludes prereleases");
        release.Prerelease = false; release.Draft = true;
        Check(ToolkitUpdates.Available("1.1.0", [release]) is null, "Toolkit updater excludes drafts");
        release.Draft = false;
        var portable = Release(release.Tag); portable.Assets = [new() { Name = "Production.Toolkit.exe" }];
        Check(ToolkitUpdates.Available("1.1.0", [portable]) is null, "A portable executable cannot be mistaken for a toolkit installer");
        var wrong = Release(release.Tag); wrong.Assets = [new() { Name = "Production-Toolkit-9.0.0-win-x64-Setup.exe" }];
        Check(ToolkitUpdates.Available("1.1.0", [wrong]) is null, "An installer for a different release is not offered");
        var originalUrl = asset.DownloadUrl;
        asset.DownloadUrl = "https://example.com/setup.exe";
        await ThrowsAsync<InvalidDataException>(() => { ToolkitUpdates.Available("1.1.0", [release]); return Task.CompletedTask; }, "Toolkit self-update rejects installers outside its GitHub repository");
        asset.DownloadUrl = originalUrl;
        using var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new SelfUpdateService(new GitHubClient(http), http, Path.Combine(Temporary, "toolkit-updates"));
        var installer = await service.DownloadAsync(new(release, asset), null, default);
        Check(File.Exists(installer), "Toolkit installer is fully downloaded and verified before handoff");
        await ThrowsAsync<InvalidDataException>(() => { SelfUpdateService.ValidateInstallerVersion(installer, "99.0.0"); return Task.CompletedTask; }, "Installer binary version must match the offered release");
        var info = SelfUpdateService.StartInfo(Path.Combine(Temporary, "folder with spaces & symbols", "Setup.exe"));
        Check(!info.UseShellExecute && info.ArgumentList.Contains("/NORESTART") && info.ArgumentList.Contains("/TOOLKITUPDATE=1") &&
            info.ArgumentList.Last() == "/LOG=" + info.FileName + ".log", "Installer handoff quotes paths safely, requests app relaunch, and prevents Windows reboot");
        Check(MaintenanceSession.RequestShutdown() == 2, "Maintenance command exits quietly when no instance is running");
    }

    private static async Task GitHubCheckTestsAsync()
    {
        var time = new ManualTime { Now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero) };
        var cachePath = Path.Combine(Temporary, "api-cache.json");
        var calls = 0;
        using var http = new HttpClient(new FakeHandler(_ => { calls++; return JsonResponse(new[] { Release("v1.0.0") }); }));
        var client = new GitHubClient(http, cachePath, time);
        var fresh = await client.GetReleasesAsync(Catalog.Apps[0], default);
        var restarted = new GitHubClient(http, cachePath, time);
        var cached = await restarted.GetReleasesAsync(Catalog.Apps[0], default);
        Check(calls == 1 && !fresh.IsCached && cached.IsCached && cached.CheckedAt == fresh.CheckedAt,
            "Rapid restarts reuse releases without changing their checked time");
        await restarted.GetReleasesAsync(Catalog.Apps[0], default, userRequested: true);
        Check(calls == 1, "Repeated clicks within one minute do not repeat API requests");
        time.Now += TimeSpan.FromMinutes(2);
        await restarted.GetReleasesAsync(Catalog.Apps[0], default);
        Check(calls == 1, "Startup reuses release information younger than ten minutes");
        await restarted.GetReleasesAsync(Catalog.Apps[0], default, userRequested: true);
        Check(calls == 2, "An explicit check can refresh the startup cache after one minute");
        time.Now += TimeSpan.FromMinutes(10);
        Check(!(await restarted.GetReleasesAsync(Catalog.Apps[0], default)).IsCached && calls == 3,
            "Startup refreshes releases once the cached check expires");
        File.WriteAllText(cachePath, "{damaged");
        await new GitHubClient(http, cachePath, time).GetReleasesAsync(Catalog.Apps[0], default);
        Check(calls == 4, "An unreadable API cache does not prevent a check");

        var limitPath = Path.Combine(Temporary, "api-limit.json");
        var reset = time.Now + TimeSpan.FromMinutes(10);
        var limitedCalls = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var limitedHttp = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            if (++limitedCalls > 1) return JsonResponse(new[] { Release("v2.0.0") });
            received.SetResult();
            await releaseResponse.Task.WaitAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
            return response;
        }));
        var limitedClient = new GitHubClient(limitedHttp, limitPath, time);
        var checks = Catalog.Apps.Append(ToolkitUpdates.Application)
            .Select(app => limitedClient.GetReleasesAsync(app, default, userRequested: true)).ToArray();
        await received.Task;
        Check(limitedCalls == 1, "Release requests are serialized across all five applications");
        releaseResponse.SetResult();
        foreach (var check in checks)
            await ThrowsAsync<HttpRequestException>(async () => await check, "A rate limit pauses the shared check queue");
        Check(limitedCalls == 1, "A blocked check makes no further GitHub API requests");
        var resumed = new GitHubClient(limitedHttp, limitPath, time);
        time.Now += TimeSpan.FromMinutes(9);
        await ThrowsAsync<HttpRequestException>(() => resumed.GetReleasesAsync(Catalog.Apps[0], default, true),
            "The primary reset deadline survives a restart and overrides a shorter Retry-After");
        Check(limitedCalls == 1, "Repeated startup and manual checks respect the persisted block");
        time.Now = reset;
        Check(limitedCalls == 1, "Expiry alone does not start an automatic retry");
        var recovered = await resumed.GetReleasesAsync(Catalog.Apps[0], default, true);
        Check(limitedCalls == 2 && recovered.Releases[0].Tag == "v2.0.0" && !recovered.IsCached,
            "The next requested check succeeds after GitHub's reset");

        foreach (var useDate in new[] { false, true })
        {
            var retryCalls = 0;
            using var retryHttp = new HttpClient(new FakeHandler(_ =>
            {
                retryCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = useDate ? new(time.Now + TimeSpan.FromMinutes(3)) : new(TimeSpan.FromMinutes(3));
                return response;
            }));
            var retryClient = new GitHubClient(retryHttp, clock: time);
            await ThrowsAsync<HttpRequestException>(() => retryClient.GetReleasesAsync(Catalog.Apps[0], default), "A secondary rate limit is reported");
            time.Now += TimeSpan.FromMinutes(2);
            await ThrowsAsync<HttpRequestException>(() => retryClient.GetReleasesAsync(Catalog.Apps[1], default), "Retry-After prevents another repository request");
            Check(retryCalls == 1, "Retry-After " + (useDate ? "date" : "seconds") + " is respected");
        }

        var permissionCalls = 0;
        using var permissionHttp = new HttpClient(new FakeHandler(_ => ++permissionCalls == 1
            ? new(HttpStatusCode.Forbidden) : JsonResponse(Array.Empty<Release>())));
        var permissionClient = new GitHubClient(permissionHttp, clock: time);
        await ThrowsAsync<HttpRequestException>(() => permissionClient.GetReleasesAsync(Catalog.Apps[0], default), "Permission errors remain visible");
        await permissionClient.GetReleasesAsync(Catalog.Apps[1], default);
        Check(permissionCalls == 2, "A repository permission error does not block other repositories");

        var quotaCalls = 0;
        using var quotaHttp = new HttpClient(new FakeHandler(_ =>
        {
            quotaCalls++;
            var response = JsonResponse(Array.Empty<Release>());
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", (time.Now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds().ToString());
            return response;
        }));
        var quotaClient = new GitHubClient(quotaHttp, clock: time);
        await quotaClient.GetReleasesAsync(Catalog.Apps[0], default);
        await ThrowsAsync<HttpRequestException>(() => quotaClient.GetReleasesAsync(Catalog.Apps[1], default), "The last allowed response pauses subsequent requests");
        Check(quotaCalls == 1, "An exhausted successful response does not wait for a 403 before backing off");
    }

    private static EnvironmentEvidence FullEnvironment() => new()
    {
        Configuration = true, Distro = true, DistroRunning = true, Container = true, ContainerRunning = true,
        ContainerImage = "kiloview/klnk-pro:latest", WebResponding = true, Watchdog = true, WatchdogRunning = true,
        NdiTools = true, NdiVersion = "6.3.2.0", Discovery = true, DiscoveryRunning = true, DiscoveryListening = true
    };
    private static EnvironmentEvidence EmptyEnvironment() => new()
    {
        Configuration = false, Distro = false, DistroRunning = false, Container = false, ContainerRunning = false,
        Watchdog = false, WatchdogRunning = false, NdiTools = false, Discovery = false, DiscoveryRunning = false, DiscoveryListening = false
    };
    private static void EnvironmentTests()
    {
        var now = DateTimeOffset.Now;
        var full = FullEnvironment();
        var complete = EnvironmentSnapshot.From(full, now);
        Check(complete.State == EnvironmentInstallationState.Full && complete.Components.Count == 3, "A complete environment reports all three installed components");
        Check(complete.Components[0].Status.Contains("running") && complete.Components[2].Status.Contains("listening"), "Environment feedback includes running and listening state");
        full.ContainerRunning = false; full.DiscoveryRunning = false;
        var stopped = EnvironmentSnapshot.From(full, now);
        Check(stopped.State == EnvironmentInstallationState.Full && stopped.Components[0].Status.Contains("stopped") && stopped.Components[2].Status.Contains("stopped"),
            "Stopped services remain installed and their stopped state stays visible");
        var missing = FullEnvironment(); missing.Discovery = false;
        Check(EnvironmentSnapshot.From(missing, now).State == EnvironmentInstallationState.Partial, "A missing Discovery component makes the environment partially installed");
        var noWatchdog = FullEnvironment(); noWatchdog.Watchdog = false;
        Check(EnvironmentSnapshot.From(noWatchdog, now).State == EnvironmentInstallationState.Partial, "A missing startup watchdog prevents fully installed status");
        var stoppedWsl = FullEnvironment(); stoppedWsl.DistroRunning = false; stoppedWsl.Container = null; stoppedWsl.ContainerRunning = null;
        var unknown = EnvironmentSnapshot.From(stoppedWsl, now);
        Check(unknown.State == EnvironmentInstallationState.Unknown && unknown.Components[0].Status.Contains("WSL stopped"),
            "A stopped WSL distribution is not mistaken for a missing container");
        Check(EnvironmentSnapshot.From(new(), now).State == EnvironmentInstallationState.Unknown, "Unreadable environment evidence never claims a missing installation");
        var empty = EnvironmentSnapshot.From(EmptyEnvironment(), now);
        Check(empty.State == EnvironmentInstallationState.NotInstalled && !empty.AnyInstalled, "No environment components means not installed");
        var resume = FullEnvironment(); resume.RestartPending = true;
        Check(EnvironmentSnapshot.From(resume, now).State == EnvironmentInstallationState.Partial, "A pending setup continuation is reported as partial");
        var badPort = FullEnvironment(); badPort.DiscoveryListening = false;
        Check(EnvironmentSnapshot.From(badPort, now).Components[2].Status.Contains("not listening"), "A running Discovery process without its listener is not reported ready");
        var card = new AppCard(Catalog.Apps[0])
        {
            Installed = new("C:\\Downloads\\setup.exe", "1.3.2", true),
            Snapshot = new([Release("v1.3.2")], now), EnvironmentStatus = empty
        };
        Check(card.Status == "Downloaded · up to date" && card.EnvironmentSummary == "Environment · Not installed",
            "A current download never implies that the environment is installed");
        Check(card.ShowCompact && card.CanInstall && card.InstallText == "Install", "A current cached setup can install a completely missing environment");
        card.EnvironmentStatus = EnvironmentSnapshot.From(missing, now);
        Check(!card.ShowCompact && card.CanInstall && card.InstallText == "Complete setup", "Partial environments keep their details and can complete setup from the cached executable");
        card.EnvironmentStatus = complete;
        Check(!card.ShowCompact && !card.CanInstall && card.CanLaunch, "A fully installed current environment offers its existing setup launcher");
        card.Installed = new("C:\\Downloads\\setup.exe", "1.3.1", true);
        Check(card.Status == "Downloaded · out of date" && card.EnvironmentStatus.State == EnvironmentInstallationState.Full,
            "An outdated setup download is independent of complete environment installation");
        var absent = new AppCard(Catalog.Apps[1]);
        Check(absent.ShowCompact && !absent.CanLaunch, "Uninstalled applications use the simple install card");
        absent.Installed = new("C:\\Programs\\app.exe", "1.0.0");
        Check(!absent.ShowCompact, "Detected applications retain their detailed cards");
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
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        InstallerRefreshTests();
        ReviewUpdatePrompt(output);
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
            if (card.Definition.IsEnvironment) card.EnvironmentStatus = LiveEnvironment ?? EnvironmentSnapshot.From(FullEnvironment(), DateTimeOffset.Now);
            card.Offline = false; card.Channel = i == 3 ? ReleaseChannel.Development : ReleaseChannel.Stable;
            if (LiveIcons.TryGetValue(card.Definition.Id + card.Channel, out var icon)) card.UpdateIcons(null, icon);
        }
        var root = (FrameworkElement)window.Content;
        window.Content = null; root.DataContext = window;
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
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
            Check(!Descendants<Button>(root).Any(b => b.Content?.ToString() is "Download only" or "Locate app" or "Releases ↗"), "Startup " + label + ": extra card links removed");
            Check(Descendants<TextBlock>(root).Any(t => t.Text == "© 2026 John Lightfoot · Proprietary · Free for non-commercial use."), "Startup " + label + ": proprietary copyright and licence visible");
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
                window.Cards[1].Activity = "Could not check releases. GitHub checks are paused until 16:04. Saved releases remain available. Check again afterward.";
                foreach (var card in window.Cards) card.Recompute();
            }
            window.SetLayoutWidth(width);
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var cards = Descendants<Border>(root).Where(b => b.DataContext is AppCard && b.CornerRadius.TopLeft == 10).ToArray();
            Check(cards.Length == 4, "UI " + label + ": four cards rendered");
            Check(cards.Select(c => Math.Round(c.ActualWidth, 2)).Distinct().Count() == 1, "UI " + label + ": equal card widths");
            if (label == "100")
            {
                var firstCard = window.Cards[0]; var originalIcon = firstCard.Icon;
                var originalColour = firstCard.Theme.Primary.Color;
                var controls = Descendants<Control>(cards[0]).ToArray();
                Rect Bounds(Control control) => control.TransformToAncestor(cards[0]).TransformBounds(new Rect(control.RenderSize));
                var before = controls.Select(Bounds).ToArray();
                firstCard.UpdateIcons(null, window.Cards[2].Icon);
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                Check(controls.Select(Bounds).SequenceEqual(before), "Changing an icon and its palette preserves every control's bounds");
                Check(firstCard.Theme.Primary.Color != originalColour &&
                    ((SolidColorBrush)Descendants<Button>(cards[0]).First().Background).Color == firstCard.Theme.Primary.Color,
                    "Rendered buttons immediately adopt the changed icon's palette");
                firstCard.UpdateIcons(null, originalIcon);
                root.UpdateLayout();
            }
            foreach (var card in cards)
            {
                var buttons = Descendants<Button>(card).Where(Rendered).ToArray();
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
            var compact = cards.Single(c => ((AppCard)c.DataContext).Definition.Id == "resolume");
            Check(Descendants<Button>(compact).Count(Rendered) == 1 && Descendants<Button>(compact).Single(Rendered).Content?.ToString() == "Install",
                "UI " + label + ": uninstalled card has one visible Install button");
            Check(Descendants<Image>(compact).Single(Rendered).ActualWidth == 96, "UI " + label + ": uninstalled card shows its large icon");
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
        foreach (var scenario in new[] { "partial-environment", "unverified-environment", "nothing-installed", "setup-download-only" })
        {
            foreach (var card in window.Cards) { card.Busy = false; card.Activity = ""; card.Offline = false; }
            var environment = window.Cards[0];
            var evidence = FullEnvironment();
            if (scenario == "partial-environment") evidence.Discovery = false;
            else if (scenario == "unverified-environment") { evidence.DistroRunning = false; evidence.Container = null; evidence.ContainerRunning = null; }
            else
            {
                evidence = EmptyEnvironment();
                foreach (var card in window.Cards) card.Installed = null;
                if (scenario == "setup-download-only") environment.Installed = new("C:\\Downloads\\setup.exe", "1.3.2", true);
            }
            environment.EnvironmentStatus = EnvironmentSnapshot.From(evidence, DateTimeOffset.Now);
            foreach (var card in window.Cards) card.Recompute();
            foreach (var label in Descendants<TextBlock>(root)) label.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            foreach (var scale in new[] { 1d, 2d, 2.5d })
            {
                var size = window.MeasureStartupSize(new Size(1920, 1040), new Size(16, 40));
                var client = new Size(size.Width - 16, size.Height - 40);
                root.Measure(client); root.Arrange(new Rect(client)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                var cards = Descendants<Border>(root).Where(b => b.DataContext is AppCard && b.CornerRadius.TopLeft == 10).ToArray();
                Check(Math.Abs(cards[0].ActualHeight - cards[1].ActualHeight) < 1 && Math.Abs(cards[2].ActualHeight - cards[3].ActualHeight) < 1,
                    scenario + " " + scale + ": each row has symmetrical card heights");
                foreach (var card in cards)
                {
                    var view = (AppCard)card.DataContext;
                    var buttons = Descendants<Button>(card).Where(Rendered).ToArray();
                    if (view.ShowCompact && (buttons.Length != 1 || buttons[0].Content?.ToString() != "Install"))
                        throw new Exception("Unexpected action on an uninstalled card.");
                    foreach (var control in Descendants<FrameworkElement>(card).Where(c => c is TextBlock or Button or Image).Where(Rendered))
                    {
                        var bounds = control.TransformToAncestor(card).TransformBounds(new Rect(control.RenderSize));
                        if (bounds.Left < -1 || bounds.Right > card.ActualWidth + 1 || bounds.Top < -1 || bounds.Bottom > card.ActualHeight + 1)
                            throw new Exception("Environment or compact-card content outside card: " + scenario);
                    }
                }
                Check(Descendants<ScrollViewer>(root).First().ScrollableHeight < 1, scenario + " " + scale + ": all cards fit at startup");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.Width * scale), (int)Math.Ceiling(client.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, scenario + "-" + scale * 100 + ".png")); encoder.Save(file);
            }
        }
        PresentationTraceSources.DataBindingSource.Flush();
        Check(!bindingErrors.ToString().Contains("Error:"), "UI has no WPF binding errors");
        window.Close();
    }

    private static void InstallerRefreshTests()
    {
        var window = new MainWindow(Path.Combine(Temporary, "installer-refresh"), true);
        window.Cards.Clear();
        var paths = new[] { Path.Combine(Temporary, "installed-job.exe"), Path.Combine(Temporary, "installed-agent.exe") };
        for (var i = 0; i < paths.Length; i++)
        {
            var definition = Catalog.Apps[i == 0 ? 1 : 3] with { KnownPaths = [paths[i]], RegistryNames = [] };
            var release = Release("v" + InstallationService.ReadVersion(typeof(MainWindow).Assembly.Location));
            release.Assets = [new() { Name = i == 0 ? "NDI-Job-Configurator.exe" : "NDI-Configurator-PC-Agent-win-x64.zip", Size = 100 }];
            window.Cards.Add(new AppCard(definition) { Snapshot = new([release], DateTimeOffset.UtcNow), Offline = true });
        }
        var snapshot = window.Cards[0].Snapshot;
        var run = typeof(MainWindow).GetMethod("RunSetupAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        void RunFixture(bool remove)
        {
            var source = Path.Combine(Temporary, remove ? "remove-fixture.cs" : "install-fixture.cs");
            var executable = Path.ChangeExtension(source, ".exe");
            var code = "using System.IO; class Fixture { static int Main() { " + string.Join(" ", paths.Select(path => remove
                ? "File.Delete(" + JsonSerializer.Serialize(path) + ");"
                : "File.Copy(" + JsonSerializer.Serialize(typeof(MainWindow).Assembly.Location) + ", " + JsonSerializer.Serialize(path) + ", true);"))
                + " return " + (remove ? "1" : "0") + "; } }";
            File.WriteAllText(source, code);
            var compiler = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + executable, source }) compiler.ArgumentList.Add(argument);
            using (var build = Process.Start(compiler)!) { build.WaitForExit(); Check(build.ExitCode == 0, "Harmless installer fixture compiles"); }
            var task = (Task)run.Invoke(window, [window.Cards[0], executable])!;
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        try
        {
            Check(window.Cards.All(card => card.Installed is null && card.CanInstall), "Missing local apps initially offer installation");
            RunFixture(false);
            Check(window.Cards.All(card => card.Installed is not null && card.State == UpdateState.Current && card.CanLaunch && !card.CanInstall),
                "Installer exit refreshes every affected card, version and action automatically");
            Check(ReferenceEquals(snapshot, window.Cards[0].Snapshot) && window.Cards.All(card => card.Offline),
                "Post-install detection retains release cache and online-check state");
            RunFixture(true);
            Check(window.Cards.All(card => card.Installed is null && card.CanInstall && !card.CanLaunch),
                "Maintenance removal refreshes cards even when setup exits with an error");
            Check(window.Cards[0].Activity.Contains("code 1") && !window.Cards[0].InstallerRunning,
                "Refresh retains the installer failure and clears its running state");
        }
        finally { window.Close(); }
    }

    private static void ReviewUpdatePrompt(string output)
    {
        using var http = new HttpClient();
        foreach (var scale in new[] { 1d, 1.5, 2d, 2.5 })
        {
            var prompt = new UpdateWindow(new SelfUpdateService(new GitHubClient(http), http, Temporary), new(Release("v1.4.0"), new ReleaseAsset()));
            var root = (FrameworkElement)prompt.Content; prompt.Content = null; root.DataContext = prompt;
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, prompt.FontFamily);
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, prompt.FontSize);
            root.Measure(new Size(524, double.PositiveInfinity));
            var size = new Size(524, root.DesiredSize.Height);
            root.Arrange(new Rect(size)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.Measure(new Size(524, double.PositiveInfinity));
            size = new Size(524, root.DesiredSize.Height);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var buttons = Descendants<Button>(root).ToArray();
            var bounds = buttons.Select(b => b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize))).ToArray();
            Check(buttons.Length == 2 && Math.Abs(buttons[0].ActualWidth - buttons[1].ActualWidth) < 1 &&
                !bounds[0].IntersectsWith(bounds[1]) && bounds.All(b => b.Left >= 0 && b.Right <= size.Width),
                $"Update prompt at {scale * 100:0}%: equal buttons, no overlaps or clipping");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"update-prompt-{scale * 100:0}.png")); encoder.Save(file);
            prompt.Close();
        }
    }
    private static bool Rendered(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        // UI review renders a detached visual tree, where IsVisible is false even
        // for drawn controls. Honour collapsed ancestors, not just local Visibility.
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is UIElement visual && visual.Visibility != Visibility.Visible) return false;
        return true;
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
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
