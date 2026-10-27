using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private const string RetainedOwner = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private static EnvironmentEvidence RetainedEnvironment() => JsonSerializer.Deserialize<EnvironmentEvidence>(JsonSerializer.Serialize(new
    {
        Roles = Array.Empty<string>(), Configuration = false, Distro = true, DistroRunning = false,
        DistroName = "KiloLink-Ubuntu", Watchdog = false, WatchdogRunning = false,
        NdiTools = true, NdiVersion = "6.3.2.0", Discovery = false, DiscoveryConfigured = false, DiscoveryRunning = false, DiscoveryListening = false,
        CurrentUserSid = RetainedOwner, ServerRecoveryPending = false,
        ComponentReceipt = new { schemaVersion = 1, roles = Array.Empty<string>(), retainedServer = true, serverOwnerSid = RetainedOwner },
        RetainedServerRecord = new { schemaVersion = 1, active = false, distro = "KiloLink-Ubuntu", serverOwnerSid = RetainedOwner }
    }))!;

    private static string RetainedFixture(string name)
    {
        var assembly = typeof(Program).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }

    private static LifecycleOperation RetainedOperation()
    {
        using var capture = JsonDocument.Parse(RetainedFixture("wrapper-exit.json"));
        return capture.RootElement.GetProperty("journal").Deserialize<LifecycleOperation>(OwnedPaths.Json)!;
    }

    private static void EnvironmentRetainedTests(string? capturedRoot = null)
    {
        var snapshot = EnvironmentSnapshot.From(RetainedEnvironment(), DateTimeOffset.UtcNow);
        var card = new AppCard(Catalog.Apps.Single(a => a.IsEnvironment))
        {
            Installed = new("C:\\Fixture\\Kiloview-Environment-Setup.exe", "3.1.0"),
            Snapshot = new([Release("v3.1.0")], DateTimeOffset.UtcNow), EnvironmentStatus = snapshot
        };
        Check(!card.NeedsEnvironmentSetup && card.CanLaunch && !card.ShowPrimary && snapshot.Status == "Inactive · data retained",
            "Successful preserving removal is neutral with Open setup and no false completion or repair demand");
        Check(snapshot.State == EnvironmentInstallationState.Inactive && snapshot.Colour == "#617082" &&
            card.EnvironmentSummary == "Server · Inactive · data retained" && !card.RepairIsPrimary,
            "Retained Server has an explicit neutral state independent of installed Setup version");
        Check(snapshot.Components[0].Detail.Contains("Linux environment") && !snapshot.Components.Any(c => c.Status.Contains("not verified")),
            "Inactive Details explains retained data without demanding stopped container verification");
        card.Snapshot = null; card.ReleaseError = "Offline fixture";
        Check(card.CanLaunch && card.LaunchText == "Open setup" && card.ShortReason == "Updates unavailable" && !card.NeedsSetup,
            "Offline release checks do not turn a deliberately inactive Server into incomplete setup");
        card.Snapshot = new([Release("v3.2.0")], DateTimeOffset.UtcNow);
        Check(card.Status == "Needs updating" && card.InstallText == "Update", "Retained-role status preserves configurator version comparison");

        void Guard(Action<EnvironmentEvidence> change, string label)
        {
            var value = RetainedEnvironment(); change(value);
            Check(EnvironmentSnapshot.From(value, DateTimeOffset.UtcNow).State != EnvironmentInstallationState.Inactive, label);
        }
        foreach (var (field, values) in new[]
        {
            ("active", new[] { "true", "null", "\"false\"" }),
            ("schemaVersion", new[] { "2", "null", "\"1\"" }),
            ("serverOwnerSid", new[] { "\"other-owner\"", "null" }),
            ("distro", new[] { "\"Other-Distro\"", "null" })
        })
            foreach (var json in values) Guard(e =>
            {
                var record = JsonNode.Parse(e.RetainedServerRecord!.Value.GetRawText())!;
                record[field] = JsonNode.Parse(json); e.RetainedServerRecord = JsonSerializer.SerializeToElement(record);
            }, "Conflicting or malformed retained " + field + " cannot report inactive: " + json);
        foreach (var (field, json) in new[] { ("retainedServer", "false"), ("retainedServer", "\"true\""), ("schemaVersion", "2"),
            ("serverOwnerSid", "\"other-owner\""), ("roles", "null"), ("roles", "[\"server\"]"), ("roles", "[\"client\",\"client\"]") })
            Guard(e =>
            {
                var record = JsonNode.Parse(e.ComponentReceipt!.Value.GetRawText())!;
                record[field] = JsonNode.Parse(json); e.ComponentReceipt = JsonSerializer.SerializeToElement(record);
            }, "Invalid or conflicting owner receipt stays guarded: " + field + "=" + json);
        Guard(e => e.RetainedServerRecord = null, "Missing retained record does not infer deliberate removal from stopped WSL");
        Guard(e => e.ComponentReceipt = null, "Missing owner receipt stays guarded");
        Guard(e => e.ComponentReceiptInvalid = true, "Unreadable owner receipt stays unknown");
        Guard(e => e.CurrentUserSid = null, "Unknown current account cannot claim retained ownership");
        Guard(e => e.Roles = null, "Unknown deployment roles cannot become inactive");
        Guard(e => e.Roles = ["server"], "Active Server role overrides retained markers");
        Guard(e => e.Configuration = true, "Reactivated Server configuration overrides old retained markers");
        Guard(e => e.Configuration = null, "Unknown Server configuration stays guarded");
        Guard(e => e.Distro = null, "Unknown distro registration stays guarded");
        Guard(e => e.Distro = false, "Missing retained distro cannot claim retained environment");
        Guard(e => e.Watchdog = true, "Remaining startup task prevents inactive status");
        Guard(e => e.Watchdog = null, "Unreadable startup tasks prevent inactive status");
        Guard(e => e.ContainerRunning = true, "Observed running container contradicts inactive status");
        Guard(e => e.WebResponding = true, "Observed responding local Server contradicts inactive status");
        Guard(e => e.DiscoveryConfigured = true, "Still-configured Discovery prevents inactive status");
        Guard(e => e.DiscoveryConfigured = null, "Unknown Discovery configuration stays guarded");
        Guard(e => e.DiscoveryRunning = true, "Running Discovery prevents inactive status");
        Guard(e => e.DiscoveryRunning = null, "Unknown Discovery state stays guarded");
        Guard(e => e.RestartPending = true, "Pending restart remains incomplete");
        Guard(e => e.ServerRecoveryPending = true, "Partial owner removal with pending watchdog recovery remains incomplete");
        Guard(e => e.ServerRecoveryPending = null, "Unreadable owner recovery state stays guarded");

        var client = RetainedEnvironment(); client.Roles = ["client"];
        var receipt = JsonNode.Parse(client.ComponentReceipt!.Value.GetRawText())!; receipt["roles"] = new JsonArray("client");
        client.ComponentReceipt = JsonSerializer.SerializeToElement(receipt); client.PcAgent = true; client.PcAgentConfigured = true;
        var combined = EnvironmentSnapshot.From(client, DateTimeOffset.UtcNow);
        Check(combined.State == EnvironmentInstallationState.Full && combined.RoleLabel == "Client" &&
            combined.Components.Any(c => c.Status == "Inactive · data retained"),
            "Removing Server preserves active Client completeness and explains retained Server data");
        client.PcAgentConfigured = false;
        Check(EnvironmentSnapshot.From(client, DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Partial,
            "Retained Server cannot hide a Client that still needs setup");
        client.PcAgentConfigured = null;
        Check(EnvironmentSnapshot.From(client, DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Unknown,
            "Retained Server cannot promote unknown Client readiness");
        var active = FullEnvironment(); active.Roles = ["server", "client"]; active.PcAgent = true; active.PcAgentConfigured = true;
        active.ComponentReceipt = client.ComponentReceipt; active.RetainedServerRecord = client.RetainedServerRecord;
        Check(EnvironmentSnapshot.From(active, DateTimeOffset.UtcNow) is { State: EnvironmentInstallationState.Full, RoleLabel: "Client + Server" },
            "Both active roles keep their existing completeness checks despite stale retained metadata");

        ReplayRetainedCapture(RetainedFixture("wrapper-exit.json"), RetainedFixture("installation-components.json"),
            RetainedFixture("retained-server.json"), RetainedFixture("local-state.json"));
        RetainedRecordReaderTests();
        if (capturedRoot is not null)
        {
            var paths = new[] { Path.Combine(capturedRoot, "vm-environment10-wrapper-exit.json"),
                Path.Combine(capturedRoot, "environment10-role-removal-success", "installation-components.json"),
                Path.Combine(capturedRoot, "environment10-role-removal-success", "retained-server.json"),
                Path.Combine(capturedRoot, "environment10-after-removal-first.json") };
            var hashes = paths.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
            ReplayRetainedCapture(File.ReadAllText(paths[0]), File.ReadAllText(paths[1]), File.ReadAllText(paths[2]), File.ReadAllText(paths[3]));
            Check(paths.Select((p, i) => hashes[i].SequenceEqual(SHA256.HashData(File.ReadAllBytes(p)))).All(v => v),
                "Exact original owner result, terminal journal, receipts and stopped-WSL evidence remain byte-identical");
        }
    }

    private static void ReplayRetainedCapture(string wrapper, string receipt, string retained, string local)
    {
        using var capture = JsonDocument.Parse(wrapper); using var state = JsonDocument.Parse(local);
        var operation = capture.RootElement.GetProperty("journal").Deserialize<LifecycleOperation>(OwnedPaths.Json)!;
        var result = capture.RootElement.GetProperty("result").Deserialize<LifecycleResult>(OwnedPaths.Json)!; result.ValidateEvidence();
        Check(result.OperationId == Guid.Parse("7dbc220c-fe4d-404a-b13d-48c556b27d9a") && result.IsSuccess && result.RecoveryRequired == false &&
            operation.Request.PreserveData && operation.Request.Role is null && operation.ExitCode == 0 && operation.Finished &&
            !operation.Pending && !operation.RequiresReview && !operation.ExitUnconfirmed && !operation.ProcessActive,
            "Exact captured preserving removal has succeeded with observed exit zero and terminal unlocked journal");
        var components = result.Components!.Value.EnumerateArray().ToDictionary(c => c.GetProperty("productId").GetString()!);
        var kilo = components["kilolink"]; var discovery = components["ndi-discovery"];
        Check(kilo.GetProperty("retainedEnvironment").GetBoolean() && !kilo.GetProperty("roleActive").GetBoolean() &&
            kilo.GetProperty("installed").ValueKind == JsonValueKind.Null && kilo.GetProperty("running").ValueKind == JsonValueKind.Null &&
            components["environment"].GetProperty("configurationAppInstalled").GetBoolean() && components["ndi-tools"].GetProperty("installed").GetBoolean() &&
            state.RootElement.GetProperty("distributions").GetProperty("stdout").GetString()!.Contains("Stopped") &&
            state.RootElement.GetProperty("linux").GetProperty("skipped").GetString() == "Distro was not already running",
            "Exact owner components distinguish retained inactive data from installed configurator/shared NDI without probing stopped WSL");
        var evidence = RetainedEnvironment();
        evidence.ComponentReceipt = JsonSerializer.Deserialize<JsonElement>(receipt); evidence.RetainedServerRecord = JsonSerializer.Deserialize<JsonElement>(retained);
        evidence.Roles = evidence.ComponentReceipt.Value.GetProperty("roles").Deserialize<string[]>();
        evidence.Configuration = kilo.GetProperty("configured").GetBoolean(); evidence.Distro = kilo.GetProperty("distroRegistered").GetBoolean();
        evidence.DiscoveryConfigured = discovery.GetProperty("configured").GetBoolean(); evidence.DiscoveryRunning = discovery.GetProperty("running").GetBoolean();
        var snapshot = EnvironmentSnapshot.From(evidence, result.UpdatedUtc);
        Check(snapshot.State == EnvironmentInstallationState.Inactive && evidence.Container is null && evidence.ContainerRunning is null &&
            evidence.WebResponding is null && snapshot.Components.Any(c => c.Detail == "6.3.2.0"),
            "Captured owner records and components produce neutral retained state without inventing container or HTTP health");
    }

    private static void RetainedRecordReaderTests()
    {
        using var resource = typeof(EnvironmentStatusService).Assembly.GetManifestResourceStream("ToolkitLauncher.Read-Environment.ps1")!;
        using var reader = new StreamReader(resource);
        var script = reader.ReadToEnd().Replace("\r", "");
        var end = script.IndexOf("try {\n    $agentRoot", StringComparison.Ordinal);
        Check(end > 0, "Production owner-record reader can be exercised before any host runtime checks");
        var isolated = Path.Combine(Temporary, "retained-record-reader");
        var stateRoot = Path.Combine(isolated, "KiloLink"); Directory.CreateDirectory(stateRoot);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var receiptPath = Path.Combine(stateRoot, "installation-components.json");
        var retainedPath = Path.Combine(stateRoot, "retained-server.json");
        var journalPath = Path.Combine(stateRoot, "last-operation.json");
        File.WriteAllText(receiptPath, RetainedFixture("installation-components.json").Replace(RetainedOwner, sid));
        File.WriteAllText(retainedPath, RetainedFixture("retained-server.json").Replace(RetainedOwner, sid));
        File.WriteAllText(journalPath, RetainedFixture("last-operation.json"));
        var command = script[..end] + script[script.LastIndexOf("$result | ConvertTo-Json", StringComparison.Ordinal)..];
        EnvironmentEvidence Probe()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["ProgramData"] = isolated;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Isolated owner-record read timed out."); }
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0) throw new IOException(error);
            var read = JsonSerializer.Deserialize<EnvironmentEvidence>(output)!;
            var evidence = RetainedEnvironment(); evidence.ComponentReceipt = read.ComponentReceipt; evidence.RetainedServerRecord = read.RetainedServerRecord;
            evidence.Roles = read.Roles; evidence.ComponentReceiptInvalid = read.ComponentReceiptInvalid;
            evidence.CurrentUserSid = read.CurrentUserSid; evidence.ServerRecoveryPending = read.ServerRecoveryPending;
            return evidence;
        }
        var before = new[] { receiptPath, retainedPath }.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        Check(EnvironmentSnapshot.From(Probe(), DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Inactive,
            "Actual production PowerShell reader preserves the empty roles array, typed retained record and current owner");
        Check(before.Select((hash, i) => hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(i == 0 ? receiptPath : retainedPath)))).All(v => v),
            "Read-only record probe leaves both owner files byte-identical");
        File.WriteAllText(Path.Combine(stateRoot, "pending-watchdog.json"), "{}");
        Check(EnvironmentSnapshot.From(Probe(), DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Partial,
            "Actual reader detects pending watchdog recovery instead of treating partial removal as inactive");
        File.Delete(Path.Combine(stateRoot, "pending-watchdog.json"));
        var fallback = JsonNode.Parse(RetainedFixture("last-operation.json"))!;
        fallback["pendingWatchdog"] = JsonSerializer.SerializeToNode(new
        { schemaVersion = 1, distro = "KiloLink-Ubuntu", action = "Uninstall", mode = "Retain", startedUtc = "2026-10-27T09:41:48.4506173Z" });
        fallback["recoveryRequired"] = true;
        File.WriteAllText(journalPath, fallback.ToJsonString());
        var interrupted = Probe();
        Check(interrupted.ServerRecoveryPending == true && EnvironmentSnapshot.From(interrupted, DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Partial,
            "Environment10 journal-only pendingWatchdog fallback blocks inactive status when separate marker persistence failed");
        fallback["recoveryRequired"] = false; File.WriteAllText(journalPath, fallback.ToJsonString());
        Check(Probe().ServerRecoveryPending == true, "Pending watchdog identity blocks inactive even before recoveryRequired is set");
        fallback.AsObject().Remove("pendingWatchdog"); fallback["pendingNativeProcess"] = new JsonObject { ["processId"] = 123, ["startedUtc"] = "2026-10-27T09:41:48Z" };
        File.WriteAllText(journalPath, fallback.ToJsonString());
        Check(Probe().ServerRecoveryPending == true, "Journal-only native-process ownership also blocks inactive status");
        fallback.AsObject().Remove("pendingNativeProcess"); fallback["recoveryRequired"] = true; File.WriteAllText(journalPath, fallback.ToJsonString());
        Check(Probe().ServerRecoveryPending == true, "Owner recoveryRequired alone is sufficient negative evidence");
        File.WriteAllText(journalPath, RetainedFixture("last-operation.json"));
        File.WriteAllText(Path.Combine(stateRoot, "pending-process.json"), "{}");
        Check(Probe().ServerRecoveryPending == true, "Separate pending native-process marker cannot be ignored");
        File.Delete(Path.Combine(stateRoot, "pending-process.json"));
        foreach (var contents in new[] { "{invalid", "{}", "[]", "[" + RetainedFixture("last-operation.json") + "]", new string(' ', 1048577),
            RetainedFixture("last-operation.json").Replace("\"recoveryRequired\":  false", "\"recoveryRequired\":  \"false\"") })
        {
            File.WriteAllText(journalPath, contents);
            var unknown = Probe();
            Check(unknown.ServerRecoveryPending is null && EnvironmentSnapshot.From(unknown, DateTimeOffset.UtcNow).State != EnvironmentInstallationState.Inactive,
                "Malformed, unsupported, oversized or ambiguous owner recovery journal stays unknown");
        }
        File.WriteAllText(journalPath, RetainedFixture("last-operation.json"));
        using (var locked = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(Probe().ServerRecoveryPending is null, "Unreadable owner recovery journal cannot become false");
        File.Delete(journalPath);
        Check(Probe().ServerRecoveryPending is null, "Missing owner recovery journal cannot establish safe inactive state");
        File.WriteAllText(journalPath, RetainedFixture("last-operation.json"));
        File.WriteAllText(retainedPath, "{invalid");
        Check(EnvironmentSnapshot.From(Probe(), DateTimeOffset.UtcNow).State != EnvironmentInstallationState.Inactive,
            "Unreadable retained owner record cannot be accepted by the production reader");
        File.Delete(retainedPath);
        Check(EnvironmentSnapshot.From(Probe(), DateTimeOffset.UtcNow).State != EnvironmentInstallationState.Inactive,
            "Absent retained owner record cannot be inferred by the production reader");
        File.WriteAllText(receiptPath, "{invalid");
        Check(EnvironmentSnapshot.From(Probe(), DateTimeOffset.UtcNow).State == EnvironmentInstallationState.Unknown,
            "Unreadable component receipt preserves the existing Unknown state");
    }

    private static void EnvironmentRetainedUiTests(string? output)
    {
        var previousRenderMode = RenderOptions.ProcessRenderMode;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        if (output is null) Console.WriteLine("Pixel capture not requested; layout assertions do not establish visual acceptance.");
        var window = new MainWindow(Path.Combine(Temporary, "retained-ui"), true); window.Cards.Clear();
        var card = new AppCard(Catalog.Apps.Single(a => a.IsEnvironment))
        {
            Installed = new("C:\\Fixture\\Kiloview-Environment-Setup.exe", "3.1.0"),
            Snapshot = new([Release("v3.1.0")], DateTimeOffset.UtcNow), EnvironmentStatus = EnvironmentSnapshot.From(RetainedEnvironment(), DateTimeOffset.UtcNow)
        };
        card.Snapshot!.Releases[0].Assets = [new() { Name = "Kiloview-Environment-Setup.exe", Size = 100,
            DownloadUrl = "https://github.com/JohnDevAc/Kiloview-Environment-Setup/releases/download/v3.1.0/Kiloview-Environment-Setup.exe",
            Digest = "sha256:" + new string('a', 64) }];
        window.Cards.Add(card);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coordinator = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", flags)!.GetValue(window)!;
        var refresh = typeof(MainWindow).GetMethod("UpdateOperationCards", flags)!;
        var operation = RetainedOperation(); coordinator.Operations.Add(operation);
        var before = JsonSerializer.Serialize(operation, OwnedPaths.Json);
        var errors = new StringWriter(); using var trace = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        var root = (FrameworkElement)window.Content; window.Content = null; root.DataContext = window;
        // Give WPF a real presentation source without displaying or activating a
        // desktop window; detached layout alone does not guarantee painted pixels.
        using var presentation = new HwndSource(new HwndSourceParameters("Retained environment preview")
        { Width = 640, Height = 950, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        presentation.RootVisual = root;
        try
        {
            refresh.Invoke(window, []);
            Check(!card.OperationFailed && !card.OperationPending && !card.RequiresCompleteRepair && card.CanLaunch && !card.NeedsSetup &&
                JsonSerializer.Serialize(operation, OwnedPaths.Json) == before,
                "Real display refresh consumes captured terminal success without changing owner journal or demanding repair");
            foreach (var details in new[] { false, true }) foreach (var scale in new[] { 1d, 2d, 2.5d })
            {
                card.DetailsExpanded = details; card.Recompute();
                var size = new Size(640, details ? 950 : 580); window.SetLayoutWidth(size.Width);
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); root.UpdateLayout();
                var text = Descendants<TextBlock>(root).Where(Rendered).Select(t => t.Text).ToArray();
                Check(text.Contains("Server · Inactive · data retained") && !text.Any(t => t.Contains("Setup incomplete") || t.Contains("not verified")) &&
                    !Descendants<Button>(root).Any(b => Rendered(b) && b.Content?.ToString() == "Complete setup") &&
                    Descendants<Button>(root).Any(b => Rendered(b) && b.Content?.ToString() == "Open setup" && b.IsEnabled),
                    "Retained card " + scale + "/details=" + details + " shows neutral state and usable Open setup");
                Check(Descendants<ScrollViewer>(root).First().ScrollableWidth < 1 && Descendants<ScrollViewer>(root).First().ScrollableHeight < 1,
                    "Retained card fits at " + scale + "/details=" + details);
                var bounds = new Rect(size);
                Check(root.IsVisible && Descendants<Button>(root).Where(Rendered).All(button => button.IsVisible && bounds.Contains(button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize)))),
                    "Retained card actions have visible bounds at " + scale + "/details=" + details);
                if (output is null) continue;
                var controlDrawing = new DrawingVisual();
                using (var context = controlDrawing.RenderOpen()) context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 32, 32));
                var controlBitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); controlBitmap.Render(controlDrawing);
                var controlPixels = new int[1024]; controlBitmap.CopyPixels(controlPixels, 128, 0);
                Console.WriteLine($"Independent red-rectangle render: opaque={controlPixels.Count(pixel => (uint)pixel >> 24 > 0)}/1024");
                var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Console.WriteLine($"Retained render: visible={root.IsVisible}, size={root.RenderSize}, opaque={pixels.Count(pixel => (uint)pixel >> 24 > 0)}/{pixels.Length}, colors={pixels.Distinct().Take(64).Count()}");
                Check(pixels.Count(pixel => (uint)pixel >> 24 > 0) > pixels.Length / 2 && pixels.Distinct().Take(64).Count() == 64,
                    "Retained card PNG has opaque UI and varied painted content at " + scale + "/details=" + details);
                Directory.CreateDirectory(output);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "environment-retained-" + (details ? "details-" : "") + scale * 100 + ".png")); encoder.Save(file);
            }
            operation.ExitCode = 1; operation.Result = operation.Result! with { Outcome = "partial", RecoveryRequired = true, Message = "Server removal incomplete." };
            refresh.Invoke(window, []);
            Check(card.OperationFailed && card.RequiresCompleteRepair && card.RepairIsPrimary && card.Status == "Repair required",
                "Actual partial owner result still exposes repair even if retained records survive a late failure");
            operation.ExitUnconfirmed = true; operation.RequiresReview = true; operation.Result = operation.Result with { Outcome = "succeeded", RecoveryRequired = false };
            refresh.Invoke(window, []);
            Check(card.OperationPending && card.MutationBlocked && !card.CanLaunch && card.Status == "Operation needs attention",
                "Unconfirmed owner exit retains the existing pending lock and Recheck requirement");
            Check(errors.ToString().Length == 0, "Retained-role WPF display has no binding errors");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); presentation.RootVisual = null; window.Content = root; window.Close(); RenderOptions.ProcessRenderMode = previousRenderMode; }
    }
}
