namespace ToolkitLauncher.Core;

public enum EnvironmentInstallationState { NotInstalled, Partial, Full, Unknown }

public sealed class EnvironmentEvidence
{
    public bool? Configuration { get; set; }
    public bool? Distro { get; set; }
    public bool? DistroRunning { get; set; }
    public bool? Container { get; set; }
    public bool? ContainerRunning { get; set; }
    public string? ContainerVersion { get; set; }
    public string? ContainerImage { get; set; }
    public bool? WebResponding { get; set; }
    public bool? Watchdog { get; set; }
    public bool? WatchdogRunning { get; set; }
    public bool? NdiTools { get; set; }
    public string? NdiVersion { get; set; }
    public bool? Discovery { get; set; }
    public bool? DiscoveryRunning { get; set; }
    public bool? DiscoveryListening { get; set; }
    public bool RestartPending { get; set; }
    public string? Note { get; set; }
}

public sealed record EnvironmentComponent(string Name, string Status, string Detail);

public sealed record EnvironmentSnapshot(EnvironmentInstallationState State, List<EnvironmentComponent> Components,
    DateTimeOffset CheckedAt, string Detail, bool AnyInstalled)
{
    public string Status => State switch
    {
        EnvironmentInstallationState.Full => "Installed fully",
        EnvironmentInstallationState.Partial => "Installed partially",
        EnvironmentInstallationState.NotInstalled => "Not installed",
        _ => "Installation not verified"
    };
    public string Colour => State == EnvironmentInstallationState.Full ? "#24735B" :
        State == EnvironmentInstallationState.Partial ? "#94620F" : "#617082";

    public static EnvironmentSnapshot From(EnvironmentEvidence evidence, DateTimeOffset checkedAt)
    {
        var required = new[] { evidence.Configuration, evidence.Distro, evidence.Container, evidence.Watchdog, evidence.NdiTools, evidence.Discovery };
        var any = required.Any(value => value == true) || evidence.RestartPending;
        var state = evidence.RestartPending ? EnvironmentInstallationState.Partial :
            required.All(value => value == true) ? EnvironmentInstallationState.Full :
            any && required.Any(value => value == false) ? EnvironmentInstallationState.Partial :
            required.Any(value => value is null) ? EnvironmentInstallationState.Unknown : EnvironmentInstallationState.NotInstalled;
        var kilo = evidence.Container == false ? "Not installed" : evidence.Container is null
            ? evidence.DistroRunning == false && evidence.Distro == true ? "WSL stopped · not verified" : "Not verified"
            : evidence.ContainerRunning == true ? evidence.WebResponding == false ? "Running · web unavailable" : "Installed · running"
            : evidence.ContainerRunning == false ? "Installed · stopped" : "Installed · state unknown";
        if (evidence.Container == true && (evidence.Watchdog == false || evidence.Configuration == false))
            kilo = evidence.ContainerRunning == true ? "Running · setup incomplete" : "Installed · setup incomplete";
        var discovery = evidence.Discovery == false ? "Not installed / incomplete" : evidence.Discovery is null ? "Not verified" :
            evidence.DiscoveryRunning == true ? evidence.DiscoveryListening == false ? "Running · not listening" :
                evidence.DiscoveryListening == true ? "Installed · listening" : "Running · port not verified" :
            evidence.DiscoveryRunning == false ? "Installed · stopped" : "Installed · state unknown";
        var ndi = evidence.NdiTools == false ? "Not installed" : evidence.NdiTools is null ? "Not verified" :
            string.IsNullOrWhiteSpace(evidence.NdiVersion) ? "Installed" : "Installed · " + evidence.NdiVersion;
        var notes = new List<string>();
        if (evidence.Configuration != true) notes.Add(evidence.Configuration == false ? "Setup configuration is missing." : "Setup configuration could not be read.");
        if (evidence.Watchdog != true) notes.Add(evidence.Watchdog == false ? "KiloLink startup task or script is missing." : "KiloLink startup could not be verified.");
        else if (evidence.WatchdogRunning != true) notes.Add(evidence.WatchdogRunning == false ? "KiloLink watchdog is stopped or disabled." : "KiloLink watchdog state is unknown.");
        if (evidence.RestartPending) notes.Add("Setup is waiting for a Windows restart or continuation.");
        if (!string.IsNullOrWhiteSpace(evidence.Note)) notes.Add(evidence.Note);
        notes.Add("Local state checked " + checkedAt.ToLocalTime().ToString("dd MMM, HH:mm") + ". Refreshes after setup, on startup or Check for updates.");
        return new(state,
        [
            new("KiloLink Server Pro", kilo,
                "Container: " + (evidence.ContainerImage ?? "not verified") +
                (string.IsNullOrWhiteSpace(evidence.ContainerVersion) ? "" : "\nVersion: " + evidence.ContainerVersion) +
                "\nWeb interface: " + (evidence.WebResponding == true ? "responding" : evidence.WebResponding == false ? "not responding" : "not checked") +
                "\nWatchdog: " + (evidence.WatchdogRunning == true ? "running" : evidence.WatchdogRunning == false ? "stopped or missing" : "not verified")),
            new("NDI Tools", ndi, "Checks NDI Tools registration and its launcher executable. Its desktop applications do not need to be running."),
            new("NDI Discovery", discovery, "Checks the installed Discovery executable, service or startup task, and its ownership of the listening port.")
        ], checkedAt, string.Join("\n", notes), any);
    }
}
