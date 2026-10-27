using System.Diagnostics;
using System.Security.Cryptography;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed class LifecycleCoordinator
{
    public LifecycleStore Store { get; }
    public List<LifecycleOperation> Operations { get; } = [];
    public event Action? Changed;
    public TimeSpan InspectTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public bool MutationBlocked => Store.Warning.Length > 0 || Operations.Any(o => o.Request.Action != "inspect" && o.Pending);
    public LifecycleCoordinator(string root)
    {
        Store = new(root);
        Operations.AddRange(Store.Load());
    }

    public static ProcessStartInfo StartInfo(LifecycleCapabilities capabilities, string action, string requestPath)
    {
        var entry = capabilities.ResolveEntry(action);
        var info = new ProcessStartInfo(entry.PowerShell
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : entry.Path)
        { UseShellExecute = false, CreateNoWindow = entry.PowerShell || action == "inspect", WorkingDirectory = capabilities.Root,
            WindowStyle = entry.PowerShell || action == "inspect" ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        if (entry.PowerShell)
        {
            // A caller hosted in PowerShell 7 may inherit incompatible Core modules.
            // Owner adapters target Windows PowerShell 5.1 and use its system modules.
            info.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(info.FileName)!, "Modules");
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry.Path, "-LifecycleRequestPath", requestPath }) info.ArgumentList.Add(argument);
        }
        else
            foreach (var argument in new[] { "--lifecycle-request", requestPath }) info.ArgumentList.Add(argument);
        return info;
    }

    public LifecycleOperation Begin(string product, string action, string transport, string? version = null)
    {
        if (!Directory.Exists(Store.Root)) OwnedPaths.CreatePrivateDirectory(Store.Root);
        OwnedPaths.ValidatePrivateDirectory(Store.Root);
        using var gate = new FileStream(OwnedPaths.Resolve(Store.Root, ".coordination.lock", false), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var saved = Store.Load();
        if (action != "inspect" && (MutationBlocked || saved.Any(o => o.Request.Action != "inspect" && o.Pending)))
            throw new InvalidOperationException("Finish or recheck the pending lifecycle operation before making another change.");
        var operation = Store.Create(product, action, transport, version);
        Operations.Add(operation);
        Changed?.Invoke();
        return operation;
    }

    private static string HashFile(string path)
    {
        OwnedPaths.CheckNoReparse(path);
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public LifecycleCapabilities? RecoveryCapabilities(string product)
    {
        var operation = Operations.LastOrDefault(o => o.Request.ProductId == product && o.Request.Action != "inspect" && o.Pending);
        if (operation?.CapabilityRoot is null) return null;
        var descriptorPath = OwnedPaths.Resolve(operation.CapabilityRoot, "toolbox-lifecycle.json");
        if (HashFile(descriptorPath) != operation.DescriptorHash || HashFile(operation.EntryPoint) != operation.EntryPointHash)
            throw new InvalidDataException("The retained recovery adapter changed. Use the product's installed maintenance entry point.");
        var capabilities = LifecycleCapabilities.Load(operation.CapabilityRoot, product);
        if (capabilities?.Supports("inspect") != true) return null;
        // Different inspect binaries must come from an installed descriptor, not an unverified cached companion.
        if (!capabilities.ResolveEntry("inspect").Path.Equals(operation.EntryPoint, StringComparison.OrdinalIgnoreCase)) return null;
        return capabilities;
    }

    public async Task<LifecycleOperation> RunAsync(string product, string action, LifecycleCapabilities? capabilities,
        ProcessStartInfo? legacy, string? version, CancellationToken closeToken, bool interactiveSetup = false)
    {
        if (interactiveSetup && (product != "pc-agent" || action != "install" || capabilities is not null || legacy is null ||
            legacy.Arguments.Length != 0 || legacy.ArgumentList.Count != 0))
            throw new InvalidOperationException("Interactive completion requires the normal PC Agent Setup without lifecycle arguments.");
        var operation = Begin(product, action, capabilities is null ? interactiveSetup ? "interactive-setup" : "legacy" : "protocol", version);
        try
        {
            var info = capabilities is null ? legacy! : StartInfo(capabilities, action, Store.RequestPath(operation));
            operation.EntryPoint = capabilities?.ResolveEntry(action).Path ?? info.FileName;
            if (capabilities is not null)
            {
                operation.CapabilityRoot = capabilities.Root;
                operation.DescriptorHash = HashFile(OwnedPaths.Resolve(capabilities.Root, "toolbox-lifecycle.json"));
                operation.EntryPointHash = HashFile(operation.EntryPoint);
            }
            using var process = Process.Start(info) ?? throw new IOException("Windows did not start the product's maintenance entry point.");
            operation.ProcessId = process.Id;
            operation.ProcessStartedUtc = process.StartTime.ToUniversalTime();
            operation.ProcessActive = true;
            Store.Save(operation);
            var elapsed = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                if (operation.Transport == "protocol") Store.Reconcile(operation);
                Changed?.Invoke();
                if (action == "inspect" && elapsed.Elapsed > InspectTimeout)
                    throw new TimeoutException("Read-only inspection timed out. No process was stopped; review the owner's log before trying again.");
                await Task.Delay(300, closeToken);
            }
            operation.ProcessActive = false;
            operation.ExitCode = process.ExitCode;
            if (capabilities is null)
            {
                operation.Finished = true;
                operation.Problem = process.ExitCode switch
                {
                    0 when interactiveSetup => "Setup closed. Checking PC Agent configuration.",
                    0 => "Installer closed. Local files were checked; this legacy installer does not report completion or health.",
                    1602 => "Installation cancelled. Local state was checked.",
                    3010 or 1641 => "Windows restart required. Recheck this application after restarting.",
                    _ => "Installer returned " + process.ExitCode + ". Review its window or log before retrying."
                };
                if (process.ExitCode is 3010 or 1641)
                    operation.Result = new(1, operation.Request.OperationId, product, action, "restart-required", DateTimeOffset.UtcNow, operation.Problem);
            }
            else
            {
                // A result may be atomically committed just after the parent's exit. A running
                // result remains pending even after this grace period; never kill a prompt/worker.
                for (var i = 0; i < 10; i++)
                {
                    Store.Reconcile(operation);
                    if (operation.Result is not null) break;
                    await Task.Delay(200, closeToken);
                }
                if (operation.Result is null)
                {
                    operation.RequiresReview = true;
                    operation.Problem = "The installer exited without a verified result. Recheck through the product's maintenance adapter before retrying.";
                }
                else Store.Reconcile(operation);
                if (operation.Result?.IsSuccess == true && process.ExitCode != 0)
                {
                    operation.RequiresReview = true;
                    operation.Problem = "Result and process exit disagree. Recheck the product before retrying.";
                }
            }
        }
        catch (OperationCanceledException) when (closeToken.IsCancellationRequested)
        { operation.Problem = "Toolkit closed while the operation was pending. Its durable result will be checked when Toolkit reopens."; }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { operation.Finished = true; operation.Problem = "Administrator prompt cancelled. No new process was started."; }
        catch (Exception error)
        {
            operation.Finished = operation.ProcessId is null;
            operation.RequiresReview = operation.ProcessId is not null;
            operation.Problem = error.Message;
        }
        finally { Store.Save(operation); Changed?.Invoke(); }
        return operation;
    }

    public bool Reconcile()
    {
        var changed = false;
        foreach (var operation in Operations.Where(o => o.Pending).ToArray())
        {
            if (operation.ProcessActive && operation.ProcessId is { } id)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (process.StartTime.ToUniversalTime() != operation.ProcessStartedUtc) operation.ProcessActive = false;
                    else if (process.HasExited) { operation.ExitCode = process.ExitCode; operation.ProcessActive = false; }
                }
                catch (ArgumentException) { operation.ProcessActive = false; }
                catch (InvalidOperationException) { operation.ProcessActive = false; }
                catch (System.ComponentModel.Win32Exception) { }
                if (!operation.ProcessActive)
                {
                    operation.ExitUnconfirmed = operation.ExitCode is null;
                    Store.Save(operation); changed = true;
                }
            }
            changed |= Store.Reconcile(operation);
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    public void ReleaseAfterInspect(string product, LifecycleOperation inspection)
    {
        var result = inspection.Result;
        if (inspection.Request.ProductId != product || inspection.Request.Action != "inspect" || result?.IsSuccess != true ||
            result.SafeToRetry != true || result.ActiveOperationId is not null || result.RecoveryRequired == true || inspection.RequiresReview)
            throw new InvalidOperationException("The product has not confirmed that recovery is safe. " + (result?.NextAction ?? result?.Message ?? inspection.Message));
        foreach (var old in Operations.Where(o => o.Request.ProductId == product && o.Request.Action != "inspect" && o.Pending))
        {
            old.Finished = true; old.RequiresReview = false; old.ProcessActive = false;
            old.Problem = "Previous attempt remains unconfirmed. Product inspection confirmed that another operation is safe.";
            // Retain the original wire result; an explicit reconciliation flag must not invent success.
            old.ReconciledBy = inspection.Request.OperationId;
            Store.Save(old);
        }
        Changed?.Invoke();
    }
}
