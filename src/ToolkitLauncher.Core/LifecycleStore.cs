using System.Text.Json;

namespace ToolkitLauncher.Core;

public sealed class LifecycleOperation
{
    public required LifecycleRequest Request { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public string Transport { get; init; } = "protocol";
    public string EntryPoint { get; set; } = "";
    public string? CapabilityRoot { get; set; }
    public string? DescriptorHash { get; set; }
    public string? EntryPointHash { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedUtc { get; set; }
    public int? ExitCode { get; set; }
    public bool ProcessActive { get; set; }
    public bool ExitUnconfirmed { get; set; }
    public LifecycleResult? Result { get; set; }
    public string Problem { get; set; } = "";
    public bool Finished { get; set; }
    public bool RequiresReview { get; set; }
    public Guid? ReconciledBy { get; set; }
    public bool Pending => ReconciledBy is null && (ProcessActive || !Finished || Result?.Outcome == "restart-required" || RequiresReview);
    public string Message => Problem.Length > 0 ? Problem : Result?.Message ?? "Waiting for the product's operation result.";
}

public sealed class LifecycleStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Warning { get; private set; } = "";
    public string DirectoryFor(Guid id) => OwnedPaths.Resolve(Root, id.ToString("D"), false);
    public string RequestPath(LifecycleOperation operation) => OwnedPaths.Resolve(DirectoryFor(operation.Request.OperationId), "request.json");

    public LifecycleOperation Create(string product, string action, string transport, string? version = null)
    {
        if (!Catalog.Apps.Any(a => a.Id == product) || !LifecycleCapabilities.Actions.Contains(action)) throw new ArgumentException("Unknown lifecycle product or action.");
        var id = Guid.NewGuid();
        var directory = DirectoryFor(id);
        if (!Directory.Exists(Root)) OwnedPaths.CreatePrivateDirectory(Root);
        OwnedPaths.ValidatePrivateDirectory(Root);
        OwnedPaths.CreatePrivateDirectory(directory);
        var operation = new LifecycleOperation
        {
            Request = new(1, id, product, action, OwnedPaths.Resolve(directory, "result.json", false)) { RequestedVersion = version },
            CreatedUtc = DateTimeOffset.UtcNow, Transport = transport
        };
        OwnedPaths.Write(Path.Combine(directory, "request.json"), operation.Request);
        Save(operation);
        return operation;
    }

    public void Save(LifecycleOperation operation)
    {
        var directory = DirectoryFor(operation.Request.OperationId);
        OwnedPaths.ValidatePrivateDirectory(directory);
        OwnedPaths.Write(Path.Combine(directory, "journal.json"), operation);
    }

    public IReadOnlyList<LifecycleOperation> Load()
    {
        Warning = "";
        try { OwnedPaths.CheckNoReparse(Root); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { Warning = "Saved operations could not be verified. " + error.Message; return []; }
        if (!Directory.Exists(Root)) return [];
        List<LifecycleOperation> operations = [];
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id)) continue;
                OwnedPaths.ValidatePrivateDirectory(directory);
                var operation = OwnedPaths.Read<LifecycleOperation>(Path.Combine(directory, "journal.json"));
                var request = OwnedPaths.Read<LifecycleRequest>(Path.Combine(directory, "request.json"));
                if (request != operation.Request || request.OperationId != id || request.SchemaVersion != 1 ||
                    !Catalog.Apps.Any(a => a.Id == request.ProductId) || !LifecycleCapabilities.Actions.Contains(request.Action) ||
                    !string.Equals(request.ResultPath, OwnedPaths.Resolve(directory, "result.json", false), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Operation journal and request disagree.");
                Reconcile(operation);
                operations.Add(operation);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Warning = "A saved operation could not be verified. Review " + directory + ". " + error.Message; }
        }
        return operations.OrderBy(o => o.CreatedUtc).ToArray();
    }

    public bool Reconcile(LifecycleOperation operation)
    {
        if (operation.Transport != "protocol" || operation.ReconciledBy is not null || !File.Exists(operation.Request.ResultPath)) return false;
        try
        {
            OwnedPaths.ValidatePrivateDirectory(DirectoryFor(operation.Request.OperationId));
            var result = OwnedPaths.Read<LifecycleResult>(operation.Request.ResultPath);
            if (result.SchemaVersion != 1 || result.OperationId != operation.Request.OperationId || result.ProductId != operation.Request.ProductId ||
                result.Action != operation.Request.Action || result.Outcome is not ("running" or "succeeded" or "no-change" or "cancelled" or "partial" or "failed" or "blocked" or "restart-required") ||
                result.UpdatedUtc < operation.CreatedUtc || result.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(5) || string.IsNullOrWhiteSpace(result.Message) ||
                operation.Result is { } previous && (result.UpdatedUtc < previous.UpdatedUtc || (result.Attempt ?? 0) < (previous.Attempt ?? 0)))
                throw new InvalidDataException("The result is stale, mismatched or unsupported.");
            result.ValidateEvidence();
            var conflict = result.IsSuccess && (operation.ExitUnconfirmed || operation.ExitCode is { } exit && exit != 0);
            var versionProblem = RequestedVersionProblem(operation.Request, result);
            var review = conflict || versionProblem.Length > 0;
            if (JsonSerializer.Serialize(result, OwnedPaths.Json) == JsonSerializer.Serialize(operation.Result, OwnedPaths.Json) && operation.RequiresReview == review) return false;
            operation.Result = result;
            operation.RequiresReview = review;
            operation.Problem = versionProblem;
            operation.Finished = !result.IsPending;
            if (conflict)
            {
                operation.RequiresReview = true;
                operation.Problem = operation.ExitUnconfirmed ? "The product reported completion while Toolkit was closed. Its process exit could not be verified. Recheck before another change."
                    : "The product reported success but its process failed (" + operation.ExitCode + "). Review the owner log and recheck.";
            }
            Save(operation);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            var previousProblem = operation.Problem;
            operation.RequiresReview = true;
            operation.Problem = "Operation result not verified. " + error.Message;
            try { Save(operation); }
            catch (Exception writeError) when (writeError is IOException or InvalidDataException or UnauthorizedAccessException)
            { Warning = "The operation journal could not be safely updated. " + writeError.Message; }
            return previousProblem != operation.Problem;
        }
    }

    private static string RequestedVersionProblem(LifecycleRequest request, LifecycleResult result)
    {
        if (!result.IsSuccess || request.Action is not ("install" or "update") || request.RequestedVersion is null) return "";
        var expected = AppVersion.Parse(request.RequestedVersion);
        if (expected is not null && result.Components is { ValueKind: JsonValueKind.Array } components)
            foreach (var component in components.EnumerateArray())
            {
                if (component.GetProperty("productId").GetString() != request.ProductId) continue;
                var reported = component.TryGetProperty("version", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
                if (component.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True &&
                    AppVersion.Parse(reported) is { } actual)
                    return actual.CompareTo(expected) == 0 ? "" :
                        $"Requested version {request.RequestedVersion} was not installed; the product reported {reported}. Recheck the application before trying another change.";
                break;
            }
        return $"The product reported completion, but requested version {request.RequestedVersion} could not be verified. Recheck the application before trying another change.";
    }
}
