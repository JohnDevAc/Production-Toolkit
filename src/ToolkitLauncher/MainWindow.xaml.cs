using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LocalState local;
    private readonly InstallationService installations;
    private readonly HttpClient apiHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient downloadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly GitHubClient github;
    private readonly PackageService packages;
    private readonly CancellationTokenSource lifetime = new();
    public ObservableCollection<AppCard> Cards { get; } = [];
    private bool refreshing;
    public bool CanRefresh => !refreshing;
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
    public MainWindow(string? dataRoot, bool preview)
    {
        PreviewMode = preview;
        local = new(dataRoot);
        installations = new(local);
        github = new(apiHttp);
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
                await RefreshAsync();
                // Release details may add a line. Fit once more only if the user has
                // left the launch size unchanged, preserving subsequent manual resizing.
                if (!lifetime.IsCancellationRequested && WindowState == WindowState.Normal &&
                    Math.Abs(Width - initialSize.Width) < 1 && Math.Abs(Height - initialSize.Height) < 1)
                    FitStartupWindow();
            };
            Activated += (_, _) => Rescan();
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
        Closed += (_, _) => { apiHttp.Dispose(); downloadHttp.Dispose(); lifetime.Dispose(); };
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

    private void Rescan()
    {
        if (PreviewMode) return;
        foreach (var card in Cards) { card.Installed = installations.Find(card.Definition); card.Recompute(); }
        Notify(nameof(Summary));
    }

    public async Task RefreshAsync()
    {
        if (refreshing || PreviewMode) return;
        refreshing = true; Notify(nameof(CanRefresh)); Notify(nameof(RefreshText)); Rescan();
        try
        {
            await Task.WhenAll(Cards.Select(async card =>
            {
                try
                {
                    var snapshot = await github.GetReleasesAsync(card.Definition, lifetime.Token);
                    card.Snapshot = snapshot; card.Offline = false;
                    local.SaveCache(card.Definition, snapshot);
                    if (!card.Busy) card.Activity = "";
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception e)
                {
                    card.Offline = true;
                    if (!card.Busy) Report(card, "Could not check releases. " + Friendly(e));
                }
                card.Recompute();
            }));
        }
        finally { refreshing = false; Notify(nameof(CanRefresh)); Notify(nameof(RefreshText)); Notify(nameof(Summary)); }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private static AppCard Card(object sender) => (AppCard)((FrameworkElement)sender).DataContext;
    private async void InstallClick(object sender, RoutedEventArgs e) => await PrepareAsync(Card(sender), true);
    private async void DownloadClick(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        if (card.Asset is not { } asset) return;
        var picker = new SaveFileDialog
        {
            Title = "Save " + card.Name + " installer", FileName = asset.Name, OverwritePrompt = true,
            Filter = Path.GetExtension(asset.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "ZIP package (*.zip)|*.zip" : "Installer (*.exe)|*.exe"
        };
        if (picker.ShowDialog(this) == true) await PrepareAsync(card, false, picker.FileName);
    }

    private async Task PrepareAsync(AppCard card, bool install, string? exportPath = null)
    {
        if (card.Busy || card.Asset is not { } asset || card.SelectedRelease is not { } release) return;
        if (install && Cards.Any(c => c.InstallerRunning))
        {
            Report(card, "Finish the open installer before starting another installation."); return;
        }
        card.Busy = true; card.Progress = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        card.Cancellation = cancellation;
        var operationName = install ? "Install" : "Download";
        try
        {
            local.Log($"{operationName}: {card.Name} {release.Tag} ({asset.Name})");
            var progress = new Progress<TransferProgress>(p => { card.Activity = p.Message; card.Progress = p.Percent; });
            var package = await packages.PrepareAsync(card.Definition, asset, progress, cancellation.Token, extract: install);
            cancellation.Token.ThrowIfCancellationRequested();
            card.Progress = 100;
            if (!install)
            {
                if (exportPath is not null && !string.Equals(Path.GetFullPath(exportPath), Path.GetFullPath(package.DownloadPath), StringComparison.OrdinalIgnoreCase))
                {
                    var temporary = exportPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        await using (var source = File.OpenRead(package.DownloadPath))
                        await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                            await source.CopyToAsync(destination, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        File.Move(temporary, exportPath, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                Report(card, "Downloaded and verified · " + Path.GetFileName(exportPath ?? package.DownloadPath));
                local.Log("Saved download: " + (exportPath ?? package.DownloadPath));
                return;
            }
            // Several downloads may finish together; serialize external installation wizards.
            if (Cards.Any(c => c != card && c.InstallerRunning))
            { Report(card, "Download ready. Finish the other installer, then select " + card.InstallText + "."); return; }
            card.InstallerRunning = true;
            card.Activity = "Installer open · complete the steps in its window.";
            using var process = InstallationService.StartSetup(package.SetupPath);
            if (card.Definition.IsEnvironment)
            {
                local.Preferences.Setups[card.Definition.Id] = new(package.SetupPath, release.Tag, asset.Digest!);
                local.Save();
            }
            await process.WaitForExitAsync(lifetime.Token);
            Rescan();
            if (process.ExitCode is 3010 or 1641) Report(card, "Installer reports that Windows must restart. Version status will be rechecked afterward.");
            else if (process.ExitCode == 0)
                Report(card, card.State == UpdateState.Current ? "Installer closed · selected version detected." :
                    "Installer closed. " + (card.Installed is null ? "Installation was not detected; use Locate app if needed." : "Current local version has been rechecked."));
            else Report(card, $"Installer exited with code {process.ExitCode}. Check its result before retrying.");
        }
        catch (OperationCanceledException) { Report(card, "Download cancelled. Incomplete download removed."); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { Report(card, "Windows administrator prompt was cancelled."); }
        catch (Exception e) { Report(card, Friendly(e)); }
        finally { card.Cancellation = null; card.InstallerRunning = false; card.Busy = false; if (!lifetime.IsCancellationRequested) Rescan(); }
    }

    private static string Friendly(Exception e) => e is TaskCanceledException ? "The connection timed out. Try again when your connection is available." : e.Message;
    private void Report(AppCard card, string message) { card.Activity = message; local.Log(card.Name + ": " + message); }
    private void CancelClick(object sender, RoutedEventArgs e) => Card(sender).Cancellation?.Cancel();
    private void LaunchClick(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        try
        {
            if (card.Installed is null) return;
            InstallationService.Launch(card.Definition, card.Installed);
            Report(card, card.Definition.Id == "pc-agent" ? "Agent launched · look for its icon in the Windows system tray." : "Application launched.");
        }
        catch (Exception error) { Report(card, Friendly(error)); Rescan(); }
    }
    private void LocateClick(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        var picker = new OpenFileDialog { Title = "Locate " + card.Name + " executable", Filter = "Windows application (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            // Avoid mistaking the downloaded bootstrapper for the installed application.
            if (!card.Definition.ExecutableNames.Contains(Path.GetFileName(picker.FileName), StringComparer.OrdinalIgnoreCase))
            { Report(card, "Choose the installed application: " + string.Join(" or ", card.Definition.ExecutableNames)); return; }
            local.Preferences.LaunchPaths[card.Definition.Id] = picker.FileName;
            local.Save(); Rescan();
            Report(card, "Application location saved.");
        }
        catch (Exception error) { Report(card, Friendly(error)); }
    }
    private void ReleasesClick(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        try { InstallationService.OpenUrl(card.Definition.RepositoryUrl + "/releases"); }
        catch (Exception error) { Report(card, error.Message); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
