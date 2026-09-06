using System.Text.Json;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed record PcAgentReadiness(bool Configured, string? SetupPath, string Detail)
{
    public bool NeedsSetup => !Configured || SetupPath is null;

    public static PcAgentReadiness Read(AppDefinition app, Installation installation,
        string? statePath = null, string? legacyStatePath = null)
    {
        // A local Setup can only be reused with its complete, matching Agent pair.
        var version = AppVersion.Parse(installation.Version);
        var setup = app.SetupNames.Select(name => Path.Combine(Path.GetDirectoryName(installation.Path)!, name))
            .FirstOrDefault(path => File.Exists(path) && version is not null
                && AppVersion.Parse(InstallationService.ReadVersion(path))?.CompareTo(version) == 0);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var overridePath = Environment.GetEnvironmentVariable("KILOVIEW_AGENT_STATE_PATH");
        var isolated = statePath is not null || overridePath is not null;
        statePath ??= overridePath is { Length: > 0 } ? Path.GetFullPath(overridePath)
            : Path.Combine(profile, "NDI Configurator", "PC Agent", "agent-state.json");
        legacyStatePath ??= isolated ? null : Path.Combine(profile, "Kiloview", "PC Agent", "agent-state.json");
        // Match AgentStore: an invalid current file must not fall back to stale legacy state.
        var path = File.Exists(statePath) ? statePath : legacyStatePath;
        var configured = false;
        var detail = "Choose the production adapter to finish setup for this Windows account.";
        try
        {
            if (path is not null && File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<SavedConfiguration>(File.ReadAllText(path), Json);
                configured = state is not null && new PcAgentConfiguration(state.SchemaVersion, state.EndpointId,
                    state.AdapterId, state.Address, state.PrefixLength).IsValid
                    && state.Memberships is not null && state.Memberships.All(m => m is not null && !string.IsNullOrWhiteSpace(m.JobName));
                detail = configured ? "" : "The saved configuration is invalid. Complete setup to repair it.";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            detail = "The saved configuration could not be read. Complete setup to review it.";
        }
        if (setup is null)
            detail = "A matching PC Agent Setup utility is missing. Select Complete setup to restore the complete release package.";
        return new(configured, setup, detail);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record SavedConfiguration(int SchemaVersion, string? EndpointId, string? AdapterId,
        string? AdapterName, string? Address, int PrefixLength, DateTimeOffset InstalledUtc, DateTimeOffset UpdatedUtc,
        Membership?[]? Memberships, Multicast? Multicast);
    private sealed record Membership(string? ServerAddress, string? BaseUri, string? JobName, DateTimeOffset RegisteredUtc);
    private sealed record Multicast(string? JobName, DateTimeOffset UpdatedUtc, string? NetPrefix, string? Netmask,
        int? Ttl, bool SendEnabled, bool ReceiveEnabled);
}
