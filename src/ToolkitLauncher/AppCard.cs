using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed class AppCard(AppDefinition definition) : INotifyPropertyChanged
{
    public AppDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string Subtitle => Definition.Subtitle;
    public string Description => Definition.Description;
    public string Glyph => Definition.Glyph;
    public string Accent => Definition.Accent;
    public string IconPath => $"pack://application:,,,/Production Toolkit;component/Assets/{Definition.Id}.ico";
    private BitmapSource? bundledIcon;
    private BitmapSource? installedIcon;
    private BitmapSource? remoteIcon;
    private IconTheme? theme;
    public BitmapSource Icon => installedIcon ?? remoteIcon ?? (bundledIcon ??= BitmapDecoder.Create(new Uri(IconPath),
        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First());
    public IconTheme Theme => theme ??= IconTheme.FromIcon(Icon);
    public void UpdateIcons(BitmapSource? installed, BitmapSource? remote)
    {
        installedIcon = installed; remoteIcon = remote;
        theme = null;
        Notify(nameof(Icon)); Notify(nameof(Theme));
    }
    public ReleaseChannel[] Channels { get; } = [ReleaseChannel.Stable, ReleaseChannel.Development];
    private ReleaseChannel channel;
    public ReleaseChannel Channel { get => channel; set { channel = value; Recompute(); ChannelChanged?.Invoke(this); } }
    public Action<AppCard>? ChannelChanged { get; set; }
    public ReleaseSnapshot? Snapshot { get; set; }
    public bool Offline { get; set; }
    public Installation? Installed { get; set; }
    public EnvironmentSnapshot? EnvironmentStatus { get; set; }
    public bool ShowEnvironment => Definition.IsEnvironment;
    public bool ShowChannelNote => !Definition.IsEnvironment;
    public string EnvironmentSummary => "Environment · " + (EnvironmentStatus?.Status ?? "Not checked");
    public string EnvironmentColour => EnvironmentStatus?.Colour ?? "#617082";
    public string EnvironmentDetail => EnvironmentStatus?.Detail ?? "Local components are checked on startup or Check for updates.";
    public List<EnvironmentComponent>? EnvironmentComponents => EnvironmentStatus?.Components;
    public bool ShowCompact => Definition.IsEnvironment ? EnvironmentStatus?.State == EnvironmentInstallationState.NotInstalled : Installed is null;
    public bool ShowDetails => !ShowCompact;
    public bool NeedsEnvironmentSetup => Definition.IsEnvironment && EnvironmentStatus?.State is EnvironmentInstallationState.NotInstalled or EnvironmentInstallationState.Partial;
    public string CompactDownloadText => Definition.IsEnvironment && Installed is not null ? Status + " · " + InstalledText : "";
    public Release? SelectedRelease => Snapshot is null ? null : ReleaseSelection.Latest(Snapshot.Releases, Channel);
    public ReleaseAsset? Asset => SelectedRelease is { } release ? ReleaseSelection.Installer(Definition, release) : null;
    public bool EquivalentPackage => Installed is not null && SelectedRelease is not null && Snapshot is not null &&
        VersionStatus.UsesEquivalentPackage(Definition, Installed.Version, SelectedRelease, Snapshot.Releases);
    public UpdateState State => EquivalentPackage ? UpdateState.Current : VersionStatus.Evaluate(Installed is not null, Installed?.Version, SelectedRelease, Channel);
    private bool busy;
    public bool Busy { get => busy; set { busy = value; Recompute(); } }
    private bool installerRunning;
    public bool InstallerRunning { get => installerRunning; set { installerRunning = value; Recompute(); } }
    private bool checking;
    public bool Checking { get => checking; set { checking = value; Recompute(); } }
    public bool CanChoose => !Busy && !Checking;
    public bool CanInstall => CanChoose && (Asset is not null && (State != UpdateState.Current || NeedsEnvironmentSetup) || NeedsEnvironmentSetup && Installed is not null);
    public bool CanLaunch => CanChoose && Installed is not null;
    public bool CanCancel => Busy && !InstallerRunning;
    public bool ShowPrimary => State != UpdateState.Current || Installed is null;
    public string InstallText => ShowCompact ? "Install" : NeedsEnvironmentSetup ? "Complete setup" : State switch
    {
        UpdateState.UpdateAvailable => "Update", UpdateState.SwitchChannel => "Switch version",
        UpdateState.NewerInstalled => "Install older", UpdateState.Current => Offline ? "Current in cache" : "Up to date",
        UpdateState.Unknown when Installed is not null => "Install selected", _ => "Install"
    };
    public string LaunchText => Definition.IsEnvironment ? "Open setup" : "Launch";
    public string InstalledText => Installed is null ? "Not detected" :
        (Installed.Version?.Split('+')[0] ?? "Version unknown") + (Installed.IsSavedSetup && !Definition.IsEnvironment ? " · saved setup" : "");
    public string VersionHeading => Definition.IsEnvironment ? "Setup download" : "Installed";
    public string LatestText => Snapshot is null ? "Not checked" : SelectedRelease is null ? "No release available" : SelectedRelease.Tag +
        (Asset is null ? " · no supported installer" : $" · {Asset.Size / 1048576d:0.#} MB");
    public string VersionToolTip => Installed is null ? "Checks registered installations and the application's standard installation folders." : Installed.Version + "\n" + Installed.Path;
    public string Status => Definition.IsEnvironment ? DownloadStatus : Snapshot is null ? (Installed is null ? "Not installed" : "Not checked") : State switch
    {
        UpdateState.Current => Offline ? "Current in cache" : "Up to date",
        UpdateState.UpdateAvailable => Offline ? "Update in cache" : "Update available",
        UpdateState.SwitchChannel => "Different channel", UpdateState.NewerInstalled => "Newer installed",
        UpdateState.Unknown => "Version unknown", UpdateState.NoRelease => "No release",
        _ => Definition.IsEnvironment ? "Setup needed" : "Not installed"
    };
    private string DownloadStatus => Installed is null ? "Not downloaded" : Snapshot is null ? "Downloaded · not checked" : State switch
    {
        UpdateState.Current => Offline ? "Downloaded · current in cache" : "Downloaded · up to date",
        UpdateState.UpdateAvailable => Offline ? "Downloaded · older than cache" : "Downloaded · out of date",
        UpdateState.SwitchChannel => "Downloaded · other channel",
        UpdateState.NewerInstalled => "Downloaded · newer version",
        _ => "Downloaded · version not verified"
    };
    public string StatusColor => State switch
    {
        UpdateState.Current => "#24735B", UpdateState.UpdateAvailable => "#94620F",
        UpdateState.SwitchChannel => "#5D59A2", _ => "#617082"
    };
    public string ChannelNote => EquivalentPackage && AppVersion.Parse(Installed?.Version)?.IsPrerelease != (Channel == ReleaseChannel.Development)
        ? "This channel uses the same package as your installed release."
        : Channel == ReleaseChannel.Development
        ? "Development builds may replace your stable installation."
        : "Stable releases are recommended for production.";
    public string DetailNote => Definition.IsEnvironment ? "Download status covers the setup executable. Environment status checks the installed components. Open setup to install or repair them." :
        State == UpdateState.NewerInstalled ? "The selected release is older. Installing it will downgrade this application." :
        State == UpdateState.Unknown && Installed is not null ? "The installed version cannot be compared reliably." :
        Definition.PackageKind == PackageKind.ZipWithSetup ? "The complete ZIP is verified and unpacked before Setup opens." :
        "The application's own installer handles installation and updates.";
    private string activity = "";
    public string Activity { get => activity; set { activity = value; Notify(); } }
    private double progress;
    public double Progress { get => progress; set { progress = value; Notify(); } }
    public string CheckedText => Snapshot is null ? "Releases have not been checked" :
        (Offline ? "Cached · " : "Checked · ") + Snapshot.CheckedAt.ToLocalTime().ToString("dd MMM, HH:mm");
    public CancellationTokenSource? Cancellation { get; set; }
    public void Recompute() => Notify("");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
