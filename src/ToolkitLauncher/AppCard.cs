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
    private ImageSource? icon;
    public ImageSource Icon => icon ??= BitmapDecoder.Create(new Uri(IconPath), BitmapCreateOptions.PreservePixelFormat,
        BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();
    public ReleaseChannel[] Channels { get; } = [ReleaseChannel.Stable, ReleaseChannel.Development];
    private ReleaseChannel channel;
    public ReleaseChannel Channel { get => channel; set { channel = value; Recompute(); ChannelChanged?.Invoke(this); } }
    public Action<AppCard>? ChannelChanged { get; set; }
    public ReleaseSnapshot? Snapshot { get; set; }
    public bool Offline { get; set; }
    public Installation? Installed { get; set; }
    public Release? SelectedRelease => Snapshot is null ? null : ReleaseSelection.Latest(Snapshot.Releases, Channel);
    public ReleaseAsset? Asset => SelectedRelease is { } release ? ReleaseSelection.Installer(Definition, release) : null;
    public bool EquivalentPackage => Installed is not null && SelectedRelease is not null && Snapshot is not null &&
        VersionStatus.UsesEquivalentPackage(Definition, Installed.Version, SelectedRelease, Snapshot.Releases);
    public UpdateState State => EquivalentPackage ? UpdateState.Current : VersionStatus.Evaluate(Installed is not null, Installed?.Version, SelectedRelease, Channel);
    private bool busy;
    public bool Busy { get => busy; set { busy = value; Recompute(); } }
    private bool installerRunning;
    public bool InstallerRunning { get => installerRunning; set { installerRunning = value; Recompute(); } }
    public bool CanChoose => !Busy;
    public bool CanInstall => !Busy && Asset is not null && State != UpdateState.Current;
    public bool CanDownload => !Busy && Asset is not null;
    public bool CanLaunch => !Busy && Installed is not null;
    public bool CanCancel => Busy && !InstallerRunning;
    public bool ShowPrimary => State != UpdateState.Current || Installed is null;
    public string InstallText => State switch
    {
        UpdateState.UpdateAvailable => "Update", UpdateState.SwitchChannel => "Switch version",
        UpdateState.NewerInstalled => "Install older", UpdateState.Current => Offline ? "Current in cache" : "Up to date",
        UpdateState.Unknown when Installed is not null => "Install selected", _ => "Install"
    };
    public string LaunchText => Definition.IsEnvironment ? "Open setup" : "Launch";
    public string InstalledText => Installed is null ? "Not detected" :
        (Installed.Version?.Split('+')[0] ?? "Version unknown") + (Installed.IsSavedSetup ? " · saved setup" : "");
    public string VersionHeading => Definition.IsEnvironment ? "Local setup" : "Installed";
    public string LatestText => Snapshot is null ? "Not checked" : SelectedRelease is null ? "No release available" : SelectedRelease.Tag +
        (Asset is null ? " · no supported installer" : $" · {Asset.Size / 1048576d:0.#} MB");
    public string VersionToolTip => Installed is null ? "Use Locate app if this application is installed in a custom folder." : Installed.Version + "\n" + Installed.Path;
    public string Status => Snapshot is null ? (Installed is null ? "Not installed" : "Not checked") : State switch
    {
        UpdateState.Current => Offline ? "Current in cache" : "Up to date",
        UpdateState.UpdateAvailable => Offline ? "Update in cache" : "Update available",
        UpdateState.SwitchChannel => "Different channel", UpdateState.NewerInstalled => "Newer installed",
        UpdateState.Unknown => "Version unknown", UpdateState.NoRelease => "No release",
        _ => Definition.IsEnvironment ? "Setup needed" : "Not installed"
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
    public string DetailNote => Definition.IsEnvironment ? "Version checks cover this setup tool. Manage server and NDI updates inside Setup." :
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
