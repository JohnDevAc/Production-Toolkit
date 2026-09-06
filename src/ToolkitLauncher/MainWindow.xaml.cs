using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LocalState local;
    private readonly HttpClient apiHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient iconHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient downloadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly GitHubClient github;
    private readonly PackageService packages;
    private readonly IconService icons;
    private readonly SelfUpdateService updater;
    private bool selfUpdating;
    private bool updatePromptOpen;
    private bool installerHandoff;
    public bool DashboardEnabled => !selfUpdating;
    public bool CanCloseForMaintenance => !Cards.Any(c => c.Busy) && (!updatePromptOpen || installerHandoff);
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
            return $"4 applications  ·  {Cards.Count(c => c.Installed is not null)} detected  ·  {updates} update{(updates == 1 ? "" : "s")} available";
        }
    }
    public string Footer { get; private set; } = "";
    public bool PreviewMode { get; }
    public void SetLayoutWidth(double width) { Columns = width < 1000 ? 1 : 2; Notify(nameof(Columns)); }

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
        SizeChanged += (_, _) => SetLayoutWidth(ActualWidth);
        if (!preview)
        {
            Loaded += async (_, _) =>
            {
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
        Closed += (_, _) => { apiHttp.Dispose(); iconHttp.Dispose(); downloadHttp.Dispose(); lifetime.Dispose(); };
        if (local.Warning is not null) Footer = local.Warning;
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
                card.Checking = true;
                // Snapshot preferences before moving file/registry reads off the UI thread.
                var custom = local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id);
                var setup = local.Preferences.Setups.GetValueOrDefault(card.Definition.Id);
                var installedTask = Task.Run(async () =>
                {
                    var found = InstallationService.Find(card.Definition, custom, setup);
                    var icon = found is null ? null : IconService.ReadInstalled(found.Path);
                    var environment = card.Definition.IsEnvironment ? await EnvironmentStatusService.ReadAsync(shutdown) : null;
                    return (Found: found, Icon: icon, Environment: environment);
                }, shutdown);
                try
                {
                    try
                    {
                        var snapshot = await github.GetReleasesAsync(card.Definition, shutdown, userRequested);
                        card.Snapshot = snapshot; card.Offline = snapshot.IsCached;
                        local.SaveCache(card.Definition, snapshot);
                        if (!card.Busy) card.Activity = "";
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        card.Offline = true;
                        if (!card.Busy) Report(card, "Could not check releases. " + Friendly(e));
                    }
                    var installed = await installedTask;
                    card.Installed = installed.Found;
                    card.EnvironmentStatus = installed.Environment;
                    if (installed.Environment is { } environment)
                        local.Log("Environment: " + environment.Status + ". " + string.Join("; ", environment.Components.Select(component => component.Name + ": " + component.Status)));
                    // The installed binary is authoritative. The release icon is a cached
                    // fallback for applications that have not been installed yet.
                    var remote = await icons.ReadRemoteAsync(card.Definition, card.SelectedRelease?.Tag ?? "main", shutdown);
                    card.UpdateIcons(installed.Icon, remote);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception e)
                {
                    if (!card.Busy) Report(card, "Could not read the local application. " + Friendly(e));
                }
                finally { card.Checking = false; }
            }));
        }
        finally { refreshing = false; Notify(nameof(CanRefresh)); Notify(nameof(RefreshText)); Notify(nameof(Summary)); }
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
        await Task.WhenAll(Cards.Select(async card =>
        {
            card.Checking = true;
            var custom = local.Preferences.LaunchPaths.GetValueOrDefault(card.Definition.Id);
            var setup = local.Preferences.Setups.GetValueOrDefault(card.Definition.Id);
            try
            {
                var result = await Task.Run(async () =>
                {
                    var found = InstallationService.Find(card.Definition, custom, setup);
                    var icon = found is null ? null : IconService.ReadInstalled(found.Path);
                    var environment = card.Definition.IsEnvironment ? await EnvironmentStatusService.ReadAsync(shutdown) : null;
                    return (Found: found, Icon: icon, Environment: environment);
                }, shutdown);
                card.Installed = result.Found;
                card.EnvironmentStatus = result.Environment;
                card.UpdateInstalledIcon(result.Icon);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception error) { Report(card, "Could not refresh the installed application. " + Friendly(error)); }
            finally { card.Checking = false; }
        }));
        Notify(nameof(Summary));
    }

    private async Task RunSetupAsync(AppCard card, string path)
    {
        card.InstallerRunning = true;
        card.Activity = "Installer open · complete the steps in its window.";
        try
        {
            using var process = InstallationService.StartSetup(path);
            await process.WaitForExitAsync(shutdown);
            card.Activity = "";
            local.Log($"{card.Name}: installer exited with code {process.ExitCode}.");
            // Failed or cancelled setup may still have installed a component, or
            // removed one during maintenance. Detection is authoritative, not exit 0.
            await RefreshInstalledAsync();
            if (process.ExitCode is 3010 or 1641) Report(card, "Installer reports that Windows must restart to finish applying changes.");
            else if (process.ExitCode != 0) Report(card, $"Installer exited with code {process.ExitCode}. Check its result before retrying.");
        }
        finally { card.InstallerRunning = false; }
    }

    private async Task CheckToolkitUpdateAsync(bool userRequested = false)
    {
        if (PreviewMode || selfUpdating || shutdown.IsCancellationRequested || Cards.Any(c => c.Busy)) return;
        selfUpdating = true; Notify(nameof(CanRefresh)); Notify(nameof(DashboardEnabled));
        try
        {
            var update = await updater.CheckAsync(shutdown, userRequested);
            Footer = local.Warning ?? ""; Notify(nameof(Footer));
            if (update is null || shutdown.IsCancellationRequested) return;
            var prompt = new UpdateWindow(updater, update) { Owner = this };
            updatePromptOpen = true;
            if (prompt.ShowDialog() != true || prompt.InstallerPath is null) return;
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
            Footer = "Could not update Production Toolkit. " + Friendly(error);
            local.Log(Footer); Notify(nameof(Footer));
        }
        finally { selfUpdating = false; updatePromptOpen = false; installerHandoff = false; Notify(nameof(CanRefresh)); Notify(nameof(DashboardEnabled)); }
    }
    private static AppCard Card(object sender) => (AppCard)((FrameworkElement)sender).DataContext;
    private async void InstallClick(object sender, RoutedEventArgs e) => await PrepareAsync(Card(sender));

    private async Task PrepareAsync(AppCard card)
    {
        if (!card.CanInstall) return;
        var asset = card.Asset;
        var release = card.SelectedRelease;
        var existingSetup = card.NeedsEnvironmentSetup && card.Installed is not null && (asset is null || card.State == UpdateState.Current);
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
            var setupPath = card.Installed?.Path;
            if (existingSetup && card.Installed!.IsSavedSetup)
            {
                var saved = local.Preferences.Setups.GetValueOrDefault(card.Definition.Id);
                if (saved is null || !string.Equals(saved.Path, setupPath, StringComparison.OrdinalIgnoreCase) ||
                    !await PackageService.VerifyAsync(setupPath!, new ReleaseAsset { Size = new FileInfo(setupPath!).Length, Digest = saved.Digest }, cancellation.Token))
                    throw new IOException("The saved setup file has changed. Select Check for updates and download it again.");
            }
            if (!existingSetup)
            {
                local.Log($"Install: {card.Name} {release!.Tag} ({asset!.Name})");
                var progress = new Progress<TransferProgress>(p =>
                {
                    if (!card.Busy || card.InstallerRunning) return;
                    card.Activity = p.Message; card.Progress = p.Percent;
                });
                var package = await packages.PrepareAsync(card.Definition, asset, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                setupPath = package.SetupPath;
                // A verified download exists even if elevation or the setup wizard is cancelled.
                if (card.Definition.IsEnvironment)
                {
                    local.Preferences.Setups[card.Definition.Id] = new(package.SetupPath, release.Tag, asset.Digest!);
                    local.Save();
                }
            }
            card.Progress = 100;
            // Several downloads may finish together; serialize external installation wizards.
            if (Cards.Any(c => c != card && c.InstallerRunning))
            { Report(card, "Download ready. Finish the other installer, then select " + card.InstallText + "."); return; }
            await RunSetupAsync(card, setupPath!);
        }
        catch (OperationCanceledException) { card.Activity = ""; local.Log(card.Name + ": download cancelled."); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { card.Activity = ""; local.Log(card.Name + ": administrator prompt cancelled."); }
        catch (Exception e) { Report(card, Friendly(e)); }
        finally { card.Cancellation = null; card.InstallerRunning = false; card.Busy = false; Notify(nameof(CanRefresh)); }
    }

    private static string Friendly(Exception e) => e is TaskCanceledException ? "The connection timed out. Try again when your connection is available." : e.Message;
    private void Report(AppCard card, string message) { card.Activity = message; local.Log(card.Name + ": " + message); }
    private void CancelClick(object sender, RoutedEventArgs e) => Card(sender).Cancellation?.Cancel();
    private async void LaunchClick(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        try
        {
            if (card.Installed is null) return;
            if (card.Definition.IsEnvironment)
            {
                if (Cards.Any(c => c.InstallerRunning)) { Report(card, "Finish the open installer before starting another installation."); return; }
                card.Busy = true; Notify(nameof(CanRefresh));
                try { await RunSetupAsync(card, card.Installed.Path); }
                finally { card.Busy = false; Notify(nameof(CanRefresh)); }
                return;
            }
            InstallationService.Launch(card.Definition, card.Installed);
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
