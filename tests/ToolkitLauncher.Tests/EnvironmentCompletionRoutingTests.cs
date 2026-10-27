using System.IO;
using System.Reflection;
using System.Text.Json;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void EnvironmentCompletionRoutingTests()
    {
        var root = Path.Combine(Temporary, "environment-completion");
        var capture = Path.Combine(root, "last-request.json");
        var capabilities = FixtureAdapter(root, "environment", EmitSuccess.Replace("action='install'", "action=$r.action")
            + "\nCopy-Item -LiteralPath $LifecycleRequestPath -Destination " + PsQuote(capture) + " -Force");
        var executable = Path.Combine(root, "Kiloview-Environment-Setup.exe");
        File.Copy(typeof(MainWindow).Assembly.Location, executable);
        var definition = Catalog.Apps.Single(a => a.IsEnvironment) with { KnownPaths = [executable], RegistryNames = [], MaintenanceDirectories = [] };
        var window = new MainWindow(Path.Combine(root, "toolkit-state"), true); window.Cards.Clear();
        var card = new AppCard(definition) { Installed = new(executable, "1.4.0") { Capabilities = capabilities } };
        try
        {
            foreach (var role in new[] { "Client", "Server", "Client + Server" })
            {
                card.EnvironmentStatus = new(EnvironmentInstallationState.Partial, [], DateTimeOffset.UtcNow, "Fixture incomplete role", true) { RoleLabel = role };
                card.Recompute(); window.Cards.Add(card);
                typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
                File.Delete(capture);
                Check(card.InstallText == "Complete setup", "Partial " + role + " exposes completion");
                var completion = (Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!;
                // The real dispatch is already underway. Avoid host Environment inspection
                // after this harmless producer exits; the captured request is the boundary.
                window.Cards.Clear(); Pump(completion);
                var request = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(capture), OwnedPaths.Json)!;
                Check(request.Action == "install" && request.Role is null && request.RequestedVersion is null && request.PreserveData,
                    "Partial " + role + " completion sends install, preserves data and leaves role selection with its owner");
            }
            window.Cards.Add(card);
            typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
            File.Delete(capture);
            var repair = (Task)typeof(MainWindow).GetMethod("MaintainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card, "repair", true])!;
            window.Cards.Clear(); Pump(repair);
            var explicitRepair = JsonSerializer.Deserialize<LifecycleRequest>(File.ReadAllText(capture), OwnedPaths.Json)!;
            Check(explicitRepair.Action == "repair" && explicitRepair.Role is null && explicitRepair.RequestedVersion is null,
                "Explicit Environment Repair preserves the owner repair semantics without forcing a role");
        }
        finally { window.Close(); }
    }
}
