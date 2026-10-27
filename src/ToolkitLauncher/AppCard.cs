using System.ComponentModel;
using System.Collections.ObjectModel;
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
    public void UpdateInstalledIcon(BitmapSource? installed) => UpdateIcons(installed, remoteIcon);
    public void UpdateRemoteIcon(BitmapSource? remote) => UpdateIcons(installedIcon, remote);
    public ReleaseChannel[] Channels { get; } = [ReleaseChannel.Stable, ReleaseChannel.Development];
    private ReleaseChannel channel;
    public ReleaseChannel Channel { get => channel; set { channel = value; Recompute(); ChannelChanged?.Invoke(this); } }
    public Action<AppCard>? ChannelChanged { get; set; }
    public ReleaseSnapshot? Snapshot { get; set; }
    public bool Offline { get; set; }
    public string ReleaseError { get; set; } = "";
    public Installation? Installed { get; set; }
    public ObservableCollection<NetworkConfigurator> NetworkConfigurators { get; } = [];
    public bool ShowNetworkDiscovery => Definition.IsJob && !HasInstallation;
    private string networkDiscoveryStatus = "";
    public string NetworkDiscoveryStatus { get => networkDiscoveryStatus; set { networkDiscoveryStatus = value; Notify(); } }
    public EnvironmentSnapshot? EnvironmentStatus { get; set; }
    public bool ShowEnvironment => Definition.IsEnvironment;
    public bool ShowChannelNote => !Definition.IsEnvironment && !NeedsPcAgentSetup;
    public string EnvironmentSummary => (EnvironmentStatus?.RoleLabel ?? "Environment") + " · " + (EnvironmentStatus?.State == EnvironmentInstallationState.Full ? "Installed" : EnvironmentStatus?.Status ?? "Not checked");
    public string EnvironmentColour => EnvironmentStatus?.Colour ?? "#617082";
    public string EnvironmentDetail => EnvironmentStatus?.Detail ?? "Local components are checked on startup, after setup closes or Check for updates.";
    public List<EnvironmentComponent>? EnvironmentComponents => EnvironmentStatus?.Components;
    public bool ShowCompact => !OperationActive && !RequiresCompleteRepair && (Definition.IsEnvironment ? EnvironmentStatus?.State == EnvironmentInstallationState.NotInstalled : !HasInstallation && Installed?.ReadinessIssue.Length is not > 0);
    public bool ShowDetails => !ShowCompact;
    public bool NeedsEnvironmentSetup => Definition.IsEnvironment && EnvironmentStatus?.State is EnvironmentInstallationState.NotInstalled or EnvironmentInstallationState.Partial;
    public bool NeedsPcAgentSetup => Definition.IsPcAgent && Installed?.PcAgent?.NeedsSetup == true;
    public bool ShowPcAgentRuntime => Definition.IsPcAgent && HasInstallation;
    public bool PcAgentRunning => Definition.IsPcAgent && Installed?.PcAgentRunning == true;
    public string PcAgentRuntimeText => Installed?.PcAgentRunning switch
    {
        true => "Running", false => "Not running", _ => "Running status unavailable"
    };
    public string PcAgentRuntimeColour => PcAgentRunning ? "#24735B" : "#617082";
    public bool NeedsSetup => NeedsEnvironmentSetup || NeedsPcAgentSetup;
    public string SetupNotice => NeedsPcAgentSetup ? Installed!.PcAgent!.Detail +
        (State == UpdateState.NewerInstalled && ExistingSetupPath is null ? " Choose a release at least as new as the installed agent." : "") : "";
    public string? ExistingSetupPath => NeedsPcAgentSetup && (Asset is null || State is UpdateState.Current or UpdateState.NewerInstalled)
        ? Installed?.PcAgent?.SetupPath
        : Definition.IsEnvironment && Installed is { IsSavedSetup: true } && (Asset is null ||
            string.Equals(Installed.SavedSetup?.Tag, SelectedRelease?.Tag, StringComparison.OrdinalIgnoreCase)) ? Installed.Path
        : NeedsEnvironmentSetup && Installed is not null && (Asset is null || State == UpdateState.Current) ? Installed.Path : null;
    public Release? SelectedRelease => Snapshot is null ? null : ReleaseSelection.Latest(Snapshot.Releases, Channel);
    public ReleaseAsset? Asset => SelectedRelease is { } release ? ReleaseSelection.ManagedPackage(Definition, release) : null;
    public bool HasInstallation => Installed is { IsSavedSetup: false, IsPresent: true };
    public bool EquivalentPackage => HasInstallation && SelectedRelease is not null && Snapshot is not null &&
        VersionStatus.UsesEquivalentPackage(Definition, Installed!.Version, SelectedRelease, Snapshot.Releases);
    public UpdateState State => EquivalentPackage ? UpdateState.Current : VersionStatus.Evaluate(HasInstallation, Installed?.Version, SelectedRelease, Channel);
    private bool busy;
    public bool Busy { get => busy; set { busy = value; Recompute(); } }
    private bool installerRunning;
    public bool InstallerRunning { get => installerRunning; set { installerRunning = value; Recompute(); } }
    private bool checking;
    public bool Checking { get => checking; set { checking = value; Recompute(); } }
    private bool releaseChecking;
    public bool ReleaseChecking { get => releaseChecking; set { releaseChecking = value; Recompute(); } }
    public bool MutationBlocked { get; set; }
    public bool OperationPending { get; set; }
    public bool OperationActive { get; set; }
    public bool PortableOperationActive { get; set; }
    public bool OperationWaitingForInput { get; set; }
    public bool ShowPendingRecheck => OperationPending && !OperationActive;
    public bool HasRecoveryAdapter { get; set; }
    public string OperationStatus { get; set; } = "";
    public string OperationOutcome { get; set; } = "";
    public bool OperationFailed { get; set; }
    public bool RequiresCompleteRepair { get; set; }
    public string? RecoveryVersion { get; set; }
    public const string RepairPackageNeeded = "Repair needs a compatible complete package. Check for updates.";
    public string CompleteRepairGuidance => Definition.PackageKind == PackageKind.PortableZip
        ? HasCompleteRepairPackage ? Installed?.PortablePackage is not null
            ? "Repair will verify and use the recorded portable package." : "Repair will verify and use the selected portable package."
            : "Repair needs a verified portable package. Check for updates."
        : HasCompleteRepairPackage ? "Repair will verify and use the selected complete package." : RepairPackageNeeded;
    public bool HasCompleteRepairPackage
    {
        get
        {
            // Portable repair uses the receipt's package before consulting the release feed.
            // This is source eligibility only; Repair still verifies the ZIP before use.
            if (Definition.PackageKind == PackageKind.PortableZip)
            {
                var portable = Installed?.PortablePackage ?? Asset;
                if (portable is null || !portable.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
                try { PackageService.ValidateSource(Definition, portable); return true; }
                catch (InvalidDataException) { return false; }
            }
            if (Asset is not { } asset || !asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                AppVersion.Parse(SelectedRelease?.Tag) is not { } offered || !CompatibleRepairPackage ||
                AppVersion.Parse(RecoveryVersion) is not { } current || offered.CompareTo(current) < 0) return false;
            try { PackageService.ValidateSource(Definition, asset); return true; }
            catch (InvalidDataException) { return false; }
        }
    }
    private bool detailsExpanded;
    public bool DetailsExpanded { get => detailsExpanded; set { detailsExpanded = value; Notify(); } }
    public string ShortReason => PortableOperationActive ? "" : OperationActive ? "Follow the steps in the installer window."
        : OperationPending ? OperationOutcome == "restart-required" ? "Restart Windows, then Recheck." : "Setup pending · Recheck when ready."
        : RequiresCompleteRepair ? HasCompleteRepairPackage ? "" : CompleteRepairGuidance
        : OperationFailed ? "Setup needs attention. See Details."
        : Installed?.MaintenanceIssue.Length > 0 ? "Maintenance unavailable. See Details."
        : NeedsPortableRepair ? RepairIsPrimary ? "" : "Repair required."
        : NeedsPcAgentSetup ? "Complete setup to enable Launch."
        : ReleaseError.Length > 0 ? "Updates unavailable"
        : Snapshot is null ? ReleaseChecking ? "" : "Updates not checked."
        : SelectedRelease is null ? "No " + Channel.ToString().ToLowerInvariant() + " release."
        : Asset is null ? "No Windows package in this release."
        : string.IsNullOrEmpty(Asset.Digest) ? "Package verification unavailable."
        : MutationBlocked ? "Finish the active setup first."
        : Channel == ReleaseChannel.Development && State != UpdateState.Current ? "Development selected." : "";
    public bool NeedsPortableRepair => Definition.PackageKind == PackageKind.PortableZip && Installed?.ReadinessIssue.Length > 0;
    public bool RepairIsPrimary => RequiresCompleteRepair || NeedsPortableRepair && HasInstallation &&
        Installed?.MaintenanceIssue.Length is not > 0 && HasCompleteRepairPackage &&
        (Installed?.PortablePackage is not null || CompatibleRepairPackage);
    public bool ShowPortableRepairLink => NeedsPortableRepair && !RepairIsPrimary;
    public bool ShowDetailsRepair => ShowMaintenance && !RepairIsPrimary;
    public bool ShowMaintenance => Installed is not null && (HasInstallation || Installed.Capabilities is not null || Installed.Uninstaller is not null || NeedsPortableRepair);
    public bool CompatibleRepairPackage => !HasInstallation || AppVersion.Parse(Installed?.Version) is { } current &&
        AppVersion.Parse(SelectedRelease?.Tag) is { } offered && offered.CompareTo(current) >= 0;
    public bool CanRepair => CanChoose && !MutationBlocked && Installed?.MaintenanceIssue.Length is not > 0 && ShowMaintenance &&
        (Installed?.Capabilities?.Supports("repair") == true || Definition.IsEnvironment || Installed?.PcAgent?.SetupPath is not null ||
         Installed?.PortablePackage is not null || Asset is not null && CompatibleRepairPackage);
    public bool CanUninstall => CanChoose && !MutationBlocked && ShowMaintenance && Installed?.MaintenanceIssue.Length is not > 0;
    public bool CanRecheck => !Busy && (HasRecoveryAdapter || Definition.PackageKind == PackageKind.PortableZip || Installed?.Capabilities?.Supports("inspect") == true);
    public bool ShowRecheck => !OperationActive && (OperationPending || Installed?.Capabilities is not null || NeedsPortableRepair);
    public string VersionLabel => Definition.IsEnvironment ? "Setup version" : "Version";
    public string DependencyNote => Definition.PackageKind == PackageKind.PortableZip && HasInstallation
        ? "Preview is available without network dependencies. Configure NDI Runtime and FFmpeg in TestCard's Dependencies + network before enabling outputs." : "";
    public string InlineReason => PortableOperationActive ? "" : OperationStatus.Length > 0 ? OperationStatus
        : RequiresCompleteRepair ? CompleteRepairGuidance
        : Installed?.MaintenanceIssue.Length > 0 ? Installed.MaintenanceIssue
        : Installed?.ReadinessIssue.Length > 0 ? Installed.ReadinessIssue
        : NeedsPcAgentSetup ? SetupNotice
        : Snapshot is null ? ReleaseChecking ? "Checking available releases… Local apps can still open." : "Releases have not been checked. Connect to check, or use a retained repair package."
        : SelectedRelease is null ? "No " + Channel.ToString().ToLowerInvariant() + " release is available. Choose another channel."
        : Asset is null ? "This release has no supported Windows package. Open the project's releases for alternatives."
        : string.IsNullOrEmpty(Asset.Digest) ? "This package has no verification digest. Open the project's releases for help."
        : Definition.PackageKind == PackageKind.PortableZip && !HasInstallation ? "Installs TestCard and its private runtime. Network outputs need NDI Runtime and FFmpeg; preview does not."
        : MutationBlocked ? "Another lifecycle operation needs to finish or be rechecked before changing software." : "";
    public bool ShowReleaseHelp => Snapshot is not null && (SelectedRelease is null || Asset is null || string.IsNullOrEmpty(Asset.Digest));
    public bool CanChoose => !Busy && !Checking;
    public bool CanInstall => RepairIsPrimary ? CanRepair : CanChoose && !MutationBlocked && Installed?.MaintenanceIssue.Length is not > 0 && (ExistingSetupPath is not null || Asset is not null && (State != UpdateState.Current || NeedsSetup)
        && !(NeedsPcAgentSetup && State == UpdateState.NewerInstalled));
    public bool CanLaunch => CanChoose && !OperationPending && (!Definition.IsEnvironment || !MutationBlocked) && Installed is not null && (HasInstallation || Installed.IsSavedSetup || Installed.IsMaintenanceOnly) && !NeedsPcAgentSetup && !NeedsPortableRepair &&
        (!Definition.IsPcAgent || Installed.PcAgentRunning == false);
    public bool CanCancel => Busy && !InstallerRunning;
    public bool ShowPrimary => RepairIsPrimary || State != UpdateState.Current || Installed is null || NeedsSetup;
    public string InstallText => RepairIsPrimary ? "Repair" : ShowCompact ? "Install" : NeedsSetup ? "Complete setup" : State switch
    {
        UpdateState.UpdateAvailable => "Update", UpdateState.SwitchChannel => "Switch version",
        UpdateState.NewerInstalled => "Install older", UpdateState.Current => "Up to date",
        UpdateState.Unknown when Installed is not null => "Install selected", _ => "Install"
    };
    public string LaunchText => Definition.IsEnvironment ? Installed?.IsMaintenanceOnly == true ? "Open maintenance" : "Open setup" : "Launch";
    public string InstalledText => !HasInstallation ? "Not detected" :
        (Installed!.Version?.Split('+')[0] ?? "Version unknown");
    public string LatestText => Snapshot is null ? "Not checked" : SelectedRelease is null ? "No release available" : SelectedRelease.Tag +
        (Asset is null ? " · no supported installer" : $" · {Asset.Size / 1048576d:0.#} MB");
    public string VersionToolTip => !HasInstallation ? "Checks registered installations and the application's standard installation folders." : Installed!.Version + "\n" + Installed.Path;
    public string Status => PortableOperationActive ? "In progress" : OperationActive ? OperationWaitingForInput ? "Waiting for input" : "Setup in progress"
        : OperationPending ? "Operation needs attention" : RequiresCompleteRepair || NeedsPortableRepair ? "Repair required" : NeedsSetup ? HasInstallation ? "Setup incomplete" : "Setup needed" :
        Definition.IsEnvironment && EnvironmentStatus?.State == EnvironmentInstallationState.Unknown ? "Installation not verified" :
        Snapshot is null ? (!HasInstallation ? "Not installed" : "Not checked") : State switch
    {
        UpdateState.Current => "Up to date",
        UpdateState.UpdateAvailable => "Needs updating",
        UpdateState.SwitchChannel => "Different channel", UpdateState.NewerInstalled => "Newer installed",
        UpdateState.Unknown => "Version unknown", UpdateState.NoRelease => "No release",
        _ => Definition.IsEnvironment ? "Setup needed" : "Not installed"
    };
    public string StatusColor => OperationActive ? "#426779" : NeedsSetup || NeedsPortableRepair || RequiresCompleteRepair || OperationPending ? "#94620F" : State switch
    {
        UpdateState.Current => "#24735B", UpdateState.UpdateAvailable => "#94620F",
        UpdateState.SwitchChannel => "#5D59A2", _ => "#617082"
    };
    public string ChannelNote => EquivalentPackage && AppVersion.Parse(Installed?.Version)?.IsPrerelease != (Channel == ReleaseChannel.Development)
        ? "This channel uses the same package as your installed release."
        : Channel == ReleaseChannel.Development
        ? "Development builds may replace your stable installation."
        : "Stable releases are recommended for production.";
    public string DetailNote => RepairIsPrimary ? CompleteRepairGuidance : NeedsPcAgentSetup ? SetupNotice : Definition.IsEnvironment ? "Open setup to install, update or repair environment components." :
        State == UpdateState.NewerInstalled ? "The selected release is older. Installing it will downgrade this application." :
        State == UpdateState.Unknown && Installed is not null ? "The installed version cannot be compared reliably." :
        Definition.PackageKind == PackageKind.PortableZip ? "Portable app. NDI Runtime and FFmpeg are needed for network outputs." :
        Definition.PackageKind == PackageKind.ZipWithSetup ? "The complete ZIP is verified and unpacked before Setup opens." :
        "The application's own installer handles installation and updates.";
    private string activity = "";
    public string Activity { get => activity; set { activity = value; Notify(); Notify(nameof(ActivitySummary)); Notify(nameof(ActivityDetail)); } }
    public string ActivitySummary => OperationActive && !PortableOperationActive || OperationStatus.Length > 0 && !Busy ? "" : activity.Length > 95 ? activity[..92] + "…" : activity;
    public string ActivityDetail => OperationActive && !PortableOperationActive ? "" : activity;
    private double progress;
    public double Progress { get => progress; set { progress = value; Notify(); } }
    public string StatusToolTip => Snapshot is null ? "Releases have not been checked." :
        (Offline ? "Compared with releases last checked on " : "Releases checked on ") +
        Snapshot.CheckedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm") + "." +
        (Offline ? " Select Check for updates to refresh." : "");
    public CancellationTokenSource? Cancellation { get; set; }
    public void Recompute() => Notify("");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
