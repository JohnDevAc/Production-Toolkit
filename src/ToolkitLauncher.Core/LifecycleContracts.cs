using System.Text.Json;

namespace ToolkitLauncher.Core;

public sealed record LifecycleRequest(int SchemaVersion, Guid OperationId, string ProductId, string Action, string ResultPath)
{
    public bool PreserveData { get; init; } = true;
    public string? RequestedVersion { get; init; }
    public string? Role { get; init; }
}

public sealed record LifecycleResult(int SchemaVersion, Guid OperationId, string ProductId, string Action,
    string Outcome, DateTimeOffset UpdatedUtc, string Message)
{
    public int? Attempt { get; init; }
    public string? Stage { get; init; }
    public string? ErrorCode { get; init; }
    public string? NextAction { get; init; }
    public string? LogPath { get; init; }
    public bool? SafeToCancel { get; init; }
    public bool? RestartRequired { get; init; }
    public bool? RecoveryRequired { get; init; }
    public bool? SafeToRetry { get; init; }
    public Guid? ActiveOperationId { get; init; }
    public JsonElement? PartialEffects { get; init; }
    public JsonElement? Components { get; init; }
    public string? BackupPath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSuccess => Outcome is "succeeded" or "no-change";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPending => Outcome is "running" or "restart-required";

    public void ValidateEvidence()
    {
        if (Attempt < 0) throw new InvalidDataException("Invalid operation attempt.");
        if (PartialEffects is { ValueKind: not JsonValueKind.Null } effects &&
            (effects.ValueKind != JsonValueKind.Array || effects.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)))
            throw new InvalidDataException("Partial effects must be an array of strings.");
        if (Components is not { ValueKind: not JsonValueKind.Null } components) return;
        if (components.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Component evidence must be an array.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object || !component.TryGetProperty("productId", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!)) throw new InvalidDataException("Component evidence has a missing or duplicate product ID.");
            foreach (var name in new[] { "installed", "configured", "running", "healthy", "pairMatches", "onboarded", "networkAvailable", "recoveryRequired" })
                if (component.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    throw new InvalidDataException("Component status must be boolean or unknown.");
            foreach (var name in new[] { "version", "channel", "setupVersion", "error" })
                if (component.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new InvalidDataException("Component version and error fields must be strings or unknown.");
        }
    }
}

public sealed class LifecycleCapabilities
{
    public int SchemaVersion { get; set; }
    public string ProductId { get; set; } = "";
    public string[] SupportedActions { get; set; } = [];
    public string EntryPoint { get; set; } = "";
    public string EntryPointKind { get; set; } = "native";
    public Dictionary<string, string> ActionEntryPoints { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string Root { get; private set; } = "";
    public static readonly string[] Actions = ["inspect", "install", "update", "repair", "uninstall", "remove-role", "remove-app", "backup", "maintain-runtime"];

    public bool Supports(string action) => SupportedActions.Contains(action, StringComparer.Ordinal);
    public (string Path, bool PowerShell) ResolveEntry(string action)
    {
        if (!Supports(action)) throw new InvalidOperationException("This product does not advertise " + action + ".");
        var mapped = ActionEntryPoints.TryGetValue(action, out var relative);
        var path = OwnedPaths.Resolve(Root, mapped ? relative! : EntryPoint);
        var powershell = mapped ? Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase) : EntryPointKind == "powershell";
        if (!Path.GetExtension(path).Equals(powershell ? ".ps1" : ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported lifecycle entry point type.");
        return (path, powershell);
    }

    public static LifecycleCapabilities? Load(string directory, string productId)
    {
        var path = OwnedPaths.Resolve(directory, "toolbox-lifecycle.json", false);
        if (!File.Exists(path)) return null;
        var value = OwnedPaths.Read<LifecycleCapabilities>(path);
        if (value.SchemaVersion != 1 || value.ProductId != productId || value.SupportedActions is null || value.SupportedActions.Length == 0 ||
            value.SupportedActions.Any(a => !Actions.Contains(a, StringComparer.Ordinal)) || value.SupportedActions.Distinct().Count() != value.SupportedActions.Length ||
            value.EntryPointKind is not ("native" or "powershell") || value.ActionEntryPoints is null)
            throw new InvalidDataException("The product lifecycle descriptor is invalid or unsupported.");
        value.Root = Path.GetFullPath(directory);
        OwnedPaths.Resolve(value.Root, value.EntryPoint, false);
        foreach (var (action, entry) in value.ActionEntryPoints)
        {
            if (!value.Supports(action)) throw new InvalidDataException("Unadvertised lifecycle action mapping.");
            OwnedPaths.Resolve(value.Root, entry, false);
        }
        return value;
    }
}
