using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void EnvironmentOpenSetupTests()
    {
        var root = Path.Combine(Temporary, "environment-open-setup"); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "fixture.cs"); var native = Path.Combine(root, "fixture.exe");
        File.WriteAllText(source, """
            using System; using System.IO; using System.Collections.Generic; using System.Web.Script.Serialization;
            [assembly: System.Reflection.AssemblyFileVersion("3.1.0.0")]
            class Fixture { static int Main(string[] args) {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                var json = new JavaScriptSerializer();
                File.WriteAllText(Path.Combine(root,"arguments.json"),json.Serialize(args));
                string outcome = File.ReadAllText(Path.Combine(root,"outcome.txt"));
                if(args.Length==0) return outcome=="cancelled"?1602:0;
                if(args.Length!=2 || args[0]!="--lifecycle-request") return 17;
                string text = File.ReadAllText(args[1]);
                var request = json.Deserialize<Dictionary<string,object>>(text);
                File.WriteAllText(Path.Combine(root,"captured-request.json"),text);
                bool inspect = (string)request["action"]=="inspect";
                if(outcome=="missing" && !inspect) return 0;
                if(inspect) outcome="succeeded";
                File.WriteAllText((string)request["resultPath"],json.Serialize(new {
                    schemaVersion=1,operationId=request["operationId"],productId=request["productId"],action=request["action"],
                    outcome=outcome,updatedUtc=DateTimeOffset.UtcNow.ToString("o"),message="Harmless native fixture "+outcome,
                    recoveryRequired=false,safeToRetry=inspect }));
                return outcome=="restart-required"?3010:outcome=="cancelled"?1602:0;
            } }
            """);
        var compiler = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/nologo", "/target:winexe", "/r:System.Web.Extensions.dll", "/out:" + native, source }) compiler.ArgumentList.Add(arg);
        using (var build = Process.Start(compiler)!) Check(build.WaitForExit(15000) && build.ExitCode == 0, "Harmless native Environment routing fixture compiles");

        foreach (var scenario in new[] { "modern", "retained", "cancelled", "restart-required", "missing", "legacy", "unsupported", "malformed", "recovery" })
        {
            var folder = Path.Combine(root, scenario); Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "Kiloview-Environment-Setup.exe"); File.Copy(native, executable);
            var outcome = scenario is "cancelled" or "restart-required" or "missing" ? scenario : "succeeded";
            File.WriteAllText(Path.Combine(folder, "outcome.txt"), outcome);
            if (scenario != "legacy")
                OwnedPaths.Write(Path.Combine(folder, "toolbox-lifecycle.json"), new {
                    schemaVersion = 1, productId = "environment", entryPoint = Path.GetFileName(executable), entryPointKind = "native",
                    supportedActions = scenario == "unsupported" ? new[] { "inspect", "repair" } : new[] { "install", "repair", "inspect" }
                });
            if (scenario == "malformed") File.WriteAllText(Path.Combine(folder, "toolbox-lifecycle.json"), "{broken");
            var definition = Catalog.Apps.Single(a => a.IsEnvironment) with
            { KnownPaths = scenario == "retained" ? [] : [executable], RegistryNames = [], MaintenanceDirectories = [folder] };
            var stateRoot = Path.Combine(folder, "state");
            var window = new MainWindow(stateRoot, true); window.Cards.Clear();
            var installed = InstallationService.Find(definition, null, null)!;
            var card = new AppCard(definition) { Installed = installed, RequiresCompleteRepair = scenario == "recovery",
                EnvironmentStatus = new(EnvironmentInstallationState.Unknown, [], DateTimeOffset.UtcNow, "No health evidence", true) { RoleLabel = "Client + Server" } };
            var lifecycle = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            try
            {
                window.Cards.Add(card);
                var task = (Task)typeof(MainWindow).GetMethod("LaunchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!;
                // Keep this actual Launch dispatch isolated from host component detection.
                window.Cards.Clear(); Pump(task);
                var argumentsPath = Path.Combine(folder, "arguments.json");
                if (scenario is "malformed" or "recovery")
                {
                    Check(!File.Exists(argumentsPath) && lifecycle.Operations.Count == 0 && card.Activity.Length > 0,
                        "Environment Open setup cannot bypass " + scenario + " maintenance evidence");
                    continue;
                }
                var operation = lifecycle.Operations.Single();
                var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(argumentsPath))!;
                if (scenario is "legacy" or "unsupported")
                {
                    Check(arguments.Length == 0 && operation.Transport == "legacy" && operation.Result is null && operation.ExitCode == 0 && !operation.Pending,
                        "Environment " + scenario + " fallback opens normal native setup without fabricating completion");
                    Check(operation.Message.Contains("does not report completion or health") && card.EnvironmentStatus.State == EnvironmentInstallationState.Unknown,
                        "Environment " + scenario + " exit zero keeps truthful health uncertainty");
                    continue;
                }
                Check(operation.Transport == "protocol" && operation.Request.Action == "install" && arguments.SequenceEqual(new[] { "--lifecycle-request", lifecycle.Store.RequestPath(operation) }),
                    "Environment " + scenario + " Open setup uses the admitted native install protocol");
                var captured = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(Path.Combine(folder, "captured-request.json")), OwnedPaths.Json)!;
                Check(captured.OperationId == operation.Request.OperationId && captured.Role is null && captured.RequestedVersion is null && captured.PreserveData,
                    "Environment " + scenario + " retains correlation/data and leaves role/version unspecified");
                if (scenario == "retained") Check(installed.IsMaintenanceOnly && !installed.IsPresent, "Retained Environment maintenance supports normal install routing");
                if (scenario == "cancelled")
                {
                    Check(operation.Result?.Outcome == "cancelled" && operation.ExitCode == 1602 && !operation.Pending && !lifecycle.MutationBlocked,
                        "Native Environment cancellation is terminal without inferred health or pending review");
                    var reopened = new LifecycleCoordinator(lifecycle.Store.Root);
                    Check(reopened.Operations.Single().Request.OperationId == captured.OperationId && !reopened.MutationBlocked && reopened.Operations.Single().Result?.Outcome == "cancelled",
                        "Cancelled Environment request remains terminal after reopening its existing journal");
                }
                else if (scenario == "missing")
                    Check(operation.Result is null && operation.ExitCode == 0 && operation.RequiresReview && operation.Pending && card.EnvironmentStatus.State == EnvironmentInstallationState.Unknown,
                        "Modern Environment exit zero without a result stays unconfirmed");
                else if (scenario == "restart-required")
                {
                    var requestBytes = File.ReadAllBytes(lifecycle.Store.RequestPath(operation));
                    var reopened = new LifecycleCoordinator(lifecycle.Store.Root);
                    var pending = reopened.Operations.Single();
                    Check(pending.Request.OperationId == captured.OperationId && pending.Result?.Outcome == "restart-required" && pending.Pending && reopened.MutationBlocked,
                        "Environment restart reopens the original durable request and mutation lock");
                    Check(File.ReadAllBytes(reopened.Store.RequestPath(pending)).SequenceEqual(requestBytes), "Environment restart preserves the exact original request bytes");
                    window.Cards.Add(card);
                    typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
                    Pump((Task)typeof(MainWindow).GetMethod("LaunchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!);
                    window.Cards.Clear();
                    Check(lifecycle.Operations.Count == 1 && !card.CanLaunch,
                        "Pending Environment restart blocks Open setup from creating a replacement request");
                    // Simulate the owner resuming the same request, without inventing a
                    // post-reboot process exit. Inspection must still reconcile it safely.
                    OwnedPaths.Write(pending.Request.ResultPath, Result(pending) with { Attempt = 2, RecoveryRequired = false });
                    reopened.Reconcile();
                    Check(reopened.Operations.Count == 1 && pending.Result?.Attempt == 2 && pending.Pending && pending.RequiresReview,
                        "Resumed owner result is read from the original operation and does not guess exit evidence");
                    Pump(reopened.RunAsync("environment", "inspect", installed.Capabilities, null, null, default));
                    reopened.ReleaseAfterInspect("environment", reopened.Operations.Last());
                    Check(!pending.Pending && !reopened.MutationBlocked && pending.ReconciledBy is not null && pending.Request.OperationId == captured.OperationId &&
                        File.ReadAllBytes(reopened.Store.RequestPath(pending)).SequenceEqual(requestBytes),
                        "Owner-safe inspection reconciles resumed Environment state without replacing its original request");
                }
                else Check(operation.Result?.IsSuccess == true && operation.ExitCode == 0 && !operation.Pending,
                    "Environment " + scenario + " install completes only with a correlated owner result and exit");

                if (scenario == "modern")
                {
                    window.Cards.Add(card);
                    typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
                    var repair = (Task)typeof(MainWindow).GetMethod("MaintainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card, "repair", true])!;
                    window.Cards.Clear(); Pump(repair);
                    var repairRequest = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(Path.Combine(folder, "captured-request.json")), OwnedPaths.Json)!;
                    Check(repairRequest.Action == "repair" && repairRequest.Role is null && repairRequest.PreserveData,
                        "Explicit Environment Repair still dispatches repair rather than normal install");
                }
            }
            finally { card.Busy = false; card.InstallerRunning = false; window.Close(); }
        }
    }
}
