using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LocalState local;
    private readonly HttpClient apiHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient iconHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient downloadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient discoveryHttp = JobConfiguratorDiscovery.CreateClient();
    private CancellationTokenSource? discoveryCancellation;
    private int discoveryGeneration;
    private readonly GitHubClient github;
    private readonly PackageService packages;
    private readonly IconService icons;
    private readonly SelfUpdateService updater;
    private readonly LifecycleCoordinator lifecycle;
    private readonly DispatcherTimer pcAgentRuntimeTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private bool checkingPcAgentRuntime;
    private readonly Func<string, bool?> pcAgentRuntimeReader = PcAgentRuntime.Read;
    private bool selfUpdating;
    private bool checkingToolkitUpdate;
    private bool updatePromptOpen;
    private bool installerHandoff;
    public bool DashboardEnabled => !selfUpdating;
    public bool CanCloseForMaintenance => !Cards.Any(c => c.Busy) && !lifecycle.MutationBlocked && (!updatePromptOpen || installerHandoff);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken shutdown;
    public ObservableCollection<AppCard> Cards { get; } = [];
    private bool refreshing;
    public bool CanRefresh => !refreshing && !selfUpdating && !Cards.Any(c => c.Busy);
    public string RefreshText => refreshing ? "Checking…" : "Check for updates";
    public int Columns { get; private set; } = 2;
    public string Summary
    {
        get
        {
            var updates = Cards.Count(c => c.State == UpdateState.UpdateAvailable);
            return $"{Cards.Count} applications  ·  {Cards.Count(c => c.HasInstallation)} detected  ·  {updates} update{(updates == 1 ? "" : "s")} available";
        }
    }
    public string Footer { get; private set; } = "";
    public bool PreviewMode { get; }
    public void SetLayoutWidth(double width) { Columns = width < 1000 ? 1 : width < 1500 ? 2 : 3; Notify(nameof(Columns)); }

    public MainWindow() : this(null, false) { }
    public MainWindow(string? dataRoot, bool preview, bool checkOnStartup = true)
    {
        PreviewMode = preview;
        shutdown = lifetime.Token;
        local = new(dataRoot);
        icons = new(iconHttp, Path.Combine(local.Root, "Icons"));
        github = new(apiHttp, Path.Combine(local.Root, "github-checks.json"));
        updater = new(github, downloadHttp, Path.Combine(local.Root, "Updates"));
        packages = new(downloadHttp, local.DownloadRoot);
        lifecycle = new(local.OperationsRoot);
        foreach (var app in Catalog.Apps)
        {
            var card = new AppCard(app) { Channel = local.Preferences.Channels.GetValueOrDefault(app.Id), Snapshot = local.LoadCache(app), Offline = true };
            card.ChannelChanged = changed =>
            {
                local.Preferences.Channels[changed.Definition.Id] = changed.Channel;
                try { local.Save(); } catch (Exception e) { Report(changed, "Could not save preferences: " + e.Message); }
                Notify(nameof(Summary));
            };
            Cards.Add(card);
        }
        InitializeComponent();
        DataContext = this;
        lifecycle.Changed += UpdateOperationCards;
        UpdateOperationCards();
        SizeChanged += (_, _) => SetLayoutWidth(ActualWidth);
        if (!preview)
        {
            pcAgentRuntimeTimer.Tick += async (_, _) => { await RefreshPcAgentRuntimeAsync(); if (lifecycle.Reconcile()) await RefreshInstalledAsync(); };
            Activated += async (_, _) => await RefreshPcAgentRuntimeAsync();
            Loaded += async (_, _) =>
            {
                pcAgentRuntimeTimer.Start();
                FitStartupWindow();
                var initialSize = new Size(Width, Height);
                if (checkOnStartup)
                {
                    await RefreshAsync();
                    await CheckToolkitUpdateAsync();
                }
                // Release details may add a line. Fit once more only if the user has
                // left the launch size unchanged, preserving subsequent manual resizing.
                if (!shutdown.IsCancellationRequested && WindowState == WindowState.Normal &&
                    Math.Abs(Width - initialSize.Width) < 1 && Math.Abs(Height - initialSize.Height) < 1)
                    FitStartupWindow();
            };
        }
        Closing += (_, e) =>
        {
            if (Cards.Any(c => c.InstallerRunning))
            {
                e.Cancel = MessageBox.Show(this, "An installer is still open. Closing the toolkit leaves that installer running. Close the toolkit?",
                    "Installer running", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes;
            }
            if (!e.Cancel) lifetime.Cancel();
        };
        Closed += (_, _) => { pcAgentRuntimeTimer.Stop(); apiHttp.Dispose(); iconHttp.Dispose(); downloadHttp.Dispose(); discoveryHttp.Dispose(); lifetime.Dispose(); };
        if (local.Warning is not null) Footer = local.Warning;
        if (lifecycle.Store.Warning.Length > 0) Footer = lifecycle.Store.Warning;
    }

    public Size MeasureStartupSize(Size available, Size chrome)
    {
        var size = WindowSizing.Select(available, chrome, clientWidth =>
        {
            SetLayoutWidth(clientWidth + chrome.Width);
            MainLayout.Measure(new Size(clientWidth, double.PositiveInfinity));
            // First measurement creates the ItemsControl templates. Let their data
            // bindings settle before measuring the complete cards rather than placeholders.
            MainLayout.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            MainLayout.UpdateLayout();
            MainLayout.InvalidateMeasure();
            MainLayout.Measure(new Size(clientWidth, double.PositiveInfinity));
            return MainLayout.DesiredSize.Height;
        });
        SetLayoutWidth(size.Width);
        return size;
    }

    private void FitStartupWindow()
    {
        UpdateLayout();
        var area = WindowSizing.WorkArea(this);
        var chrome = new Size(Math.Max(0, ActualWidth - MainLayout.ActualWidth), Math.Max(0, ActualHeight - MainLayout.ActualHeight));
        var desired = MeasureStartupSize(area.Size, chrome);
        MinWidth = Math.Min(640, area.Width);
        MinHeight = Math.Min(480, area.Height);
        Width = desired.Width;
        Height = desired.Height;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        UpdateLayout();
    }

    public async Task RefreshAsync(bool userRequested = false)
    {
        if (refreshing || PreviewMode || shutdown.IsCancellationRequested) return;
        refreshing = true; Notify(nameof(CanRefresh)); Notify(nameof(RefreshText));
        try
        {
            await Task.WhenAll(Cards.Select(async card =>
            {
                var localCheck = RefreshLocalCardAsync(card);
                card.ReleaseChecking = true;
                try
                {
                    try
                    {
                        var snapshot = await github.GetReleasesAsync(card.Definition, shutdown, userRequested);
                        card.Snapshot = snapshot; card.Offline = snapshot.IsCached; card.ReleaseError = "";
                        local.SaveCache(card.Definition, snapshot);
                        if (!card.Busy) card.Activity = "";
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        card.Offline = true;
                        card.ReleaseError = "Could not check releases. " + Friendly(e);
                        local.Log(card.Name + ": " + e);
                    }
                    var remote = await icons.ReadRemoteAsync(card.Definition, card.SelectedRelease?.Tag ?? "main", shutdown);
                    card.UpdateRemoteIcon(remote);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception e)
                {
                    local.Log(card.Name + " icon check: " + e);
                }
                finally { card.ReleaseChecking = false; await localCheck; }
            }));
        }
        finally { refreshing = false; Notify(nameof(CanRefresh)); Notify(nameof(RefreshText)); Notify(nameof(Summary)); }
    }

    public async Task RefreshJobDiscoveryAsync(AppCard card, JobConfiguratorDiscovery? discovery = null)
    {
        if (!card.Definition.IsJob || shutdown.IsCancellationRequested) return;
        var generation = ++discoveryGeneration;
        discoveryCancellation?.Cancel();
        card.NetworkConfigurators.Clear();
        card.NetworkDiscoveryStatus = "";
        if (card.HasInstallation) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        discoveryCancellation = cancellation;
        var acceptingResults = true;
        card.NetworkDiscoveryStatus = "Looking for NDI Job Configurator on your network…";
        try
        {
            var result = await Task.Run(() => (discovery ?? new JobConfiguratorDiscovery(discoveryHttp)).DiscoverAsync(instance =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (acceptingResults && generation == discoveryGeneration && ReferenceEquals(discoveryCancellation, cancellation)
                        && !shutdown.IsCancellationRequested && !card.HasInstallation && !card.NetworkConfigurators.Contains(instance))
                        card.NetworkConfigurators.Add(instance);
                });
            }, cancellation.Token), cancellation.Token);
            acceptingResults = false;
            if (generation != discoveryGeneration || card.HasInstallation) return;
            card.NetworkConfigurators.Clear();
            foreach (var instance in result.Instances) card.NetworkConfigurators.Add(instance);
            card.NetworkDiscoveryStatus = result.TimedOut ? $"Network check incomplete ({result.Checked} of {result.Total} addresses). Use Check for updates to retry."
                : result.Instances.Count > 0 ? "" : result.Total == 0 ? "No supported local network is connected." : "No NDI Job Configurator found on your network.";
            if (result.UnsupportedNetworks > 0) card.NetworkDiscoveryStatus += " Automatic discovery supports IPv4 /20–/30 networks.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == discoveryGeneration && !shutdown.IsCancellationRequested)
            {
                card.NetworkDiscoveryStatus = "Could not check the local network. Use Check for updates to retry.";
                local.Log("Job Configurator discovery: " + error.Message);
            }
        }
        finally { acceptingResults = false; if (ReferenceEquals(discoveryCancellation, cancellation)) discoveryCancellation = null; }
    }

    private async void OpenNetworkConfiguratorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkConfigurator instance) return;
        var card = Cards.Single(c => c.Definition.IsJob);
        try
        {
            if (await new JobConfiguratorDiscovery(discoveryHttp).ProbeAsync(instance.Address, shutdown) is null)
            {
                card.NetworkConfigurators.Remove(instance);
                card.NetworkDiscoveryStatus = "That server is no longer reachable. Use Check for updates to search again.";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(instance.Address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { Report(card, "Could not open the web UI. " + Friendly(error)); }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (!CanRefresh) return;
        await RefreshAsync(userRequested: true);
        await CheckToolkitUpdateAsync(userRequested: true);
    }

    // Recheck all local cards because Environment Setup can also install the PC
    // Agent. Keep release snapshots and their API cooldown untouched.
    public async Task RefreshInstalledAsync()
    {
        if (shutdown.IsCancellationRequested) return;
        await Task.WhenAll(Cards.Select(RefreshLocalCardAsync));
        Notify(nameof(Summary));
    }

    private async Task RefreshLocalCardAsync(AppCard card)
    {
            card.Checking = true;
            var custom = local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id);
            var setup = local.Preferences.Setups.GetValueOrDefault(card.Definition.Id);
            try
            {
                var result = await Task.Run(async () =>
                {
                    var found = InstallationService.Find(card.Definition, custom, setup, local.Root);
                    var icon = found is null ? null : IconService.ReadInstalled(found.Path);
                    var environment = card.Definition.IsEnvironment ? await EnvironmentStatusService.ReadAsync(shutdown) : null;
                    return (Found: found, Icon: icon, Environment: environment);
                }, shutdown);
                card.Installed = result.Found;
                if (card.Definition.IsJob && !PreviewMode) _ = RefreshJobDiscoveryAsync(card);
                card.EnvironmentStatus = result.Environment;
                card.UpdateInstalledIcon(result.Icon);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception error) { Report(card, "Could not refresh the installed application. " + Friendly(error)); }
            finally { card.Checking = false; }
    }

    private async Task RunSetupAsync(AppCard card, string path)
        => await RunManagedSetupAsync(card, path, LifecycleCapabilities.Load(Path.GetDirectoryName(path)!, card.Definition.Id), "install");

    private async Task CompletePcAgentSetupAsync(AppCard card)
    {
        // Readiness admits only the installed Agent's adjacent, version-matched Setup.
        // Its ordinary entrypoint owns licence/NIC consent and waits for its elevated UI.
        var installed = card.Installed ?? throw new IOException("PC Agent is no longer installed.");
        var readiness = PcAgentReadiness.Read(card.Definition, installed);
        if (readiness.Configured && readiness.SetupPath is not null) { await RefreshInstalledAsync(); return; }
        var setup = readiness.SetupPath ?? throw new IOException("A matching PC Agent Setup utility is required.");
        OwnedPaths.CheckNoReparse(setup);
        card.InstallerRunning = true;
        card.Activity = "Setup open · choose the adapter in its window.";
        try
        {
            var operation = await lifecycle.RunAsync(card.Definition.Id, "install", null,
                new System.Diagnostics.ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)!,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal }, null, shutdown, interactiveSetup: true);
            await RefreshInstalledAsync();
            if (!operation.Pending)
            {
                var configured = card.HasInstallation && card.Installed?.PcAgent is { Configured: true, SetupPath: not null };
                var evidence = configured ? "PC Agent configured. " + card.PcAgentRuntimeText + "."
                    : "PC Agent setup is still incomplete. " + card.SetupNotice;
                operation.Problem = operation.ExitCode == 0 ? evidence : operation.Message + " " + evidence;
                // Native UI has no lifecycle result. Preserve that fact; exit zero alone is
                // never recorded as a succeeded repair/configuration result.
                lifecycle.Store.Save(operation);
                card.Activity = configured && operation.ExitCode == 0 ? "" : operation.Message;
            }
            else card.Activity = operation.Message;
            local.Log($"PC Agent interactive setup {operation.Request.OperationId}: {operation.Message}");
            UpdateOperationCards();
        }
        finally { card.InstallerRunning = false; }
    }

    private async Task RunManagedSetupAsync(AppCard card, string path, LifecycleCapabilities? capabilities, string action, string? requestedVersion = null)
    {
        card.InstallerRunning = true;
        card.Activity = "Installer open · complete the steps in its window.";
        try
        {
            var operation = await lifecycle.RunAsync(card.Definition.Id, action, capabilities,
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! }, requestedVersion, shutdown);
            card.Activity = operation.Message;
            local.Log($"{card.Name}: {action} {operation.Request.OperationId}: {operation.Message}");
            await RefreshInstalledAsync();
            if (card.NeedsPcAgentSetup) Report(card, "PC Agent setup is still incomplete. " + card.SetupNotice);
            else if (!operation.Pending && operation.ExitCode == 0) card.Activity = "";
        }
        finally { card.InstallerRunning = false; }
    }

    private async Task CheckToolkitUpdateAsync(bool userRequested = false)
    {
        if (PreviewMode || selfUpdating || checkingToolkitUpdate || lifecycle.MutationBlocked || shutdown.IsCancellationRequested || Cards.Any(c => c.Busy)) return;
        checkingToolkitUpdate = true;
        try
        {
            var update = await updater.CheckAsync(shutdown, userRequested);
            Footer = local.Warning ?? ""; Notify(nameof(Footer));
            if (update is null || shutdown.IsCancellationRequested || Cards.Any(c => c.Busy) || lifecycle.MutationBlocked) return;
            var prompt = new UpdateWindow(updater, update) { Owner = this };
            updatePromptOpen = true;
            if (prompt.ShowDialog() != true || prompt.InstallerPath is null) return;
            selfUpdating = true; Notify(nameof(CanRefresh)); Notify(nameof(DashboardEnabled));
            updatePromptOpen = false;
            // The installer asks this instance to close only once it is ready to replace
            // the files. A failed/cancelled installer can therefore leave the app usable.
            using var process = System.Diagnostics.Process.Start(SelfUpdateService.StartInfo(prompt.InstallerPath))
                ?? throw new IOException("Windows did not start the toolkit installer.");
            installerHandoff = true;
            Footer = "Installing Production Toolkit… the app will close and reopen."; Notify(nameof(Footer));
            await process.WaitForExitAsync(shutdown);
            if (process.ExitCode == 0) Close(); // Also closes a development/portable copy after migration.
            else throw new IOException($"Toolkit installer exited with code {process.ExitCode}. You can retry Check for updates.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            Footer = selfUpdating ? "Could not update Production Toolkit. " + Friendly(error) : "Updates unavailable";
            local.Log("Could not update Production Toolkit: " + error); Notify(nameof(Footer));
        }
        finally { checkingToolkitUpdate = false; selfUpdating = false; updatePromptOpen = false; installerHandoff = false; Notify(nameof(CanRefresh)); Notify(nameof(DashboardEnabled)); }
    }
    private static AppCard Card(object sender) => (AppCard)((FrameworkElement)sender).DataContext;
    private async void InstallClick(object sender, RoutedEventArgs e) => await PrepareAsync(Card(sender));

    private async Task PrepareAsync(AppCard card)
    {
        if (!card.CanChoose) return;
        if (card.RepairIsPrimary) { await MaintainAsync(card, "repair"); return; }
        if (!await RefreshPcAgentAsync(card)) return;
        if (!card.CanInstall) return;
        var asset = card.Asset;
        var release = card.SelectedRelease;
        var existingSetup = card.ExistingSetupPath is not null;
        if (!existingSetup && (asset is null || release is null)) return;
        if (Cards.Any(c => c.InstallerRunning))
        {
            Report(card, "Finish the open installer before starting another installation."); return;
        }
        card.Busy = true; card.Progress = 0; Notify(nameof(CanRefresh));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        card.Cancellation = cancellation;
        try
        {
            if (card.Definition.IsPcAgent && card.Installed?.PcAgent is { Configured: false, SetupPath: not null })
            {
                await CompletePcAgentSetupAsync(card);
                return;
            }
            if (card.Definition.PackageKind == PackageKind.PortableZip)
            {
                await MaintainPortableAsync(card, card.HasInstallation ? "update" : "install", asset);
                return;
            }
            var setupPath = card.ExistingSetupPath;
            string? requestedVersion = null;
            LifecycleCapabilities? capabilities = setupPath is null ? null : LifecycleCapabilities.Load(Path.GetDirectoryName(setupPath)!, card.Definition.Id);
            // Completing an Environment role should skip healthy components. Explicit
            // Repair remains a separate owner operation that may force their replacement.
            var action = card.NeedsEnvironmentSetup ? "install" : card.HasInstallation ? card.NeedsSetup ? "repair" : "update" : "install";
            if (existingSetup && card.Installed!.IsSavedSetup)
            {
                var saved = local.Preferences.Setups.GetValueOrDefault(card.Definition.Id);
                if (saved is null || !string.Equals(saved.Path, setupPath, StringComparison.OrdinalIgnoreCase) ||
                    !await PackageService.VerifyAsync(setupPath!, new ReleaseAsset { Size = new FileInfo(setupPath!).Length, Digest = saved.Digest }, cancellation.Token))
                    throw new IOException("The saved setup file has changed. Select Check for updates and download it again.");
                if (capabilities is not null && action is ("install" or "update"))
                {
                    var version = InstallationService.ReadVersion(setupPath!);
                    if (AppVersion.Parse(version) is not null) requestedVersion = version!.Trim().TrimStart('v', 'V').Split('+')[0];
                }
            }
            if (!existingSetup)
            {
                local.Log($"Install: {card.Name} {release!.Tag} ({asset!.Name})");
                var progress = new Progress<TransferProgress>(p =>
                {
                    if (!card.Busy || card.InstallerRunning) return;
                    card.Activity = p.Message; card.Progress = p.Percent;
                });
                var package = await packages.PrepareManagedAsync(card.Definition, release, action, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                setupPath = package.SetupPath;
                capabilities = package.Capabilities;
                requestedVersion = package.PayloadVersion;
                // A verified download exists even if elevation or the setup wizard is cancelled.
                if (card.Definition.IsEnvironment && string.Equals(package.DownloadPath, package.SetupPath, StringComparison.OrdinalIgnoreCase))
                {
                    local.Preferences.Setups[card.Definition.Id] = new(package.SetupPath, release.Tag, asset.Digest!);
                    local.Save();
                }
            }
            card.Progress = 100;
            // Several downloads may finish together; serialize external installation wizards.
            if (Cards.Any(c => c != card && c.InstallerRunning))
            { Report(card, "Download ready. Finish the other installer, then select " + card.InstallText + "."); return; }
            await RunManagedSetupAsync(card, setupPath!, capabilities, action, requestedVersion);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { card.Activity = "Download cancelled."; local.Log(card.Name + ": download cancelled."); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { card.Activity = ""; local.Log(card.Name + ": administrator prompt cancelled."); }
        catch (Exception e) { Report(card, Friendly(e)); }
        finally { card.Cancellation = null; card.InstallerRunning = false; card.Busy = false; Notify(nameof(CanRefresh)); }
    }

    private static string Friendly(Exception e) => e is TaskCanceledException ? "The connection timed out. Try again when your connection is available." : e.Message;
    private void Report(AppCard card, string message) { card.Activity = message; local.Log(card.Name + ": " + message); }
    private void CancelClick(object sender, RoutedEventArgs e) => Card(sender).Cancellation?.Cancel();
    public async Task RefreshPcAgentRuntimeAsync()
    {
        if (checkingPcAgentRuntime || shutdown.IsCancellationRequested) return;
        checkingPcAgentRuntime = true;
        try
        {
            // Discovery and removal can change cards while the worker is running.
            foreach (var card in Cards.Where(c => c.Definition.IsPcAgent).ToArray())
            {
                if (card.Installed is not { IsPresent: true } installed || !card.CanChoose || !Cards.Contains(card)) continue;
                bool StillCurrent() => !shutdown.IsCancellationRequested && Cards.Contains(card) && card.CanChoose && ReferenceEquals(card.Installed, installed);
                var custom = local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id);
                try
                {
                    var found = await Task.Run(() =>
                    {
                        var running = pcAgentRuntimeReader(installed.Path);
                        return File.Exists(installed.Path) ? installed with { PcAgentRunning = running }
                            : InstallationService.Find(card.Definition, custom, null, local.Root);
                    }, shutdown);
                    if (StillCurrent() && found != installed) { card.Installed = found; card.Recompute(); Notify(nameof(Summary)); }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception error)
                {
                    // An async timer/activation callback must not bring down the
                    // dashboard. Keep owner recovery evidence and log the failure.
                    local.Log(card.Name + " runtime check: " + error);
                    if (StillCurrent()) { card.Installed = installed with { PcAgentRunning = null }; card.Recompute(); }
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { checkingPcAgentRuntime = false; }
    }
    private async Task<bool> RefreshPcAgentAsync(AppCard card)
    {
        if (!card.Definition.IsPcAgent) return true;
        var custom = local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id);
        card.Checking = true;
        try
        {
            card.Installed = await Task.Run(() => InstallationService.Find(card.Definition, custom, null, local.Root), shutdown);
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return false; }
        catch (Exception error) { Report(card, "Could not check PC Agent setup. " + Friendly(error)); return false; }
        finally { card.Checking = false; }
    }
    private async void LaunchClick(object sender, RoutedEventArgs e) => await LaunchAsync(Card(sender));
    private async Task LaunchAsync(AppCard card)
    {
        try
        {
            if (!card.CanChoose) return;
            if (!await RefreshPcAgentAsync(card)) return;
            if (card.Installed is null) return;
            if (card.NeedsPcAgentSetup) { await PrepareAsync(card); return; }
            if (!card.CanLaunch) return;
            if (card.Definition.IsEnvironment)
            {
                if (Cards.Any(c => c.InstallerRunning)) { Report(card, "Finish the open installer before starting another installation."); return; }
                if (card.Installed.MaintenanceIssue.Length > 0) throw new InvalidOperationException(card.Installed.MaintenanceIssue);
                if (card.RequiresCompleteRepair) throw new InvalidOperationException("Use Repair to recover this installation before opening setup.");
                var capabilities = card.Installed.Capabilities?.Supports("install") == true ? card.Installed.Capabilities : null;
                card.Busy = true; Notify(nameof(CanRefresh));
                // Normal setup retains the owner's role/consent UI and original request
                // through reboot. Explicit Repair remains a separate maintenance action.
                try { await RunManagedSetupAsync(card, card.Installed.Path, capabilities, "install"); }
                finally { card.Busy = false; Notify(nameof(CanRefresh)); }
                return;
            }
            card.Busy = true;
            try
            {
                InstallationService.Launch(card.Definition, card.Installed);
                if (card.Definition.IsPcAgent)
                    for (var attempt = 0; attempt < 10; attempt++)
                    {
                        await RefreshPcAgentAsync(card);
                        if (card.Installed?.PcAgentRunning != false) break;
                        await Task.Delay(150, shutdown);
                    }
            }
            finally { card.Busy = false; }
            card.Activity = "";
            local.Log(card.Name + ": launched.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { card.Activity = ""; }
        catch (Exception error) { Report(card, Friendly(error)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
