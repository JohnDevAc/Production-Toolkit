using System.Text.Json;

namespace ToolkitLauncher.Core;

public enum EnvironmentInstallationState { NotInstalled, Partial, Full, Unknown, Inactive }

public sealed class EnvironmentEvidence
{
    public string[]? Roles { get; set; }
    public bool ComponentReceiptInvalid { get; set; }
    public JsonElement? ComponentReceipt { get; set; }
    public JsonElement? RetainedServerRecord { get; set; }
    public string? CurrentUserSid { get; set; }
    public string? DistroName { get; set; }
    public bool? ServerRecoveryPending { get; set; }
    public bool? PcAgent { get; set; }
    public bool? PcAgentConfigured { get; set; }
    public string? PcAgentSetupDetail { get; set; }
    public PcAgentConfiguration? PcAgentConfiguration { get; set; }
    public bool? PcAgentNetworkAvailable { get; set; }
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
    public bool? DiscoveryConfigured { get; set; }
    public bool? DiscoveryRunning { get; set; }
    public bool? DiscoveryListening { get; set; }
    public bool RestartPending { get; set; }
    public string? Note { get; set; }
}

public sealed record EnvironmentComponent(string Name, string Status, string Detail);

public sealed record EnvironmentSnapshot(EnvironmentInstallationState State, List<EnvironmentComponent> Components,
    DateTimeOffset CheckedAt, string Detail, bool AnyInstalled)
{
    public string RoleLabel { get; init; } = "Environment";
    public string Status => State switch
    {
        EnvironmentInstallationState.Full => "Installed fully",
        EnvironmentInstallationState.Partial => "Installed partially",
        EnvironmentInstallationState.NotInstalled => "Not installed",
        EnvironmentInstallationState.Inactive => "Inactive · data retained",
        _ => "Installation not verified"
    };
    public string Colour => State == EnvironmentInstallationState.Full ? "#24735B" :
        State == EnvironmentInstallationState.Partial ? "#94620F" : "#617082";

    public static EnvironmentSnapshot From(EnvironmentEvidence evidence, DateTimeOffset checkedAt)
    {
        if (evidence.ComponentReceiptInvalid)
            return new(EnvironmentInstallationState.Unknown,
                [new("Deployment role", "Not verified", "The saved component receipt is unreadable or unsupported. Review Environment Setup and restore compatible ownership information.")],
                checkedAt, "Deployment completeness cannot be determined from an invalid component receipt.",
                evidence.NdiTools == true || evidence.PcAgent == true || evidence.Configuration == true || evidence.Distro == true);
        if (evidence.PcAgentConfiguration is not null) evidence.PcAgentConfigured = evidence.PcAgentConfigured != false && evidence.PcAgentConfiguration.IsValid;
        var agentNetwork = evidence.PcAgentConfigured != true ? evidence.PcAgentSetupDetail ?? "Local production adapter configuration is required."
            : evidence.PcAgentNetworkAvailable == true ? "Production adapter and saved address are available."
            : evidence.PcAgentNetworkAvailable == false ? "Production adapter or saved address is unavailable. Connect the adapter or refresh PC Agent."
            : "Production adapter availability has not been verified.";
        var client = evidence.Roles?.Contains("client") == true
            || evidence.Roles is null && evidence.Configuration == false && evidence.Distro == false
                && evidence.Watchdog == false && evidence.PcAgent == true;
        var server = evidence.Roles?.Contains("server") == true || evidence.Configuration == true;
        var retainedServer = HasInactiveServer(evidence);
        var retainedComponent = new EnvironmentComponent("KiloLink Server Pro", "Inactive · data retained",
            "Server services were removed. KiloLink data and the Linux environment are retained. Open setup to configure a role.");
        if (retainedServer && !client)
            return new(EnvironmentInstallationState.Inactive,
            [
                retainedComponent,
                new("NDI Tools", evidence.NdiTools == true ? "Installed · shared runtime" : evidence.NdiTools == false ? "Not installed" : "Not verified", evidence.NdiVersion ?? ""),
                new("NDI Discovery", "Inactive", "Server Discovery is not configured or running.")
            ], checkedAt, "Server role inactive. Data and the Linux environment are retained.", true) { RoleLabel = "Server" };
        if (!client && !server && evidence.Configuration == false && evidence.Distro == false && evidence.Watchdog == false
            && (evidence.NdiTools == true || evidence.PcAgent == true))
        {
            var selectedNone = evidence.Roles is { Length: 0 };
            return new(selectedNone ? EnvironmentInstallationState.NotInstalled : EnvironmentInstallationState.Unknown,
            [
                new("NDI Tools", evidence.NdiTools == true ? "Installed · shared runtime" : "Not installed / not verified", evidence.NdiVersion ?? ""),
                new("PC Agent", evidence.PcAgent == true ? "Installed independently" : "Not installed / not verified", ""),
                new("Deployment role", selectedNone ? "No Environment role selected" : "Not verified", "Select Client or Server in Environment Setup to establish its required components.")
            ], checkedAt, selectedNone ? "Environment server ownership was removed; shared tools were retained." : "Legacy shared tools were found without reliable deployment-role evidence.", true);
        }
        if (client && !server)
        {
            var complete = evidence.NdiTools == true && evidence.PcAgent == true && evidence.PcAgentConfigured == true;
            var unknown = evidence.NdiTools is null || evidence.PcAgent is null || evidence.PcAgentConfigured is null;
            var clientState = evidence.RestartPending ? EnvironmentInstallationState.Partial : complete ? EnvironmentInstallationState.Full
                : unknown ? EnvironmentInstallationState.Unknown : EnvironmentInstallationState.Partial;
            return new(clientState,
            [
                new("NDI Tools", evidence.NdiTools == true ? "Installed" : evidence.NdiTools == false ? "Not installed" : "Not verified", evidence.NdiVersion ?? ""),
                new("PC Agent", evidence.PcAgent != true ? "Not installed / not verified" : evidence.PcAgentConfigured == true ? "Installed · configured" : "Installed · setup required", agentNetwork),
                retainedServer ? retainedComponent : new("Server components", "Not required", "Client deployment uses remote Job Configurator, KiloLink and Discovery as configured.")
            ], checkedAt, "Client deployment. " + evidence.Note, evidence.NdiTools == true || evidence.PcAgent == true) { RoleLabel = "Client" };
        }
        var required = new[] { evidence.Configuration, evidence.Distro, evidence.Container, evidence.Watchdog, evidence.NdiTools, evidence.Discovery };
        if (client) required = required.Concat(new[] { evidence.PcAgent, evidence.PcAgentConfigured }).ToArray();
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
            new("NDI Discovery", discovery, "Checks the installed Discovery executable, service or startup task, and its ownership of the listening port."),
            .. client ? new[] { new EnvironmentComponent("PC Agent", evidence.PcAgent != true ? "Not installed / not verified"
                : evidence.PcAgentConfigured == true ? "Installed · configured" : "Installed · setup required",
                "Required by the selected Client role on this combined host. " + agentNetwork) } : Array.Empty<EnvironmentComponent>()
        ], checkedAt, string.Join("\n", notes), any) { RoleLabel = client ? "Client + Server" : server ? "Server" : "Environment" };
    }

    private static bool HasInactiveServer(EnvironmentEvidence evidence)
    {
        // Current owner records establish intent, never a stale successful operation.
        // Missing/contradictory records and a pending recovery must retain normal guards.
        if (evidence.Roles is null || evidence.Roles.Contains("server") || evidence.Configuration != false ||
            evidence.Distro != true || evidence.Watchdog != false || evidence.WatchdogRunning == true ||
            evidence.ContainerRunning == true || evidence.WebResponding == true || evidence.DiscoveryConfigured != false || evidence.DiscoveryRunning != false ||
            evidence.DiscoveryListening == true || evidence.RestartPending || evidence.ServerRecoveryPending != false ||
            string.IsNullOrWhiteSpace(evidence.CurrentUserSid) || string.IsNullOrWhiteSpace(evidence.DistroName) ||
            evidence.ComponentReceipt is not { ValueKind: JsonValueKind.Object } receipt ||
            evidence.RetainedServerRecord is not { ValueKind: JsonValueKind.Object } retained) return false;
        static bool Is(JsonElement item, string name, JsonValueKind kind) => item.TryGetProperty(name, out var value) && value.ValueKind == kind;
        static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static bool SchemaOne(JsonElement item) => item.TryGetProperty("schemaVersion", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var version) && version == 1;
        if (!SchemaOne(receipt) || !SchemaOne(retained) || !Is(receipt, "retainedServer", JsonValueKind.True) || !Is(retained, "active", JsonValueKind.False) ||
            Text(receipt, "serverOwnerSid") != evidence.CurrentUserSid || Text(retained, "serverOwnerSid") != evidence.CurrentUserSid ||
            Text(retained, "distro") != evidence.DistroName || !receipt.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array) return false;
        var recordedRoles = roles.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : null).ToArray();
        return recordedRoles.All(r => r is "client") && recordedRoles.Length <= 1 && recordedRoles.SequenceEqual(evidence.Roles);
    }
}
