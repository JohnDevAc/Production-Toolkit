using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void RequestedVersionTests()
    {
        foreach (var scenario in new[] { "retained-stable", "no-change", "other-prerelease", "retained-dev", "matched-dev", "matched-native", "missing-version", "missing-primary", "missing-components", "not-installed", "implicit", "inspect", "repair", "uninstall", "cancelled", "running" })
        {
            var coordinator = new LifecycleCoordinator(Path.Combine(Temporary, "requested-version", scenario));
            var action = scenario is "inspect" or "repair" or "uninstall" ? scenario : "update";
            var requested = scenario == "implicit" ? null : scenario == "matched-native" ? "3.1.0" : scenario == "retained-dev" ? "0.7.5" : "0.7.5-dev.1";
            var op = coordinator.Begin("pc-agent", action, "protocol", requested);
            op.ExitCode = 0;
            var version = scenario switch
            {
                "matched-dev" => "0.7.5-dev.1+source123", "matched-native" => "3.1.0.0",
                "other-prerelease" => "0.7.5-dev.2", "retained-dev" or "missing-primary" => "0.7.5-dev.1", "missing-version" => null, _ => "0.7.5"
            };
            var result = Result(op, scenario is "no-change" or "cancelled" or "running" ? scenario : "succeeded") with
            {
                Components = JsonSerializer.SerializeToElement(new[] { new
                {
                    productId = scenario == "missing-primary" ? "job" : "pc-agent",
                    installed = scenario != "not-installed", version, channel = scenario == "matched-dev" ? "Development" : "Production"
                } })
            };
            if (scenario == "missing-components") result = result with { Components = null };
            OwnedPaths.Write(op.Request.ResultPath, result);
            var original = File.ReadAllText(op.Request.ResultPath);
            coordinator.Store.Reconcile(op);
            var mismatch = scenario is "retained-stable" or "no-change" or "other-prerelease" or "retained-dev" or "missing-version" or "missing-primary" or "missing-components" or "not-installed";
            Check(op.RequiresReview == mismatch, scenario + ": requested payload outcome is verified independently of owner success");
            Check(File.ReadAllText(op.Request.ResultPath) == original && JsonSerializer.Serialize(op.Result, OwnedPaths.Json) == JsonSerializer.Serialize(result, OwnedPaths.Json),
                scenario + ": original owner wire evidence is retained");
            Check(!coordinator.Store.Reconcile(op), scenario + ": repeated result reconciliation is stable");
            if (mismatch)
            {
                Check(op.Pending && coordinator.MutationBlocked && op.Message.Contains(requested!) && op.Message.Contains("Recheck"),
                    scenario + ": mismatch gives requested-version guidance and blocks another mutation");
                if (scenario is "retained-stable" or "no-change")
                    Check(op.Message.Contains("0.7.5") && op.Message.Contains("reported"), scenario + ": retained installed version is visible");
                var reopened = new LifecycleCoordinator(coordinator.Store.Root);
                Check(reopened.Operations.Single().RequiresReview && reopened.MutationBlocked, scenario + ": requested-version review survives reopening");
                var inspection = coordinator.Begin("pc-agent", "inspect", "protocol");
                OwnedPaths.Write(inspection.Request.ResultPath, Result(inspection) with { SafeToRetry = true, RecoveryRequired = false });
                coordinator.Store.Reconcile(inspection);
                coordinator.ReleaseAfterInspect("pc-agent", inspection);
                Check(!coordinator.MutationBlocked && op.ReconciledBy == inspection.Request.OperationId && op.Result?.Outcome == result.Outcome && coordinator.Operations.Count == 2,
                    scenario + ": explicit safe inspection releases the lock without rewriting success or retrying mutation");
            }
            else Check(op.Pending == (scenario == "running"), scenario + ": matching, implicit and non-install outcomes retain existing semantics");
        }
    }

    private static void PackageVersionRoutingTests()
    {
        var root = Path.Combine(Temporary, "package-version-routing"); Directory.CreateDirectory(root);
        string Binary(string version)
        {
            var path = Path.Combine(root, version + ".exe");
            var source = Path.ChangeExtension(path, ".cs");
            File.WriteAllText(source, "using System; [assembly: System.Reflection.AssemblyFileVersion(\"0.7.5.0\")] " +
                "[assembly: System.Reflection.AssemblyInformationalVersion(\"" + version + "\")] class Fixture { static void Main() {} }");
            var compiler = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + path, source }) compiler.ArgumentList.Add(argument);
            using var process = Process.Start(compiler)!;
            Check(process.WaitForExit(15000) && process.ExitCode == 0, "Harmless payload fixture compiles at " + version);
            return path;
        }
        var stable = Binary("0.7.5"); var development = Binary("0.7.5-dev.1+payload123");
        var previousState = File.Exists(AgentStatePath) ? File.ReadAllText(AgentStatePath) : null;
        File.WriteAllText(AgentStatePath, ValidAgentState);
        try
        {
            foreach (var scenario in new[] { "retained-stable", "matched-dev", "fresh-install", "native-entry", "native-only", "unknown-payload", "ambiguous-primary", "repair-downgrade" })
            {
                var directory = Path.Combine(root, scenario); var installed = Path.Combine(directory, "installed"); Directory.CreateDirectory(installed);
                var executable = Path.Combine(installed, "NDI Configurator PC Agent.exe");
                var setup = Path.Combine(installed, "NDI Configurator PC Agent Setup.exe");
                if (scenario != "fresh-install") { File.Copy(stable, executable); File.Copy(stable, setup); }
                var definition = Catalog.Apps.Single(a => a.IsPcAgent) with { KnownPaths = [executable], RegistryNames = [], MaintenanceDirectories = [] };
                var source = Path.Combine(directory, "package"); var capture = Path.Combine(directory, "captured.json");
                var expected = scenario == "matched-dev" || scenario == "fresh-install" ? "0.7.5-dev.1+payload123" : "0.7.5";
                var body = EmitSuccess.Replace("message='Fixture success'", "message='Owner reported completion';components=@(@{productId='pc-agent';installed=$true;version='" + expected + "';channel='" + (expected.Contains("dev") ? "Development" : "Production") + "'})")
                    .Replace("ConvertTo-Json)", "ConvertTo-Json -Depth 6)") + "\nCopy-Item -LiteralPath $LifecycleRequestPath -Destination " + PsQuote(capture);
                if (scenario is "matched-dev" or "fresh-install")
                    body = "Copy-Item -LiteralPath " + PsQuote(development) + " -Destination " + PsQuote(executable) + " -Force\n" +
                        "Copy-Item -LiteralPath " + PsQuote(development) + " -Destination " + PsQuote(setup) + " -Force\n" + body;
                FixtureAdapter(source, "pc-agent", body);
                // The primary payload is versioned; the harmless script models owner behavior
                // without executing any real application, installer or output.
                if (scenario is not ("unknown-payload" or "native-only"))
                {
                    Directory.CreateDirectory(Path.Combine(source, "Agent"));
                    File.Copy(development, Path.Combine(source, "Agent", Path.GetFileName(executable)));
                }
                if (scenario == "ambiguous-primary")
                {
                    Directory.CreateDirectory(Path.Combine(source, "other"));
                    File.Copy(stable, Path.Combine(source, "other", Path.GetFileName(executable)));
                }
                Directory.CreateDirectory(Path.Combine(source, "companion"));
                File.Copy(stable, Path.Combine(source, "companion", "NDIJobConfigurator.exe"));
                if (scenario is "native-entry" or "native-only")
                {
                    // Admission of native metadata is exercised separately below; do not launch it.
                    var descriptor = OwnedPaths.Read<LifecycleCapabilities>(Path.Combine(source, "toolbox-lifecycle.json"));
                    descriptor.EntryPoint = "NDI Configurator PC Agent Setup.exe"; descriptor.EntryPointKind = "native";
                    OwnedPaths.Write(Path.Combine(source, "toolbox-lifecycle.json"), descriptor);
                    File.Copy(development, Path.Combine(source, descriptor.EntryPoint));
                }
                var package = Path.Combine(directory, "package.zip"); ZipFile.CreateFromDirectory(source, package);
                var bytes = File.ReadAllBytes(package);
                var release = Release("v0.7.5-dev.1", true);
                var asset = new ReleaseAsset { Name = "NDI-Configurator-PC-Agent-win-x64.zip", Size = bytes.Length,
                    Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), DownloadUrl = definition.RepositoryUrl + "/releases/download/" + release.Tag + "/NDI-Configurator-PC-Agent-win-x64.zip" };
                release.Assets = [asset];
                var stateRoot = Path.Combine(directory, "toolkit"); var downloads = Path.Combine(stateRoot, "Downloads");
                var cached = Path.Combine(downloads, definition.Id, asset.Digest[7..].ToLowerInvariant(), asset.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.WriteAllBytes(cached, bytes);
                using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Package-version fixture refuses network")));
                var service = new PackageService(http, downloads);
                if (scenario is "native-entry" or "native-only")
                {
                    // Different tags may identify the same bytes. The admitted payload owns
                    // the expected version even when the release's display tag differs.
                    var preparing = service.PrepareManagedAsync(definition, release, "update", null, default); Pump(preparing);
                    var prepared = preparing.Result;
                    Check(prepared.PayloadVersion == "0.7.5-dev.1" && prepared.Capabilities?.EntryPointKind == "native",
                        "Verified native Agent package admits its actual payload version without executing the owner");
                    release.Tag = "v99.0.0";
                    preparing = service.PrepareManagedAsync(definition, release, "update", null, default); Pump(preparing); prepared = preparing.Result;
                    Check(prepared.PayloadVersion == "0.7.5-dev.1", "A different release tag cannot replace the verified binary version");
                    var repairing = service.PrepareManagedAsync(definition, release, "repair", null, default); Pump(repairing);
                    var repair = repairing.Result;
                    Check(repair.PayloadVersion is null, "Package repair does not gain explicit version-switch intent");
                    continue;
                }
                var window = new MainWindow(stateRoot, true); window.Cards.Clear();
                try
                {
                    typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, service);
                    var card = new AppCard(definition) { Installed = InstallationService.Find(definition, null, null), Snapshot = new([release], DateTimeOffset.UtcNow), Channel = ReleaseChannel.Development, Offline = true };
                    window.Cards.Add(card);
                    var coordinator = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    if (scenario == "repair-downgrade")
                    {
                        Check(!card.CompatibleRepairPackage && card.State == UpdateState.SwitchChannel, "Explicit channel selection does not make a stable-to-dev downgrade compatible with implicit repair");
                        continue;
                    }
                    Check(card.CanInstall && (scenario == "fresh-install" ? !card.HasInstallation : card.InstallText == "Switch version"), scenario + ": public stable baseline offers the explicit dev package action");
                    Pump((Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!);
                    var request = OwnedPaths.Read<LifecycleRequest>(capture);
                    Check(request.RequestedVersion == (scenario is "unknown-payload" or "ambiguous-primary" ? null : "0.7.5-dev.1") && request.PreserveData && request.Role is null &&
                        request.Action == (scenario == "fresh-install" ? "install" : "update"), scenario + ": verified primary payload version reaches the actual package request without role or data-policy changes");
                    var operation = coordinator.Operations.Single();
                    Check(operation.ExitCode == 0 && operation.Result?.IsSuccess == true, scenario + ": the owner fixture completed normally");
                    Check(operation.RequiresReview == (scenario == "retained-stable"), scenario + ": dispatch distinguishes matching completion from retained stable success");
                    if (scenario is "matched-dev" or "fresh-install")
                        Check(card.State == UpdateState.Current && !operation.Pending && !card.OperationPending, scenario + ": installed dev payload is reread and the card becomes current");
                    if (scenario == "retained-stable")
                        Check(card.OperationPending && card.InlineReason.Contains("0.7.5-dev.1") && card.InlineReason.Contains("0.7.5") && !card.CanInstall,
                            "Card retains actionable version mismatch and disables repeat switching until Recheck");
                }
                finally { window.Close(); }
            }
            foreach (var tampered in new[] { false, true })
            {
                var directory = Path.Combine(root, tampered ? "saved-tampered" : "saved-verified");
                var capture = Path.Combine(directory, "captured.json");
                FixtureAdapter(directory, "environment", EmitSuccess.Replace("message='Fixture success'",
                    "message='Saved setup completed';components=@(@{productId='environment';installed=$true;version='0.7.5-dev.1'})")
                    .Replace("ConvertTo-Json)", "ConvertTo-Json -Depth 6)") + "\nCopy-Item -LiteralPath $LifecycleRequestPath -Destination " + PsQuote(capture));
                var setup = Path.Combine(directory, "Kiloview-Environment-Setup.exe"); File.Copy(development, setup);
                var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup)));
                if (tampered) File.AppendAllText(setup, "changed");
                var definition = Catalog.Apps.Single(a => a.IsEnvironment) with { KnownPaths = [], RegistryNames = [], MaintenanceDirectories = [] };
                var window = new MainWindow(Path.Combine(directory, "toolkit"), true); window.Cards.Clear();
                try
                {
                    var local = (LocalState)typeof(MainWindow).GetField("local", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    local.Preferences.Setups[definition.Id] = new(setup, "v99.0.0", digest);
                    var card = new AppCard(definition) { Installed = new(setup, null, true) { SavedSetup = local.Preferences.Setups[definition.Id] } };
                    window.Cards.Add(card);
                    Check(card.CanInstall, "Verified saved Environment package remains available offline");
                    var preparing = (Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!;
                    window.Cards.Clear(); Pump(preparing); // Avoid host Environment probing after the fixture.
                    if (tampered) Check(!File.Exists(capture) && card.Activity.Contains("changed"), "Changed saved setup cannot supply version intent or reach its owner");
                    else
                    {
                        var request = OwnedPaths.Read<LifecycleRequest>(capture);
                        Check(request.Action == "install" && request.RequestedVersion == "0.7.5-dev.1" && request.Role is null && request.PreserveData,
                            "Verified saved setup sends its executable version rather than its different display tag");
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { if (previousState is null) File.Delete(AgentStatePath); else File.WriteAllText(AgentStatePath, previousState); }
    }

    private static async Task CandidateVersionAdmissionTestsAsync(string manifestPath)
    {
        var versions = new Dictionary<string, string> { ["environment"] = "3.1.0", ["job"] = "0.8.16-dev.1", ["pc-agent"] = "0.7.5-dev.1", ["resolume"] = "0.3.8" };
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var requests = 0;
        using var http = new HttpClient(new FakeHandler(_ => { requests++; throw new HttpRequestException("Frozen candidate admission refuses network"); }));
        foreach (var pin in manifest.RootElement.EnumerateArray())
        {
            var product = pin.GetProperty("product").GetString()!;
            var path = pin.GetProperty("path").GetString()!;
            var digest = pin.GetProperty("sha256").GetString()!;
            var definition = Catalog.Apps.Single(a => a.Id == product);
            var release = Release("v" + versions[product], AppVersion.Parse(versions[product])!.IsPrerelease);
            var asset = new ReleaseAsset { Name = Path.GetFileName(path), Size = new FileInfo(path).Length, Digest = "sha256:" + digest,
                DownloadUrl = definition.RepositoryUrl + "/releases/download/" + release.Tag + "/" + Path.GetFileName(path) };
            release.Assets = [asset];
            Check(await PackageService.VerifyAsync(path, asset, default), product + ": original frozen package hash matches admission");
            var root = Path.Combine(Temporary, "candidate-version-admission", product);
            var cached = Path.Combine(root, product, digest, asset.Name); Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.Copy(path, cached);
            // Frozen owners are read-only; only this disposable copied cache is writable.
            File.SetAttributes(cached, FileAttributes.Normal);
            var prepared = await new PackageService(http, root).PrepareManagedAsync(definition, release, "update", null, default);
            Check(prepared.Capabilities?.Supports("update") == true && AppVersion.Parse(prepared.PayloadVersion)?.CompareTo(AppVersion.Parse(versions[product])) == 0,
                product + ": actual frozen package admits primary binary version " + prepared.PayloadVersion);
            Check(await PackageService.VerifyAsync(path, asset, default), product + ": original owner package remains unchanged");
            Console.WriteLine(JsonSerializer.Serialize(new { product, path, sha256 = digest, payloadVersion = prepared.PayloadVersion, ownerExecuted = false, networkRequests = requests }));
        }
        Check(requests == 0, "All four frozen owner packages admitted with no network or owner execution");
    }
}
