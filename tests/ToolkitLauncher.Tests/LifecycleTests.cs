using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static LifecycleResult Result(LifecycleOperation op, string outcome = "succeeded") =>
        new(1, op.Request.OperationId, op.Request.ProductId, op.Request.Action, outcome, DateTimeOffset.UtcNow, "Fixture " + outcome);

    private static LifecycleCapabilities FixtureAdapter(string root, string product, string body)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "fixture.ps1"), "param([string]$LifecycleRequestPath)\n$ErrorActionPreference='Stop'\n" + body, new UTF8Encoding(true));
        OwnedPaths.Write(Path.Combine(root, "toolbox-lifecycle.json"), new
        {
            schemaVersion = 1, productId = product, supportedActions = new[] { "install", "inspect", "uninstall", "repair", "update" },
            entryPoint = "fixture.ps1", entryPointKind = "powershell"
        });
        return LifecycleCapabilities.Load(root, product)!;
    }

    private const string EmitSuccess = """
        $r = [IO.File]::ReadAllText($LifecycleRequestPath) | ConvertFrom-Json
        $result = @{schemaVersion=1;operationId=$r.operationId;productId=$r.productId;action=$r.action;outcome='succeeded';updatedUtc=[DateTimeOffset]::UtcNow.ToString('o');message='Fixture success'}
        [IO.File]::WriteAllText($r.resultPath, ($result | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        """;

    private static async Task LifecycleTestsAsync()
    {
        var root = Path.Combine(Temporary, "lifecycle-é-测试");
        var coordinator = new LifecycleCoordinator(Path.Combine(root, "operations"));
        var op = coordinator.Begin("job", "install", "protocol");
        OwnedPaths.ValidatePrivateDirectory(coordinator.Store.DirectoryFor(op.Request.OperationId));
        var requestText = File.ReadAllText(coordinator.Store.RequestPath(op));
        Check(!requestText.Contains("requestedVersion") && !requestText.Contains("role") && requestText.Contains("preserveData"), "Lifecycle request omits unverified version and owner role, retaining data by default");
        Check(coordinator.MutationBlocked && new LifecycleCoordinator(coordinator.Store.Root).MutationBlocked, "Missing result stays pending across Toolkit restart");
        var competing = new LifecycleCoordinator(coordinator.Store.Root);
        await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => competing.Begin("pc-agent", "repair", "protocol")), "A second Toolkit coordinator consults the persisted mutation lock");
        await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => coordinator.Begin("resolume", "install", "protocol")), "Unconfirmed operation blocks conflicting mutations");
        foreach (var invalid in new[] { Result(op) with { OperationId = Guid.NewGuid() }, Result(op) with { UpdatedUtc = op.CreatedUtc.AddSeconds(-1) }, Result(op) with { SchemaVersion = 2 }, Result(op) with { Components = JsonSerializer.SerializeToElement(new { installed = true }) } })
        {
            OwnedPaths.Write(op.Request.ResultPath, invalid);
            coordinator.Reconcile();
            Check(op.RequiresReview && op.Pending && op.Result is null, "Stale, mismatched, unsupported and malformed component results never establish success");
        }
        foreach (var schema in new[] { "true", "[1]", "\"1\"" })
        {
            var json = JsonSerializer.Serialize(Result(op), OwnedPaths.Json).Replace("\"schemaVersion\": 1", "\"schemaVersion\": " + schema);
            File.WriteAllText(op.Request.ResultPath, json);
            coordinator.Reconcile();
            Check(op.RequiresReview && op.Result is null, "Consumer rejects coerced result schema type " + schema);
        }
        OwnedPaths.Write(op.Request.ResultPath, Result(op, "running")); coordinator.Reconcile();
        Check(op.Pending && !coordinator.Reconcile(), "Repeated heartbeat is stable and remains pending");
        OwnedPaths.Write(op.Request.ResultPath, Result(op, "partial") with { RecoveryRequired = true, NextAction = "Review retained files" }); coordinator.Reconcile();
        Check(!op.Pending && !coordinator.MutationBlocked && op.Result?.NextAction == "Review retained files", "Owner terminal partial completes attempt and retains recovery guidance");
        var restart = coordinator.Begin("job", "update", "protocol");
        OwnedPaths.Write(restart.Request.ResultPath, Result(restart, "restart-required")); coordinator.Reconcile();
        Check(new LifecycleCoordinator(coordinator.Store.Root).MutationBlocked, "Restart-required persists as pending");
        var inspection = coordinator.Begin("job", "inspect", "protocol");
        OwnedPaths.Write(inspection.Request.ResultPath, Result(inspection) with { SafeToRetry = false }); coordinator.Reconcile();
        await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => coordinator.ReleaseAfterInspect("job", inspection)), "Inspection cannot force-unlock unresolved owner state");
        OwnedPaths.Write(inspection.Request.ResultPath, Result(inspection) with { SafeToRetry = true, RecoveryRequired = false }); coordinator.Store.Reconcile(inspection);
        coordinator.ReleaseAfterInspect("job", inspection);
        Check(!coordinator.MutationBlocked && restart.ReconciledBy == inspection.Request.OperationId && restart.Result?.Outcome == "restart-required", "Owner-safe inspection releases stale lock while preserving original unconfirmed outcome");

        foreach (var invalidPath in new[] { "../escape.exe", "C:\\escape.exe", "folder/../file.exe", "bad.exe:stream", "folder./file.exe" })
            await ThrowsAsync<InvalidDataException>(() => Task.Run(() => OwnedPaths.Resolve(root, invalidPath, false)), "Descriptor paths cannot escape the owned root");
        Check(OwnedPaths.IsCloudTag(0x9000601a) && OwnedPaths.IsCloudTag(0x9000e01a) && !OwnedPaths.IsCloudTag(0xa000000c) && !OwnedPaths.IsCloudTag(0xa0000003), "Cloud tag allowlist excludes symlink and junction tags");
        OwnedPaths.CheckNoReparse(Environment.CurrentDirectory);
        Check(true, "Actual workspace cloud placeholders pass final-path validation");
        var linkedTarget = Path.Combine(root, "link-target"); Directory.CreateDirectory(linkedTarget);
        var sentinel = Path.Combine(linkedTarget, "retained.txt"); File.WriteAllText(sentinel, "retain");
        var link = Path.Combine(root, "junction");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var junctionInfo = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        junctionInfo.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("New-Item -ItemType Junction -Path " + PsQuote(link) + " -Target " + PsQuote(linkedTarget) + " -ErrorAction Stop | Out-Null")) }) junctionInfo.ArgumentList.Add(argument);
        using (var process = Process.Start(junctionInfo)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "Owned junction fixture is created"); }
        try { await ThrowsAsync<InvalidDataException>(() => Task.Run(() => OwnedPaths.Resolve(link, "retained.txt")), "Actual junction traversal is rejected before accessing its target"); }
        finally { Directory.Delete(link, false); }
        Check(File.ReadAllText(sentinel) == "retain", "Junction rejection preserves the outside target");
        var unsafeStore = new LifecycleStore(Path.Combine(root, "unsafe-acl"));
        var unsafeOp = unsafeStore.Create("job", "install", "protocol");
        var unsafeDirectory = new DirectoryInfo(unsafeStore.DirectoryFor(unsafeOp.Request.OperationId));
        var originalAcl = unsafeDirectory.GetAccessControl();
        var broadAcl = unsafeDirectory.GetAccessControl();
        broadAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        try
        {
            unsafeDirectory.SetAccessControl(broadAcl);
            var unsafeCoordinator = new LifecycleCoordinator(unsafeStore.Root);
            Check(unsafeCoordinator.MutationBlocked && unsafeCoordinator.Store.Warning.Length > 0, "Broadly writable saved operation fails closed with a visible warning");
        }
        finally { unsafeDirectory.SetAccessControl(originalAcl); }

        var successful = FixtureAdapter(Path.Combine(root, "success"), "job", EmitSuccess + "\nexit 0");
        var command = LifecycleCoordinator.StartInfo(successful, "install", coordinator.Store.RequestPath(op));
        Check(command.ArgumentList.Contains("-ExecutionPolicy") && command.ArgumentList.Contains("Bypass") && command.Environment["PSModulePath"]!.EndsWith(@"WindowsPowerShell\v1.0\Modules"), "Verified Windows PowerShell handoff overrides inherited policy and incompatible module paths for this process only");
        var completed = await coordinator.RunAsync("job", "install", successful, null, null, default);
        Check(completed.Result?.IsSuccess == true && completed.ExitCode == 0 && !completed.Pending, "Consumer invokes Windows PowerShell adapter and reads UTF-8 result in Unicode path");
        var conflictAdapter = FixtureAdapter(Path.Combine(root, "conflict"), "job", EmitSuccess + "\nStart-Sleep -Milliseconds 1100\nexit 17");
        var conflictTask = coordinator.RunAsync("job", "update", conflictAdapter, null, null, default);
        while (!conflictTask.IsCompleted && coordinator.Operations.Last().Result is null) await Task.Delay(50);
        Check(coordinator.MutationBlocked && coordinator.Operations.Last().ProcessActive, "Terminal success emitted before process exit retains mutation lock");
        var reopened = new LifecycleCoordinator(coordinator.Store.Root);
        Check(reopened.MutationBlocked, "Toolkit reopen cannot trust terminal result while producer is still active");
        var conflict = await conflictTask;
        Check(conflict.RequiresReview && conflict.Pending && conflict.ExitCode == 17, "Contradictory nonzero exit never becomes success");
        var missing = new LifecycleCoordinator(Path.Combine(root, "missing-operations"));
        var noResult = await missing.RunAsync("job", "install", FixtureAdapter(Path.Combine(root, "missing"), "job", "exit 0"), null, null, default);
        Check(noResult.Result is null && noResult.Pending && noResult.RequiresReview, "Zero exit without result remains unconfirmed");

        var closing = new LifecycleCoordinator(Path.Combine(root, "closing-operations"));
        using var cancel = new CancellationTokenSource();
        var closingRoot = Path.Combine(root, "closing");
        var closingReady = Path.Combine(closingRoot, "ready");
        var closingRelease = Path.Combine(closingRoot, "release");
        var closingTask = closing.RunAsync("job", "install", FixtureAdapter(closingRoot, "job", """
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'ready'), 'ready')
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not [IO.File]::Exists((Join-Path $PSScriptRoot 'release'))) {
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Closing fixture release timed out.' }
                Start-Sleep -Milliseconds 30
            }
            """ + "\n" + EmitSuccess), null, null, cancel.Token);
        try
        {
            await WaitUntilAsync(() => File.Exists(closingReady), TimeSpan.FromSeconds(15));
            cancel.Cancel();
            var pending = await closingTask;
            var reopenedClosing = new LifecycleCoordinator(closing.Store.Root);
            Check(pending.Pending && reopenedClosing.MutationBlocked, "Closing Toolkit preserves the live owner operation without stopping it");
            using (var producer = Process.GetProcessById(pending.ProcessId!.Value))
            {
                Check(producer.StartTime.ToUniversalTime() == pending.ProcessStartedUtc && !producer.HasExited,
                    "Closing fixture ready marker belongs to the exact live owner process");
                File.WriteAllText(closingRelease, "release");
                await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(producer.ExitCode == 0, "Closing fixture completes successfully after release");
            }
            // The test observes exit, but the reopened coordinator must still verify it independently.
            reopenedClosing.Reconcile();
            Check(reopenedClosing.Operations.Single().Result?.IsSuccess == true && reopenedClosing.MutationBlocked && reopenedClosing.Operations.Single().ExitUnconfirmed, "Reopened Toolkit ingests completion but retains uncertain process exit for owner recheck");
        }
        finally
        {
            File.WriteAllText(closingRelease, "release");
            cancel.Cancel();
            var owned = await closingTask.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                using var producer = owned.ProcessId is { } id ? Process.GetProcessById(id) : null;
                if (producer is not null && producer.StartTime.ToUniversalTime() == owned.ProcessStartedUtc && !producer.HasExited)
                {
                    try { await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (TimeoutException)
                    {
                        // Only this fixture's verified process handle may be stopped after cleanup times out.
                        producer.Kill();
                        await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (ArgumentException) { } // The owned fixture already exited and was reaped.
            catch (InvalidOperationException) { } // It exited between lookup and inspection.
        }

        var portableState = new LocalState(Path.Combine(root, "portable"));
        var bytes = TestCardZip(); var asset = TestCardAsset(bytes);
        using var http = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var packages = new PackageService(http, portableState.DownloadRoot);
        var executable = await packages.InstallPortableAsync(TestCardDefinition, asset, portableState.ApplicationsRoot, null, default);
        var service = new InstallationService(portableState); service.RegisterPortable(TestCardDefinition, executable, asset);
        portableState.Save(); File.Delete(Path.Combine(portableState.Root, "settings.json"));
        Check(service.Find(TestCardDefinition)?.Path == executable, "Portable inventory survives deleted UI preferences");
        File.Delete(Path.Combine(Path.GetDirectoryName(executable)!, ".runtime", "dotnet.exe"));
        var damaged = service.Find(TestCardDefinition)!;
        var damagedCard = new AppCard(TestCardDefinition) { Installed = damaged };
        Check(damagedCard.NeedsPortableRepair && !damagedCard.CanLaunch && damagedCard.CanRepair, "Damaged private runtime disables Launch and offers retained-package repair");
        var repaired = await packages.InstallPortableAsync(TestCardDefinition, asset, portableState.ApplicationsRoot, null, default);
        service.RegisterPortable(TestCardDefinition, repaired, asset);
        var inventory = new PortableInstallations(portableState);
        using (inventory.AcquireLock(TestCardDefinition))
            await ThrowsAsync<IOException>(() => Task.Run(() => { using var rejected = inventory.AcquireLock(TestCardDefinition); }), "Portable lifecycle lock rejects simultaneous removal/repair");
        await ThrowsAsync<IOException>(() => Task.Run(() => inventory.Remove(TestCardDefinition, _ => true)), "Running owned copy blocks removal without killing processes");
        var retainedSettings = Path.Combine(portableState.Root, "testcard-user-settings.json"); File.WriteAllText(retainedSettings, "retain");
        var lockedFile = Path.Combine(Path.GetDirectoryName(repaired)!, "TestCard.Worker.dll");
        using (var locked = File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            await ThrowsAsync<IOException>(() => Task.Run(() => inventory.Remove(TestCardDefinition, _ => false)), "Locked portable file yields retryable partial removal");
        inventory.Remove(TestCardDefinition, _ => false);
        Check(inventory.Read(TestCardDefinition)?.Copies.Length == 0 && service.Find(TestCardDefinition) is null && File.Exists(retainedSettings), "Retry removes only owned copies and retains unrelated data");
        Check(!Directory.Exists(Path.GetDirectoryName(repaired)), "Portable owned package is removed after retry");
        await LifecyclePackageTestsAsync(root);
    }

    private static async Task LifecyclePackageTestsAsync(string root)
    {
        var app = Catalog.Apps.Single(a => a.Id == "job");
        byte[] Archive(bool descriptor, bool corrupt, bool companion)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Invoke-Lifecycle.ps1").Open())) writer.Write("fixture");
                if (descriptor)
                {
                    using var writer = new StreamWriter(zip.CreateEntry("toolbox-lifecycle.json").Open());
                    writer.Write(corrupt ? "{}" : JsonSerializer.Serialize(new { schemaVersion = 1, productId = "job", entryPoint = "Invoke-Lifecycle.ps1", entryPointKind = "powershell", supportedActions = new[] { "install", "repair" } }));
                }
                if (companion)
                {
                    using var writer = new StreamWriter(zip.CreateEntry("pc-onboarding/toolbox-lifecycle.json").Open());
                    writer.Write("{\"schemaVersion\":1,\"productId\":\"pc-agent\"}");
                }
            }
            return buffer.ToArray();
        }
        async Task<PreparedPackage> Prepare(byte[] zip)
        {
            var exe = new byte[] { 1, 2, 3 };
            ReleaseAsset Asset(string name, byte[] payload) => new() { Name = name, Size = payload.Length, Digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), DownloadUrl = app.RepositoryUrl + "/releases/download/vfixture/" + name };
            var release = new Release { Assets = [Asset("NDI-Job-Configurator-Windows.zip", zip), Asset("NDI-Job-Configurator.exe", exe)] };
            using var client = new HttpClient(new FakeHandler(r => new(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri!.AbsolutePath.EndsWith(".zip") ? zip : exe) }));
            return await new PackageService(client, Path.Combine(root, "package-admission")).PrepareManagedAsync(app, release, "install", null, default);
        }
        var complete = await Prepare(Archive(true, false, true));
        Check(complete.Capabilities?.ProductId == "job" && File.Exists(Path.Combine(complete.Capabilities.Root, "pc-onboarding", "toolbox-lifecycle.json")), "Root product descriptor admits a full ZIP with independent companion metadata");
        var legacy = await Prepare(Archive(false, false, false));
        Check(legacy.Capabilities is null && legacy.SetupPath.EndsWith("NDI-Job-Configurator.exe"), "Historical same-name ZIP without descriptor uses the separately verified legacy EXE");
        await ThrowsAsync<InvalidDataException>(() => Prepare(Archive(true, true, true)), "Malformed present root descriptor cannot silently downgrade to legacy execution");

        var installedRoot = Path.Combine(root, "environment-app");
        var retainedRoot = Path.Combine(root, "environment-maintenance");
        Directory.CreateDirectory(installedRoot); Directory.CreateDirectory(retainedRoot);
        var installedExe = Path.Combine(installedRoot, "Kiloview-Environment-Setup.exe");
        var maintenanceExe = Path.Combine(retainedRoot, "Kiloview-Environment-Setup.exe");
        File.Copy(typeof(MainWindow).Assembly.Location, installedExe); File.Copy(installedExe, maintenanceExe);
        foreach (var directory in new[] { installedRoot, retainedRoot })
            OwnedPaths.Write(Path.Combine(directory, "toolbox-lifecycle.json"), new { schemaVersion = 1, productId = "environment", entryPoint = Path.GetFileName(installedExe), supportedActions = new[] { "inspect", "repair", "uninstall" } });
        var environment = Catalog.Apps[0] with { KnownPaths = [installedExe], RegistryNames = [], MaintenanceDirectories = [retainedRoot] };
        Check(InstallationService.Find(environment, null, null) is { IsPresent: true, Capabilities: not null }, "Environment MSI app is detected independently of Toolkit preferences");
        File.Delete(installedExe); File.Delete(Path.Combine(installedRoot, "toolbox-lifecycle.json"));
        var retained = InstallationService.Find(environment, null, null);
        var card = new AppCard(environment) { Installed = retained };
        Check(retained is { IsPresent: false, IsMaintenanceOnly: true, Capabilities: not null } && !card.HasInstallation && card.CanLaunch && card.CanUninstall,
            "Environment app removal retains discoverable maintenance without claiming installed app currency");
        var external = Path.Combine(root, "external.exe"); File.WriteAllText(external, "fixture");
        Check(RegisteredUninstaller.Parse("\"" + external + "\" /quiet", installedRoot) is null,
            "Registered uninstaller outside exact product root is not invoked");
        var olderStable = new Release { Tag = "v1.0.0", Assets = [new() { Name = "NDI-Job-Configurator.exe" }] };
        var newerDev = new AppCard(app) { Installed = new(external, "2.0.0-dev.1"), Snapshot = new([olderStable], DateTimeOffset.UtcNow) };
        Check(newerDev.State == UpdateState.SwitchChannel && !newerDev.CanRepair, "Repair cannot silently downgrade a newer version across release channels");
        var evidence = new EnvironmentEvidence { Roles = ["client"], NdiTools = true };
        var incompleteAgent = new Installation(external, "1.0.0") { PcAgent = new(true, null, "Matching Setup missing.") };
        EnvironmentStatusService.ApplyAgentReadiness(evidence, incompleteAgent);
        var client = EnvironmentSnapshot.From(evidence, DateTimeOffset.UtcNow);
        Check(evidence.PcAgent == true && evidence.PcAgentConfigured == false && client.State == EnvironmentInstallationState.Partial && client.Components.Any(c => c.Name == "PC Agent" && c.Status.Contains("Installed") && c.Detail == "Matching Setup missing."),
            "Environment shares Agent readiness while keeping installed evidence distinct from a missing Setup pair");
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    private static void LifecycleUiTests()
    {
        var root = Path.Combine(Temporary, "lifecycle-ui");
        var state = new LocalState(root);
        var bytes = TestCardZip(); var asset = TestCardAsset(bytes);
        using var client = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var packages = new PackageService(client, state.DownloadRoot);
        string? executable = null;
        Pump(Task.Run(async () => executable = await packages.InstallPortableAsync(TestCardDefinition, asset, state.ApplicationsRoot, null, default)));
        new InstallationService(state).RegisterPortable(TestCardDefinition, executable!, asset);
        var window = new MainWindow(root, true); window.Cards.Clear();
        var card = new AppCard(TestCardDefinition) { Installed = new InstallationService(state).Find(TestCardDefinition) }; window.Cards.Add(card);
        var coordinator = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var pending = coordinator.Begin("testcard", "repair", "portable");
        var before = Directory.GetFiles(state.ApplicationsRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using var offline = new HttpClient(new FakeHandler(_ => throw new IOException("No network allowed during Recheck")));
        typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new PackageService(offline, state.DownloadRoot));
        try
        {
            var task = (Task)typeof(MainWindow).GetMethod("MaintainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card, "inspect", true])!;
            Pump(task);
            Check(!coordinator.MutationBlocked && pending.ReconciledBy is not null && pending.Result is null && before.All(p => File.GetLastWriteTimeUtc(p.Key) == p.Value), "Portable UI Recheck is read-only, uses no network and preserves interrupted outcome");
            card.ReleaseChecking = true;
            Check(card.CanLaunch && !card.Checking, "Remote release check does not disable local portable Launch");
            card.Installed = null; card.Snapshot = new([new Release { Tag = "vfixture" }], DateTimeOffset.UtcNow); card.Recompute();
            Check(card.ShowCompact && card.ShowReleaseHelp && card.InlineReason.Contains("no supported Windows package"), "Compact unavailable package exposes reason and working releases help action");
        }
        finally { window.Close(); }
        var localExe = Path.Combine(root, "local-app.exe"); File.Copy(typeof(MainWindow).Assembly.Location, localExe);
        var offlineWindow = new MainWindow(Path.Combine(root, "offline-window"), false, false); offlineWindow.Cards.Clear();
        var localDefinition = Catalog.Apps.Single(a => a.Id == "udp-to-ndi") with { KnownPaths = [localExe], RegistryNames = [] };
        var localCard = new AppCard(localDefinition); offlineWindow.Cards.Add(localCard);
        using var gate = new PendingReleaseHandler(); using var api = new HttpClient(gate);
        typeof(MainWindow).GetField("github", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, new GitHubClient(api));
        try
        {
            var refresh = offlineWindow.RefreshAsync();
            Pump(WaitUntilAsync(() => localCard.CanLaunch && localCard.ReleaseChecking, TimeSpan.FromSeconds(5)));
            Check(!refresh.IsCompleted && !localCard.Checking && localCard.CanLaunch && offlineWindow.DashboardEnabled,
                "Actual asynchronous refresh enables detected local Launch while the release response is still blocked");
            gate.Release.TrySetResult(); Pump(refresh);
            var retainedSnapshot = localCard.Snapshot;
            const string connectionFailure = "No connection could be made because the target machine actively refused it (api.github.com:443)";
            using var unavailable = new HttpClient(new FakeHandler(_ => throw new HttpRequestException(connectionFailure)));
            var unavailableGithub = new GitHubClient(unavailable);
            typeof(MainWindow).GetField("github", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, unavailableGithub);
            typeof(MainWindow).GetField("icons", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, new IconService(unavailable, Path.Combine(root, "offline-icons")));
            typeof(MainWindow).GetField("updater", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, new SelfUpdateService(unavailableGithub, unavailable, Path.Combine(root, "offline-updates")));
            Pump(offlineWindow.RefreshAsync(true));
            Check(localCard.ShortReason == "Updates unavailable" && localCard.ActivitySummary == "" && localCard.CanLaunch && ReferenceEquals(retainedSnapshot, localCard.Snapshot),
                "Failed release check keeps local Launch and saved releases with only concise default status");
            Check(localCard.ReleaseError.Contains(connectionFailure) && File.ReadAllText(Path.Combine(root, "offline-window", "activity.log")).Contains(connectionFailure),
                "Release failure diagnostic remains available in Details and durable logs");
            Pump((Task)typeof(MainWindow).GetMethod("CheckToolkitUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(offlineWindow, [true])!);
            Check(offlineWindow.Footer == "Updates unavailable" && offlineWindow.DashboardEnabled,
                "Toolkit update check failure keeps raw transport details out of the default footer");
            var recoveredGithub = new GitHubClient(api);
            typeof(MainWindow).GetField("github", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, recoveredGithub);
            typeof(MainWindow).GetField("updater", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, new SelfUpdateService(recoveredGithub, api, Path.Combine(root, "offline-updates")));
            Pump(offlineWindow.RefreshAsync(true));
            Pump((Task)typeof(MainWindow).GetMethod("CheckToolkitUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(offlineWindow, [true])!);
            Check(localCard.ReleaseError == "" && localCard.ShortReason != "Updates unavailable" && offlineWindow.Footer == "" && localCard.CanLaunch,
                "Successful retry clears stale update errors without changing local availability");
            using var timeoutHttp = new HttpClient(new FakeHandler(_ => throw new TaskCanceledException("Fixture HTTP deadline")));
            typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(offlineWindow, new PackageService(timeoutHttp, Path.Combine(root, "timeout-cache")));
            localCard.Installed = null;
            localCard.Snapshot = new([new Release { Tag = "v1.0.3", Assets = [new() { Name = "UDP-to-NDI-Setup-1.0.3-x64.exe", Size = 10, Digest = "sha256:" + new string('a', 64), DownloadUrl = localDefinition.RepositoryUrl + "/releases/download/v1.0.3/UDP-to-NDI-Setup-1.0.3-x64.exe" }] }], DateTimeOffset.UtcNow);
            var preparation = (Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(offlineWindow, [localCard])!;
            Pump(preparation);
            Check(localCard.Activity.Length > 0 && !localCard.Activity.Contains("cancelled", StringComparison.OrdinalIgnoreCase) && !localCard.InstallerRunning,
                "HTTP timeout before handoff is reported as a failure, not user cancellation");
        }
        finally { gate.Release.TrySetResult(); offlineWindow.Close(); }
    }

    private sealed class PendingReleaseHandler : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > limit) throw new TimeoutException("Fixture condition did not arrive."); await Task.Delay(30); }
    }

    private sealed record OwnerFixture(string Product, string Path, string Kind, string[] Outcomes);
    private sealed record NativeInspectFixture(string Product, string Path, string Sha256);
    private static async Task NativeInspectTestsAsync(string manifest)
    {
        var fixtures = JsonSerializer.Deserialize<NativeInspectFixture[]>(File.ReadAllText(manifest), OwnedPaths.Json)!;
        foreach (var fixture in fixtures)
        {
            if (fixture.Product is not ("environment" or "pc-agent")) throw new InvalidDataException("Only explicitly supplied read-only native inspection fixtures are accepted.");
            using (var stream = File.OpenRead(fixture.Path))
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native candidate changed.");
            var capabilities = LifecycleCapabilities.Load(Path.GetDirectoryName(fixture.Path)!, fixture.Product)!;
            var coordinator = new LifecycleCoordinator(Path.Combine(Temporary, "native-inspect-é-测试", fixture.Product));
            var operation = await coordinator.RunAsync(fixture.Product, "inspect", capabilities, null, null, default);
            Check(operation.Result?.IsSuccess == true && operation.ExitCode == 0 && !operation.Pending, "Actual native " + fixture.Product + " inspect through Toolkit consumer: " + operation.Message);
            Console.WriteLine("NATIVE INSPECT EVIDENCE " + coordinator.Store.DirectoryFor(operation.Request.OperationId));
        }
    }
    private static string PsQuote(string text) => "'" + text.Replace("'", "''") + "'";
    private static async Task OwnerFixtureTestsAsync(string manifest)
    {
        var fixtures = JsonSerializer.Deserialize<OwnerFixture[]>(File.ReadAllText(manifest), OwnedPaths.Json)!;
        foreach (var fixture in fixtures)
        foreach (var scenario in fixture.Outcomes)
        {
            if (!File.Exists(fixture.Path)) throw new FileNotFoundException("Owner fixture is missing", fixture.Path);
            var root = Path.Combine(Temporary, "owner-é-测试", fixture.Product, scenario);
            var body = "& " + PsQuote(fixture.Path) + (fixture.Kind == "agent" ? " --lifecycle-fixture $LifecycleRequestPath " + PsQuote(scenario)
                : " -LifecycleRequestPath $LifecycleRequestPath " + (fixture.Kind == "resolume" ? "-FixtureRoot " + PsQuote(Path.Combine(root, "product")) + " -Scenario " : "-FixtureOutcome ") + PsQuote(scenario)) + " *> " + PsQuote(Path.Combine(root, "fixture.log")) + "\nexit $LASTEXITCODE";
            body = "try {\n" + body + "\n} catch { [IO.File]::WriteAllText(" + PsQuote(Path.Combine(root, "fixture.log")) + ", ($_ | Out-String)); exit 1 }";
            var coordinator = new LifecycleCoordinator(Path.Combine(root, "operations"));
            var operation = await coordinator.RunAsync(fixture.Product, "install", FixtureAdapter(Path.Combine(root, "adapter"), fixture.Product, body), null, null, default);
            var outcome = scenario switch { "success" => "succeeded", "cancel" => "cancelled", "busy" => "blocked", "copy-failure" or "registration-failure" => "failed", "crash" => "running", _ => scenario };
            var code = outcome switch { "succeeded" or "no-change" => 0, "cancelled" => 1602, "blocked" => 1618, "restart-required" => 3010, "running" => fixture.Product == "environment" ? 7 : 17, _ => 1 };
            if (operation.Result?.Outcome != outcome || operation.ExitCode != code)
                Console.WriteLine(File.Exists(Path.Combine(root, "fixture.log")) ? File.ReadAllText(Path.Combine(root, "fixture.log")) : "Fixture has no diagnostics.");
            Check(operation.Result?.Outcome == outcome && operation.ExitCode == code, "Actual " + fixture.Product + " producer/consumer: " + scenario + " (exit " + operation.ExitCode + "; " + operation.Message + ")");
            var reopened = new LifecycleCoordinator(coordinator.Store.Root);
            Check(reopened.MutationBlocked == (outcome is "running" or "restart-required"), "Actual " + fixture.Product + " outcome survives Toolkit reopen: " + scenario);
            Console.WriteLine("OWNER EVIDENCE " + coordinator.Store.DirectoryFor(operation.Request.OperationId));
        }
    }
}
