using System.IO;
using System.Reflection;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void PcAgentRuntimeRecoveryTests()
    {
        foreach (var path in new[] { "", " ", "relative.exe", "C:\\bad\0path.exe", Path.Combine(Temporary, "missing-agent.exe") })
            Check(PcAgentRuntime.Read(path) is null, "Absent or invalid Agent identity stays unknown: " + path.Replace('\0', '?'));

        var root = Path.Combine(Temporary, "agent-removal-runtime");
        var executable = Path.Combine(root, "NDI Configurator PC Agent.exe");
        var capabilities = FixtureAdapter(root, "pc-agent", EmitSuccess.Replace("outcome='succeeded'",
            "outcome='partial';recoveryRequired=$true;nextAction='Run repair from the complete Agent package to recover'") + "\nexit 1");
        var definition = Catalog.Apps.Single(a => a.IsPcAgent) with { KnownPaths = [executable], RegistryNames = [], MaintenanceDirectories = [] };
        var stateRoot = Path.Combine(root, "toolkit");
        var window = new MainWindow(stateRoot, false, false) { Opacity = 0, ShowInTaskbar = false, IsHitTestVisible = false };
        window.Cards.Clear();
        var card = new AppCard(definition); window.Cards.Add(card);
        var lifecycle = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("pcAgentRuntimeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var reader = typeof(MainWindow).GetField("pcAgentRuntimeReader", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Pump(lifecycle.RunAsync("pc-agent", "uninstall", capabilities, null, null, default));
            var operation = lifecycle.Operations.Single();
            var resultBefore = File.ReadAllBytes(operation.Request.ResultPath);
            Pump(window.RefreshInstalledAsync());
            Check(card.Installed is { Path.Length: 0, IsPresent: false, Capabilities: not null } && card.OperationOutcome == "partial" && card.OperationFailed,
                "Actual partial owner result retains maintenance discovery without an Agent executable");
            var ticks = 0; timer.Interval = TimeSpan.FromMilliseconds(25); timer.Tick += (_, _) => ticks++;
            window.Show();
            Pump(WaitUntilAsync(() => ticks >= 3, TimeSpan.FromSeconds(5)));
            timer.Stop();
            Check(window.IsLoaded && !card.ShowPcAgentRuntime && !card.CanLaunch && card.CanRepair && card.CanRecheck && card.CanUninstall,
                "Real WPF timer survives recovery-only Agent and keeps recovery controls usable");
            Check(operation.Result is { Outcome: "partial", RecoveryRequired: true } && card.OperationStatus.Contains("complete Agent package") &&
                File.ReadAllBytes(operation.Request.ResultPath).SequenceEqual(resultBefore),
                "Runtime polling preserves the owner's partial outcome and recovery instructions");

            File.Copy(typeof(MainWindow).Assembly.Location, executable);
            Pump(window.RefreshInstalledAsync());
            var prior = card.Installed!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            reader.SetValue(window, (Func<string, bool?>)(_ => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Runtime fixture gate"); return true; }));
            var poll = window.RefreshPcAgentRuntimeAsync();
            try
            {
                Pump(WaitUntilAsync(() => entered.IsSet, TimeSpan.FromSeconds(5)));
                File.Delete(executable);
                Pump(window.RefreshInstalledAsync());
                var recovered = card.Installed;
                window.Cards.Add(new AppCard(Catalog.Apps.Single(a => a.Id == "testcard")));
                release.Set(); Pump(poll);
                Check(ReferenceEquals(card.Installed, recovered) && card.Installed is { IsPresent: false } && card.CanRepair && !card.PcAgentRunning,
                    "In-flight runtime result cannot overwrite removal discovery or fail when the card collection changes");
            }
            finally { release.Set(); Pump(poll); }

            reader.SetValue(window, (Func<string, bool?>)PcAgentRuntime.Read);
            card.Installed = prior;
            Pump(window.RefreshPcAgentRuntimeAsync());
            Check(card.Installed is { IsPresent: false, Capabilities: not null } && !card.CanLaunch && card.CanRecheck,
                "Executable removed between discovery and polling is rediscovered as retained maintenance");

            File.Copy(typeof(MainWindow).Assembly.Location, executable);
            Pump(window.RefreshInstalledAsync());
            reader.SetValue(window, (Func<string, bool?>)(_ => throw new IOException("Runtime inspection fixture failure")));
            Pump(window.RefreshPcAgentRuntimeAsync());
            Check(card.Installed!.PcAgentRunning is null && card.OperationOutcome == "partial" && card.CanRepair &&
                File.ReadAllText(Path.Combine(stateRoot, "activity.log")).Contains("Runtime inspection fixture failure"),
                "Unexpected async runtime inspection failure stays unknown, logged, and preserves recovery");
            reader.SetValue(window, (Func<string, bool?>)PcAgentRuntime.Read);
            Pump(window.RefreshPcAgentRuntimeAsync());
            Check(card.Installed!.PcAgentRunning == false && window.IsLoaded,
                "Runtime polling resumes normally after a failed check");
        }
        finally { timer.Stop(); window.Close(); }
    }
}
