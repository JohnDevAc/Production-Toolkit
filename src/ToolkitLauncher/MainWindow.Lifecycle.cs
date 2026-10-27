using System.Diagnostics;
using System.Windows;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public partial class MainWindow
{
    private Guid? activePortableOperation;

    private void UpdateOperationCards()
    {
        if (lifecycle.Store.Warning.Length > 0) { Footer = lifecycle.Store.Warning; Notify(nameof(Footer)); }
        foreach (var card in Cards)
        {
            var operation = lifecycle.Operations.LastOrDefault(o => o.Request.ProductId == card.Definition.Id && o.Request.Action != "inspect");
            card.MutationBlocked = lifecycle.MutationBlocked;
            card.OperationPending = operation?.Pending == true;
            card.PortableOperationActive = operation is { Transport: "portable", Pending: true, Finished: false, RequiresReview: false, Result: null } &&
                operation.Request.OperationId == activePortableOperation;
            card.OperationActive = card.PortableOperationActive || IsActiveOperation(operation);
            card.OperationWaitingForInput = card.OperationActive && operation?.Result is { Outcome: "running", Stage: "waiting-input" };
            card.HasRecoveryAdapter = operation?.Pending == true && operation.CapabilityRoot is not null;
            var rechecked = PortableRecheckSupersedes(operation);
            card.OperationOutcome = rechecked ? "" : operation?.Result?.Outcome ?? "";
            card.OperationFailed = !rechecked && operation?.ReconciledBy is null && (operation?.Result?.Outcome is "failed" or "partial" or "blocked" || operation?.ExitCode is not null and not 0 and not 1602);
            card.OperationStatus = card.PortableOperationActive || rechecked || operation is null || operation.ReconciledBy is not null || !operation.Pending && operation.Result?.IsSuccess == true ? "" : operation.Message +
                (string.IsNullOrWhiteSpace(operation.Result?.NextAction) ? "" : " " + operation.Result.NextAction);
            var evidence = lifecycle.Operations.LastOrDefault(o => o.Request.ProductId == card.Definition.Id && !o.RequiresReview &&
                !o.ProcessActive && o.Result?.RecoveryRequired is not null)?.Result;
            card.RequiresCompleteRepair = evidence?.RecoveryRequired == true;
            card.RecoveryVersion = null;
            if (card.RequiresCompleteRepair && evidence?.Components is { ValueKind: System.Text.Json.JsonValueKind.Array } components)
                foreach (var component in components.EnumerateArray())
                    if (component.GetProperty("productId").GetString() == card.Definition.Id &&
                        component.TryGetProperty("version", out var version) && version.ValueKind == System.Text.Json.JsonValueKind.String)
                        card.RecoveryVersion = version.GetString();
            if (card.RequiresCompleteRepair)
                card.RecoveryVersion = new[] { card.RecoveryVersion, card.Installed?.Version, InstallationService.ReadMaintenanceVersion(card.Installed?.Capabilities) }
                    .Where(v => AppVersion.Parse(v) is not null).OrderByDescending(v => AppVersion.Parse(v)).FirstOrDefault();
            card.Recompute();
        }
        Notify(nameof(CanRefresh));
    }

    private bool PortableRecheckSupersedes(LifecycleOperation? operation)
    {
        if (operation is not { Transport: "portable", Pending: false, Finished: true, ExitUnconfirmed: false }) return false;
        var latest = lifecycle.Operations.LastOrDefault(o => o.Request.ProductId == operation.Request.ProductId);
        // A later successful read-only check replaces old card guidance, never journal
        // evidence or a pending mutation. Failed/unsafe inspections cannot hide a failure.
        return latest is { Transport: "portable", Request.Action: "inspect", Pending: false, Finished: true,
            RequiresReview: false, ExitUnconfirmed: false, ProcessActive: false,
            Result: { IsSuccess: true, SafeToRetry: true, RecoveryRequired: false, ActiveOperationId: null, RestartRequired: not true } } &&
            latest.CreatedUtc > operation.CreatedUtc && latest.ExitCode is null or 0;
    }

    private static bool IsActiveOperation(LifecycleOperation? operation)
    {
        if (operation is not { Pending: true, ProcessActive: true, ProcessId: { } id, ProcessStartedUtc: { } started,
            RequiresReview: false, ExitUnconfirmed: false, ExitCode: null } ||
            operation.Result is { Outcome: "failed" or "partial" or "blocked" or "cancelled" or "restart-required" }
                or { RecoveryRequired: true } or { RestartRequired: true }) return false;
        // A saved heartbeat/ProcessActive flag does not establish current activity. This
        // read-only display check never changes the coordinator's pending/result evidence.
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && process.StartTime.ToUniversalTime() == started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private async void RepairClick(object sender, RoutedEventArgs e) => await MaintainAsync(Card(sender), "repair");
    private async void UninstallClick(object sender, RoutedEventArgs e) => await MaintainAsync(Card(sender), "uninstall");
    private async void RecheckClick(object sender, RoutedEventArgs e) => await MaintainAsync(Card(sender), "inspect");
    private void ReleasesClick(object sender, RoutedEventArgs e) => InstallationService.OpenUrl(Card(sender).Definition.RepositoryUrl + "/releases");

    private async Task MaintainAsync(AppCard card, string action, bool confirmed = false)
    {
        if (action == "repair" && !card.CanRepair || action == "uninstall" && !card.CanUninstall || action == "inspect" && !card.CanRecheck) return;
        if (action == "uninstall" && !confirmed)
        {
            var scope = card.Definition.IsEnvironment ? "Environment Setup will ask which installed role to remove. Shared tools and user data are retained by default."
                : card.Definition.PackageKind == PackageKind.PortableZip ? "Remove Toolkit-owned TestCard package folders, including retained previous copies. TestCard settings and shared runtimes are retained."
                : "Open this application's own removal workflow. Review its data-retention options before proceeding.";
            if (MessageBox.Show(this, scope, "Uninstall " + card.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        }
        card.Busy = true;
        try
        {
            if (card.Definition.PackageKind == PackageKind.PortableZip)
            {
                await MaintainPortableAsync(card, action);
                return;
            }
            var capabilities = card.Installed?.Capabilities;
            if (action == "repair" && capabilities?.Supports("inspect") == true)
            {
                // Ask the installed owner before choosing retained vs complete-package
                // repair, including on a fresh Toolkit state with no prior journal.
                var inspection = await lifecycle.RunAsync(card.Definition.Id, "inspect", capabilities, null, null, shutdown);
                if (inspection.Pending || inspection.RequiresReview || inspection.ExitCode != 0 || inspection.Result?.IsSuccess != true)
                    throw new InvalidOperationException("Repair needs a verified owner inspection. Use Recheck. " + inspection.Message);
                UpdateOperationCards();
            }
            if (action == "repair" && card.RequiresCompleteRepair)
            {
                if (!card.HasCompleteRepairPackage) { Report(card, AppCard.RepairPackageNeeded); return; }
                using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                card.Cancellation = recoveryCancellation;
                PreparedPackage recoveryPackage;
                try
                {
                    recoveryPackage = await packages.PrepareManagedAsync(card.Definition, card.SelectedRelease!, "repair",
                        new Progress<TransferProgress>(p => { if (card.Busy && !card.InstallerRunning) { card.Activity = p.Message; card.Progress = p.Percent; } }), recoveryCancellation.Token);
                }
                catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    local.Log(card.Name + " recovery package: " + error);
                    Report(card, AppCard.RepairPackageNeeded); return;
                }
                recoveryCancellation.Token.ThrowIfCancellationRequested();
                if (!recoveryPackage.DownloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || recoveryPackage.Capabilities?.Supports("repair") != true)
                    throw new InvalidOperationException(AppCard.RepairPackageNeeded);
                await RunManagedSetupAsync(card, recoveryPackage.SetupPath, recoveryPackage.Capabilities, "repair");
                return;
            }
            if (action == "inspect" && capabilities?.Supports(action) != true) capabilities = lifecycle.RecoveryCapabilities(card.Definition.Id);
            if (capabilities?.Supports(action) == true)
            {
                card.InstallerRunning = action != "inspect";
                var operation = await lifecycle.RunAsync(card.Definition.Id, action, capabilities, null, null, shutdown);
                if (action == "inspect" && card.OperationPending) lifecycle.ReleaseAfterInspect(card.Definition.Id, operation);
                Report(card, operation.Message);
                await RefreshInstalledAsync();
                if (operation.Result?.IsSuccess == true) card.Activity = "";
                return;
            }
            if (action == "inspect") throw new InvalidOperationException("This installed version does not advertise read-only lifecycle inspection. Use its owner's recovery workflow.");
            if (action == "uninstall")
            {
                if (card.Installed?.Uninstaller is { } uninstaller)
                {
                    card.InstallerRunning = true;
                    var operation = await lifecycle.RunAsync(card.Definition.Id, action, null, uninstaller.StartInfo(), null, shutdown);
                    Report(card, operation.Message);
                    await RefreshInstalledAsync();
                }
                else if (card.Definition.IsEnvironment && card.Installed is { Path.Length: > 0 } environment)
                {
                    Report(card, "This older Setup does not advertise targeted removal. Choose Uninstall in its own window and review the retained components.");
                    await RunManagedSetupAsync(card, environment.Path, null, "uninstall");
                }
                else
                {
                    InstallationService.OpenUrl("ms-settings:appsfeatures");
                    Report(card, "This older application has no verified callable uninstaller. Remove " + card.Name + " in Windows Installed apps, then Check for updates to refresh its state.");
                }
                return;
            }
            var retainedSetup = card.Installed?.PcAgent?.SetupPath ?? (card.Definition.IsEnvironment ? card.Installed?.Path : null);
            if (capabilities is null && !string.IsNullOrWhiteSpace(retainedSetup) && File.Exists(retainedSetup))
            { await RunManagedSetupAsync(card, retainedSetup, null, "repair"); return; }
            if (card.SelectedRelease is null || card.Asset is null || !card.CompatibleRepairPackage)
                throw new InvalidOperationException("No compatible repair package is available. Check releases; repair will not downgrade this application.");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            card.Cancellation = cancellation;
            var package = await packages.PrepareManagedAsync(card.Definition, card.SelectedRelease, action,
                new Progress<TransferProgress>(p => { if (!card.InstallerRunning) { card.Activity = p.Message; card.Progress = p.Percent; } }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await RunManagedSetupAsync(card, package.SetupPath, package.Capabilities, action);
        }
        catch (OperationCanceledException) when (card.Cancellation?.IsCancellationRequested == true || shutdown.IsCancellationRequested)
        { Report(card, "Operation cancelled before installer handoff."); }
        catch (Exception error) { Report(card, Friendly(error)); }
        finally { card.Cancellation = null; card.InstallerRunning = false; card.Busy = false; UpdateOperationCards(); }
    }

    private async Task MaintainPortableAsync(AppCard card, string action, ReleaseAsset? installAsset = null)
    {
        var operation = lifecycle.Begin(card.Definition.Id, action, "portable");
        if (action != "inspect")
        {
            // Only this live invocation can establish local activity; restored journals cannot.
            activePortableOperation = operation.Request.OperationId;
            card.Activity = action == "uninstall" ? "Removing owned portable copies…" : "Preparing application…";
            UpdateOperationCards();
        }
        try
        {
            var portable = new PortableInstallations(local);
            using var ownershipLock = portable.AcquireLock(card.Definition);
            if (action == "inspect")
            {
                portable.Inspect(card.Definition);
                operation.Result = new(1, operation.Request.OperationId, card.Definition.Id, action, "succeeded", DateTimeOffset.UtcNow,
                    "Portable ownership and recovery state checked. Re-detecting packaged files.") { SafeToRetry = true, RecoveryRequired = false };
                operation.Finished = true;
                lifecycle.Store.Save(operation);
                lifecycle.ReleaseAfterInspect(card.Definition.Id, operation);
                card.Activity = "";
                return;
            }
            portable.AdoptLegacy(card.Definition, local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id));
            if (action == "uninstall")
            {
                await Task.Run(() => portable.Remove(card.Definition), shutdown);
                card.Installed = null;
            }
            else
            {
                var asset = installAsset ?? card.Installed?.PortablePackage ?? card.Asset ?? throw new InvalidOperationException("No verified portable repair package is recorded. Check releases first.");
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                card.Cancellation = cancellation;
                var executable = await packages.InstallPortableAsync(card.Definition, asset, local.ApplicationsRoot,
                    new Progress<TransferProgress>(p => { if (card.Busy) { card.Activity = p.Message; card.Progress = p.Percent; } }), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                card.Installed = new InstallationService(local).RegisterPortable(card.Definition, executable, asset);
                card.UpdateInstalledIcon(IconService.ReadInstalled(executable));
            }
            operation.Result = new(1, operation.Request.OperationId, card.Definition.Id, action, "succeeded", DateTimeOffset.UtcNow,
                action == "uninstall" ? "Owned portable copies removed. Settings and shared runtimes were retained." : "Portable application verified and ready. No application or output was started.");
            card.Activity = "";
            card.Progress = 100;
        }
        catch (OperationCanceledException) when (card.Cancellation?.IsCancellationRequested == true || shutdown.IsCancellationRequested)
        {
            operation.Result = new(1, operation.Request.OperationId, card.Definition.Id, action, "cancelled", DateTimeOffset.UtcNow, "Portable preparation cancelled. The previous installation remains selected.");
        }
        catch (Exception error)
        {
            operation.Result = new(1, operation.Request.OperationId, card.Definition.Id, action, action == "uninstall" ? "partial" : "failed", DateTimeOffset.UtcNow, error.Message)
            { NextAction = "Review the remaining owned files, close TestCard if running, and retry.", RecoveryRequired = action == "uninstall" };
            Report(card, error.Message);
        }
        finally
        {
            if (activePortableOperation == operation.Request.OperationId) activePortableOperation = null;
            operation.Finished = true;
            lifecycle.Store.Save(operation);
            card.Cancellation = null;
            card.Installed = new InstallationService(local).Find(card.Definition);
            card.Recompute(); UpdateOperationCards(); Notify(nameof(Summary));
        }
    }
}
