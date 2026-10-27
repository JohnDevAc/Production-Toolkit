using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void ActiveOperationDisplayTests(string? output = null)
    {
        var window = new MainWindow(Path.Combine(Temporary, "active-operation-ui"), true);
        window.Cards.Clear();
        var card = new AppCard(Catalog.Apps.Single(a => a.IsJob))
        {
            Installed = new("C:\\Example\\job.exe", "0.8.16"),
            Snapshot = new([Release("v0.8.16")], DateTimeOffset.UtcNow),
            Busy = true, InstallerRunning = true,
            Activity = "Installer open · complete the steps in its window."
        };
        window.Cards.Add(card);
        var lifecycle = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var refresh = typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var producer = Process.GetCurrentProcess();
        var started = producer.StartTime.ToUniversalTime();
        var operation = lifecycle.Store.Create("job", "update", "protocol");
        operation.ProcessId = producer.Id; operation.ProcessStartedUtc = started; operation.ProcessActive = true;
        lifecycle.Operations.Add(operation);
        var root = (FrameworkElement)window.Content;
        window.Content = null; root.DataContext = window;
        var bindingErrors = new StringWriter();
        var trace = new TextWriterTraceListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);

        void Refresh()
        {
            var before = JsonSerializer.Serialize(operation, OwnedPaths.Json);
            refresh.Invoke(window, []);
            if (before != JsonSerializer.Serialize(operation, OwnedPaths.Json)) throw new Exception("Display changed operation evidence");
        }
        void Publish(string outcome, string? stage = null, bool? recovery = null)
        {
            OwnedPaths.Write(operation.Request.ResultPath, Result(operation, outcome) with
            { Stage = stage, RecoveryRequired = recovery, Message = "Owner " + outcome, NextAction = recovery == true ? "Use the complete repair package." : null });
            lifecycle.Store.Reconcile(operation); Refresh();
        }
        void Render(string label)
        {
            if (output is null) return;
            foreach (var scale in new[] { 1d, 2d, 2.5d })
            {
                var size = new Size(640, card.DetailsExpanded ? 900 : 520);
                window.SetLayoutWidth(size.Width);
                root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                var text = Descendants<TextBlock>(root).Where(Rendered).Select(t => t.Text).ToArray();
                Check(text.Contains(card.Status) && text.Count(t => t == card.ShortReason) == 1 &&
                    !text.Any(t => t.Contains("Recheck") || t.Contains("needs attention") || t.Contains("Installer open")) &&
                    !Descendants<Button>(root).Any(b => Rendered(b) && b.Content?.ToString() == "Recheck"),
                    label + " " + scale + ": real card renders active status and one instruction without recovery wording");
                Check(Descendants<ScrollViewer>(root).First().ScrollableWidth < 1 &&
                    Descendants<ScrollViewer>(root).First().ScrollableHeight < 1,
                    label + " " + scale + ": active card fits without scrolling");
                var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, label + "-" + scale * 100 + ".png")); encoder.Save(file);
            }
        }
        try
        {
            Publish("running", "installing");
            Check(card.Status == "Setup in progress" && !card.ShortReason.Contains("Recheck"),
                "Verified active Job update is progress, not an attention or recheck state");
            Check(card.OperationPending && lifecycle.MutationBlocked && !card.CanInstall && !card.CanLaunch &&
                card.ActivitySummary == "" && card.InlineReason == "Owner running",
                "Active display preserves locks and owner details without duplicate installer instructions");
            Render("active-setup");

            Publish("running", "waiting-input");
            Check(card.Status == "Waiting for input" && card.ShortReason == "Follow the steps in the installer window.",
                "Validated waiting-input uses concise installer-window guidance");
            Render("waiting-input");

            card.DetailsExpanded = true; Render("waiting-input-details"); card.DetailsExpanded = false;
            card.Busy = false; card.InstallerRunning = false; Refresh();
            Check(card.Status == "Waiting for input" && !card.ShowRecheck && lifecycle.MutationBlocked,
                "Recovered live producer identity remains active without relying on this window's Busy flag");
            operation.ProcessStartedUtc = started.AddSeconds(-1); Refresh();
            Check(card.Status == "Operation needs attention" && card.ShortReason.Contains("Recheck") && lifecycle.MutationBlocked,
                "Reused or stale PID identity cannot turn a saved running heartbeat into active status");
            operation.ProcessStartedUtc = started;
            operation.ProcessId = int.MaxValue; Refresh();
            Check(card.Status == "Operation needs attention", "Missing producer cannot appear active from a saved process flag");
            operation.ProcessId = producer.Id;
            operation.ProcessActive = false; Refresh();
            Check(card.Status == "Operation needs attention" && card.OperationPending,
                "Running result after producer exit stays unconfirmed with recheck guidance");

            operation.ProcessActive = true;
            Publish("succeeded");
            Check(card.Status == "Setup in progress" && card.OperationPending && lifecycle.MutationBlocked,
                "Terminal success before producer exit cannot display Up to date or release the lock");
            operation.ProcessActive = false; operation.ExitCode = 0; Refresh();
            Check(card.Status == "Up to date" && !card.OperationPending && !lifecycle.MutationBlocked,
                "Verified terminal success and exit restore normal version status");

            operation.ProcessActive = true; operation.ExitCode = null;
            Publish("running", "waiting-input");
            File.WriteAllText(operation.Request.ResultPath, "{broken"); lifecycle.Store.Reconcile(operation); Refresh();
            Check(operation.RequiresReview && card.Status == "Operation needs attention" && card.InlineReason.Contains("not verified"),
                "Invalid owner result overrides stale waiting-input even with a live process");
            Publish("succeeded"); operation.ProcessActive = false; operation.ExitCode = 17;
            lifecycle.Store.Reconcile(operation); Refresh();
            Check(operation.RequiresReview && card.OperationPending && card.Status == "Operation needs attention",
                "Success and nonzero-exit conflict keeps review status and mutation lock");
            operation.ExitCode = null; operation.ExitUnconfirmed = true;
            lifecycle.Store.Reconcile(operation); Refresh();
            Check(card.Status == "Operation needs attention" && operation.RequiresReview,
                "Success with unconfirmed exit after reopen never appears active or complete");
            operation.ExitUnconfirmed = false; operation.ProcessActive = true;
            Publish("restart-required");
            Check(card.ShortReason == "Restart Windows, then Recheck." && card.OperationPending && lifecycle.MutationBlocked,
                "Restart-required keeps its exact guidance and lock even before producer exit");

            foreach (var outcome in new[] { "failed", "blocked", "partial", "cancelled" })
            {
                Publish(outcome, recovery: outcome == "partial");
                Check(card.Status == "Operation needs attention" && card.OperationPending,
                    "Terminal " + outcome + " is never hidden by active-process wording");
            }
            operation.ProcessActive = false; Publish("partial", recovery: true);
            Check(card.RequiresCompleteRepair && card.Status == "Repair required" && card.InstallText == "Repair" &&
                card.InlineReason.Contains("complete repair package") && !card.OperationPending,
                "Completed partial result retains owner recovery guidance and guarded Repair");

            lifecycle.Operations.Clear();
            operation = lifecycle.Store.Create("job", "update", "protocol");
            lifecycle.Operations.Add(operation); operation.Finished = true; operation.RequiresReview = true; operation.ExitCode = 0;
            Refresh();
            Check(card.Status == "Operation needs attention" && card.ShortReason.Contains("Recheck") && lifecycle.MutationBlocked,
                "Exit zero with no result stays unconfirmed rather than active or successful");

            foreach (var transport in new[] { "interactive-setup", "legacy" })
            {
                lifecycle.Operations.Clear(); window.Cards.Clear();
                card = new(Catalog.Apps.Single(a => a.IsPcAgent)) { Busy = true, InstallerRunning = true };
                window.Cards.Add(card);
                operation = lifecycle.Store.Create("pc-agent", "install", transport);
                operation.ProcessActive = true; operation.ProcessId = producer.Id; operation.ProcessStartedUtc = started;
                lifecycle.Operations.Add(operation); Refresh();
                Check(card.Status == "Setup in progress" && !card.ShortReason.Contains("Recheck") && lifecycle.MutationBlocked && operation.Result is null,
                    "Live " + transport + " uses progress without inventing an owner result");
                Check(!card.ShowCompact, "Active " + transport + " exposes its status even before an installation is detected");
                if (transport == "interactive-setup") Render("active-native-setup");
            }
            Check(bindingErrors.ToString().Length == 0, "Active and waiting-input WPF states have no binding errors");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); trace.Dispose();
            card.Busy = false; card.InstallerRunning = false; window.Close();
        }
    }
}
