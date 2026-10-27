using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
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
    private static void PortableGuidanceTests(string? output = null)
    {
        PortableRepairPolicyTests();
        var state = new LocalState(Path.Combine(Temporary, "portable-guidance"));
        var bytes = TestCardZip(); var asset = TestCardAsset(bytes);
        using var gate = new PortableResponseGate(bytes);
        using var http = new HttpClient(gate);
        var networkAttempts = 0;
        using var offline = new HttpClient(new FakeHandler(_ => { networkAttempts++; throw new IOException("Offline fixture"); }));
        var window = new MainWindow(state.Root, true); window.Cards.Clear();
        var release = Release("v" + InstallationService.ReadVersion(typeof(MainWindow).Assembly.Location)); release.Assets = [asset];
        var card = new AppCard(TestCardDefinition) { Snapshot = new([release], DateTimeOffset.UtcNow) }; window.Cards.Add(card);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var refresh = typeof(MainWindow).GetMethod("UpdateOperationCards", flags)!;
        var packages = typeof(MainWindow).GetField("packages", flags)!;
        var coordinator = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", flags)!.GetValue(window)!;
        packages.SetValue(window, new PackageService(http, state.DownloadRoot));
        var bindingErrors = new StringWriter(); var trace = new TextWriterTraceListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);

        void Refresh()
        {
            var before = JsonSerializer.Serialize(coordinator.Operations, OwnedPaths.Json);
            refresh.Invoke(window, []);
            Check(before == JsonSerializer.Serialize(coordinator.Operations, OwnedPaths.Json), "Portable display preserves all operation evidence");
        }
        Task Maintain(string action) => (Task)typeof(MainWindow).GetMethod("MaintainAsync", flags)!.Invoke(window, [card, action, true])!;
        void Render(string label, bool active = false, bool rechecked = false)
        {
            if (output is null) return;
            var root = (FrameworkElement)window.Content; window.Content = null; root.DataContext = window;
            try
            {
                foreach (var scale in new[] { 1d, 2d, 2.5d })
                {
                    var size = new Size(640, card.DetailsExpanded ? 900 : 580); window.SetLayoutWidth(size.Width);
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); root.UpdateLayout();
                    var text = Descendants<TextBlock>(root).Where(Rendered).Select(t => t.Text).ToArray();
                    if (card.RepairIsPrimary)
                        Check(Descendants<Button>(root).Count(b => Rendered(b) && b.Content?.ToString() == "Repair") == 1 &&
                            Descendants<Button>(root).Single(b => Rendered(b) && b.Content?.ToString() == "Repair").IsEnabled == card.CanInstall,
                            label + " " + scale + ": exactly one primary Repair, including expanded Details");
                    Check(text.Contains(card.Status) && !text.Any(t => t.Contains(AppCard.RepairPackageNeeded)) &&
                        (!active || text.Contains(card.ActivitySummary) && !text.Any(t => t.Contains("Recheck") || t.Contains("installer window") || t.Contains("needs attention"))) &&
                        (!rechecked || text.Contains(card.Installed!.ReadinessIssue) && !text.Any(t => t.Contains("Close TestCard") || t.Contains("Setup needs attention"))),
                        label + " " + scale + ": rendered card shows current portable guidance");
                    Check(Descendants<ScrollViewer>(root).First().ScrollableWidth < 1 && Descendants<ScrollViewer>(root).First().ScrollableHeight < 1,
                        label + " " + scale + ": portable card fits without scrolling");
                    var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, label + "-" + scale * 100 + ".png")); encoder.Save(file);
                }
            }
            finally { window.Content = root; }
        }
        try
        {
            var unpackWasActive = false;
            card.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AppCard.Activity) && card.Activity == "Unpacking application…")
                    unpackWasActive = card.PortableOperationActive && card.ShortReason == "" && card.ActivitySummary == card.Activity &&
                        !card.ShowPendingRecheck && !card.ShowRecheck && coordinator.MutationBlocked;
            };
            var install = (Task)typeof(MainWindow).GetMethod("PrepareAsync", flags)!.Invoke(window, [card])!;
            Pump(WaitUntilAsync(() => gate.Started.Task.IsCompleted, TimeSpan.FromSeconds(5)));
            Check(!install.IsCompleted && card.PortableOperationActive && card.Status == "In progress" && card.ShortReason == "" &&
                card.ActivitySummary.Length > 0 && card.OperationStatus == "" && !card.ShowCompact && !card.CanLaunch && !card.CanInstall,
                "Real portable preparation shows activity without pending recovery or installer-window wording");
            Render("portable-preparing", active: true);
            gate.Release.TrySetResult(); Pump(install);
            Check(unpackWasActive, "Actual unpack progress remains visible while the portable mutation is locked");
            Check(!card.OperationActive && !card.PortableOperationActive && !coordinator.MutationBlocked && card.CanLaunch,
                "Completed portable preparation clears only the transient activity state");

            packages.SetValue(window, new PackageService(offline, state.DownloadRoot));
            var originalPath = card.Installed!.Path;
            File.Delete(Path.Combine(Path.GetDirectoryName(originalPath)!, ".runtime", "dotnet.exe"));
            var settingsPath = Path.Combine(state.Root, "settings.json");
            if (File.Exists(settingsPath)) File.Move(settingsPath, settingsPath + ".retained");
            card.Installed = new InstallationService(new LocalState(state.Root)).Find(TestCardDefinition);
            card.Snapshot = null;
            var failed = coordinator.Begin("testcard", "uninstall", "portable");
            failed.Result = Result(failed, "partial") with { Message = "Close TestCard before removing its files.", RecoveryRequired = true };
            failed.Finished = true; coordinator.Store.Save(failed); card.Activity = failed.Result.Message; Refresh();
            var journalPath = Path.Combine(coordinator.Store.DirectoryFor(failed.Request.OperationId), "journal.json");
            var journal = File.ReadAllBytes(journalPath);
            Check(card.Installed?.Path == originalPath && card.NeedsPortableRepair && !card.CanLaunch && card.CanRepair &&
                card.RequiresCompleteRepair && card.HasCompleteRepairPackage && card.ShortReason == "" && card.DetailNote.Contains("recorded portable package"),
                "Receipt rediscovery offers recorded package Repair without a release feed or complete-installer warning");
            Check(card.OperationFailed && card.InlineReason.Contains("Close TestCard"), "Unrechecked removal failure remains visible");
            card.DetailsExpanded = true; Render("portable-repair-recorded"); card.DetailsExpanded = false;
            var receiptPath = Path.Combine(state.Root, "Receipts", "testcard.json"); var receipt = File.ReadAllBytes(receiptPath);
            var files = Directory.GetFiles(state.ApplicationsRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
            Pump(Maintain("inspect"));
            Check(!card.OperationFailed && !card.RequiresCompleteRepair && card.OperationStatus == "" && card.Activity == "" &&
                card.ShortReason == "" && card.RepairIsPrimary && card.ShowPrimary && card.InstallText == "Repair" && card.CanInstall &&
                !card.ShowPortableRepairLink && !card.ShowDetailsRepair && card.InlineReason.Contains(".runtime") && !card.CanLaunch && card.CanRepair,
                "Successful portable Recheck replaces stale removal guidance with current missing-runtime readiness");
            Check(journal.SequenceEqual(File.ReadAllBytes(journalPath)) && receipt.SequenceEqual(File.ReadAllBytes(receiptPath)) &&
                files.All(p => File.GetLastWriteTimeUtc(p.Key) == p.Value) && failed.Result.Outcome == "partial" && failed.ReconciledBy is null && networkAttempts == 0,
                "Read-only Recheck preserves failed result, receipt and files and makes no network request");
            card.DetailsExpanded = true; Render("portable-rechecked-runtime", rechecked: true); card.DetailsExpanded = false;

            var goodInspect = coordinator.Operations.Last(); var goodResult = goodInspect.Result!;
            foreach (var unsafeResult in new[]
            {
                goodResult with { Outcome = "failed" }, goodResult with { SafeToRetry = false }, goodResult with { RecoveryRequired = true },
                goodResult with { ActiveOperationId = Guid.NewGuid() }, goodResult with { RestartRequired = true }
            })
            {
                goodInspect.Result = unsafeResult; Refresh();
                Check(card.OperationFailed && card.InlineReason.Contains("Close TestCard"), "Failed or unsafe Recheck cannot hide a previous removal failure");
            }
            goodInspect.Result = goodResult; goodInspect.RequiresReview = true; Refresh();
            Check(card.OperationFailed, "Unverified inspect evidence cannot replace failure guidance");
            goodInspect.RequiresReview = false;
            coordinator.Operations.Remove(goodInspect); coordinator.Operations.Insert(0, goodInspect); Refresh();
            Check(card.OperationFailed, "An older inspection does not supersede the latest mutation");
            coordinator.Operations.Remove(goodInspect); coordinator.Operations.Add(goodInspect); Refresh();

            using (var reopened = new PortableWindowScope(state.Root))
            {
                var restored = reopened.Window.Cards.Single(c => c.Definition.Id == "testcard");
                restored.Installed = new InstallationService(new LocalState(state.Root)).Find(TestCardDefinition);
                refresh.Invoke(reopened.Window, []);
                Check(!restored.OperationFailed && restored.InlineReason.Contains(".runtime") && !restored.CanLaunch && restored.CanRepair,
                    "Fresh Toolkit window derives current readiness from receipt and later successful Recheck");
            }

            var pending = coordinator.Begin("testcard", "repair", "portable"); card.Busy = true; Refresh();
            Check(!card.PortableOperationActive && card.OperationPending && card.ShortReason.Contains("Recheck") && coordinator.MutationBlocked,
                "Saved pending portable operation is never made active merely by Busy");
            var later = coordinator.Store.Create("testcard", "inspect", "portable"); later.Finished = true;
            later.Result = Result(later, "succeeded") with { SafeToRetry = true, RecoveryRequired = false }; coordinator.Operations.Add(later); Refresh();
            Check(card.OperationPending && !card.OperationActive && coordinator.MutationBlocked && !card.CanRepair && !card.CanInstall,
                "Display of a later inspect never releases an unreconciled pending mutation");
            coordinator.Operations.Remove(later); card.Busy = false; Pump(Maintain("inspect"));
            Check(pending.ReconciledBy is not null && pending.Result is null && !coordinator.MutationBlocked,
                "Existing portable inspection still performs explicit pending recovery without inventing success");

            var cachePath = Path.Combine(state.DownloadRoot, TestCardDefinition.Id, asset.Digest![7..].ToLowerInvariant(), asset.Name);
            File.WriteAllBytes(cachePath, [0]); Pump(Maintain("repair"));
            Check(card.OperationFailed && !card.CanLaunch && card.Installed?.Path == originalPath && networkAttempts > 0,
                "Recorded package guidance does not bypass cache verification or claim offline availability");
            File.WriteAllBytes(cachePath, bytes); var attemptsBeforeRepair = networkAttempts;
            Pump((Task)typeof(MainWindow).GetMethod("PrepareAsync", flags)!.Invoke(window, [card])!);
            Check(!card.OperationFailed && !card.OperationActive && card.CanLaunch && card.Installed!.Path != originalPath &&
                File.Exists(originalPath) && networkAttempts == attemptsBeforeRepair && coordinator.Operations.Last().Result?.IsSuccess == true &&
                coordinator.Operations.Last().Request.Action == "repair",
                "Primary Repair calls the existing repair handler, restores runtime in a fresh copy and retains the previous copy without network");
            Check(bindingErrors.ToString().Length == 0, "Portable guidance has no WPF binding errors");
        }
        finally
        {
            gate.Release.TrySetResult(); PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); trace.Dispose(); window.Close();
        }
    }

    private static void PortableRepairPolicyTests()
    {
        var asset = TestCardAsset([1, 2, 3]);
        var installed = new Installation("C:\\Fixture\\TestCard.exe", "1.0.0")
            { ReadinessIssue = "Repair required: missing .runtime/dotnet.exe.", PortablePackage = asset };
        var card = new AppCard(TestCardDefinition) { Installed = installed };
        Check(card.RepairIsPrimary && card.ShowPrimary && card.InstallText == "Repair" && card.CanInstall && !card.CanLaunch &&
            !card.ShowPortableRepairLink && !card.ShowDetailsRepair && card.ShortReason == "",
            "Known recorded portable package makes Repair the sole primary recovery action without release metadata");
        var release = Release("v1.0.0"); release.Assets = [asset]; card.Snapshot = new([release], DateTimeOffset.UtcNow);
        Check(card.State == UpdateState.Current && card.RepairIsPrimary && card.CanInstall,
            "Current release still offers primary Repair when required portable files are missing");
        card.Busy = true;
        Check(card.RepairIsPrimary && !card.CanInstall && !card.CanLaunch, "Busy keeps the primary Repair disabled");
        card.Busy = false; card.Checking = true;
        Check(!card.CanInstall, "Local detection keeps the primary Repair disabled");
        card.Checking = false; card.MutationBlocked = true; card.OperationPending = true;
        Check(card.RepairIsPrimary && !card.CanInstall && !card.CanLaunch, "Pending lifecycle lock still blocks both primary Repair and Launch");
        card.MutationBlocked = false; card.OperationPending = false;
        card.Installed = installed with { MaintenanceIssue = "Ownership could not be verified." };
        Check(!card.RepairIsPrimary && !card.CanRepair && card.ShowDetailsRepair,
            "Unsafe ownership is not promoted to an available primary Repair");
        card.Installed = installed with { PortablePackage = null }; card.Snapshot = null;
        Check(!card.RepairIsPrimary && !card.CanRepair && card.ShowPortableRepairLink && card.ShortReason == "Repair required.",
            "Missing repair package preserves the existing unavailable recovery controls");
        var invalid = TestCardAsset([1, 2, 3]); invalid.Digest = null;
        card.Installed = installed with { PortablePackage = invalid };
        Check(!card.RepairIsPrimary && card.ShowPortableRepairLink && !card.CanLaunch,
            "Unverified recorded package is not promoted to primary Repair");
        card.Installed = installed with { PortablePackage = null }; card.Snapshot = new([release], DateTimeOffset.UtcNow);
        Check(card.RepairIsPrimary && card.CanInstall, "Compatible verified release package can supply the portable primary Repair");
        release.Tag = "v0.9.0";
        Check(!card.RepairIsPrimary && !card.CanRepair && card.ShowPortableRepairLink,
            "Older selected package is not promoted into a portable downgrade repair");
        card.Installed = installed;
        Check(card.RepairIsPrimary && card.CanInstall, "Recorded package retains precedence over an older selected release");
        card.Installed = installed with { ReadinessIssue = "", PortablePackage = asset }; release.Tag = "v1.0.0";
        Check(!card.RepairIsPrimary && !card.ShowPrimary && !card.ShowPortableRepairLink && card.ShowDetailsRepair && card.CanLaunch,
            "Healthy up-to-date portable app keeps Launch and ordinary Details maintenance");
        var external = new AppCard(Catalog.Apps.Single(a => a.IsJob))
            { Installed = new("C:\\Fixture\\job.exe", "1.0.0") { ReadinessIssue = "Application files missing." }, Snapshot = new([release], DateTimeOffset.UtcNow) };
        Check(!external.RepairIsPrimary && external.ShowDetailsRepair, "External-owner readiness does not acquire portable primary policy");
        external.RequiresCompleteRepair = true;
        Check(external.RepairIsPrimary && external.InstallText == "Repair" && !external.ShowDetailsRepair,
            "Existing external-owner recovery retains its primary Repair while suppressing its identical Details action");
    }

    private sealed class PortableResponseGate(byte[] bytes) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private sealed class PortableWindowScope(string root) : IDisposable
    {
        public MainWindow Window { get; } = new(root, true);
        public void Dispose() => Window.Close();
    }
}
