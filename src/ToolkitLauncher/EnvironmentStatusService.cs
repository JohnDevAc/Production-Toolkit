using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public static class EnvironmentStatusService
{
    public static async Task<EnvironmentSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var evidence = new EnvironmentEvidence();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var resource = typeof(EnvironmentStatusService).Assembly.GetManifestResourceStream("ToolkitLauncher.Read-Environment.ps1")!;
            using var reader = new StreamReader(resource);
            var script = await reader.ReadToEndAsync(timeout.Token);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var local = await RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout.Token);
            if (local.Code != 0) throw new IOException("Windows environment details could not be read.");
            evidence = JsonSerializer.Deserialize<EnvironmentEvidence>(local.Output, GitHubClient.JsonOptions) ?? throw new InvalidDataException("Environment evidence is missing.");
            // Both dashboard cards use the same installed pair/configuration rules.
            ApplyAgentReadiness(evidence, InstallationService.Find(Catalog.Apps.Single(a => a.IsPcAgent), null, null));
            if (evidence.PcAgentConfiguration is { IsValid: true } agent)
            {
                try
                {
                    evidence.PcAgentNetworkAvailable = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .Any(n => n.Id.Equals(agent.AdapterId, StringComparison.OrdinalIgnoreCase)
                            && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == agent.Address && a.PrefixLength == agent.PrefixLength));
                }
                catch (System.Net.NetworkInformation.NetworkInformationException) { evidence.PcAgentNetworkAvailable = null; }
            }
            using var details = JsonDocument.Parse(local.Output);
            var distro = details.RootElement.GetProperty("DistroName").GetString()!;
            var webPort = details.RootElement.GetProperty("WebPort").GetInt32();
            var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            await ReadContainerAsync(evidence, distro, (arguments, token) => RunAsync(wsl, arguments, token), timeout.Token);
            if (evidence.ContainerRunning == true && webPort is > 0 and <= 65535)
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{webPort}/", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    evidence.WebResponding = (int)response.StatusCode is >= 200 and < 400;
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { evidence.WebResponding = false; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { evidence.Note += " Some environment details could not be read. Open Setup to review the installation."; }
        cancellationToken.ThrowIfCancellationRequested();
        return EnvironmentSnapshot.From(evidence, DateTimeOffset.Now);
    }

    public static void ApplyAgentReadiness(EnvironmentEvidence evidence, Installation? agent)
    {
        evidence.PcAgent = agent is { IsPresent: true };
        evidence.PcAgentConfigured = agent?.PcAgent is { NeedsSetup: false };
        evidence.PcAgentSetupDetail = agent?.PcAgent?.Detail;
    }

    private static async Task ReadContainerAsync(EnvironmentEvidence evidence, string distro,
        Func<string[], CancellationToken, Task<(int Code, string Output)>> runWsl, CancellationToken token)
    {
        if (evidence.Distro == false)
        {
            evidence.DistroRunning = false; evidence.Container = false; evidence.ContainerRunning = false;
            return;
        }
        if (evidence.Distro != true || !Regex.IsMatch(distro, @"\A[a-zA-Z0-9_.-]{1,64}\z")) return;
        var running = await runWsl(["--list", "--running", "--quiet"], token);
        if (running.Code != 0) { evidence.Note += " WSL running state could not be read."; return; }
        evidence.DistroRunning = running.Output.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name.Trim(), distro, StringComparison.OrdinalIgnoreCase));
        // All Docker queries share one invocation of an already-running distribution.
        // Never start/pull/update a distro, service, image or container to identify it.
        if (evidence.DistroRunning != true) return;
        const string command = "names=$(docker container ls -a --filter 'name=^/KLNKSVR-pro$' --format '{{.Names}}') || exit 2; " +
            "if [ \"$names\" != 'KLNKSVR-pro' ]; then printf '{\"Present\":false}'; else " +
            "container=$(docker container inspect --format '{\"Present\":true,\"Status\":{{json .State.Status}},\"Image\":{{json .Config.Image}},\"ImageId\":{{json .Image}},\"Version\":{{json (index .Config.Labels \"org.opencontainers.image.version\")}}}' KLNKSVR-pro) || exit 2; " +
            "image_id=$(docker container inspect --format '{{.Image}}' KLNKSVR-pro) || exit 2; " +
            "identity=$(docker image inspect --format '{\"Id\":{{json .Id}},\"RepoTags\":{{json .RepoTags}},\"RepoDigests\":{{json .RepoDigests}}}' \"$image_id\" 2>/dev/null) || identity=null; " +
            "printf '{\"Container\":%s,\"ImageIdentity\":%s}' \"$container\" \"$identity\"; fi";
        var inspected = await runWsl(["--distribution", distro, "--user", "root", "--exec", "/bin/sh", "-c", command], token);
        if (inspected.Code == 0) ApplyContainerInspection(evidence, inspected.Output);
        else evidence.Note += " Docker or the KiloLink container could not be inspected.";
    }

    public static void ApplyContainerInspection(EnvironmentEvidence evidence, string output)
    {
        evidence.Container = null; evidence.ContainerRunning = null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var container = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Container", out var nested) ? nested : root;
            if (container.ValueKind != JsonValueKind.Object || !container.TryGetProperty("Present", out var present))
                throw new JsonException("Missing container presence evidence.");
            if (present.ValueKind == JsonValueKind.False) { evidence.Container = false; evidence.ContainerRunning = false; return; }
            if (present.ValueKind != JsonValueKind.True) throw new JsonException("Invalid container presence evidence.");
            evidence.ContainerImage = ReadString(container, "Image");
            evidence.ContainerVersion = ReadString(container, "Version");
            evidence.ContainerRunning = ReadString(container, "Status") switch
            {
                "running" => true, "created" or "restarting" or "paused" or "exited" or "dead" or "removing" => false, _ => null
            };
            evidence.Container = IsKiloLinkImage(container, root.TryGetProperty("ImageIdentity", out var identity) ? identity : default);
            if (evidence.Container != true) evidence.Note += evidence.Container == false
                ? " The named container does not use the KiloLink image."
                : " The named container's KiloLink image identity could not be verified.";
        }
        catch (JsonException) { evidence.Note += " Docker container evidence could not be read."; }
    }

    private static string? ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool? IsKiloLinkImage(JsonElement container, JsonElement identity)
    {
        const string repository = @"\A(?:docker\.io/)?kiloview/klnk-pro(?::[\w.-]+|@sha256:[a-fA-F0-9]{64})?\z";
        const string imageId = @"\Asha256:[a-fA-F0-9]{64}\z";
        var reference = ReadString(container, "Image");
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (Regex.IsMatch(reference, repository)) return true;
        if (!Regex.IsMatch(reference, imageId)) return reference.StartsWith("sha256:", StringComparison.Ordinal) ? null : false;
        // Config.Image may be an immutable image ID. Verify the local image by that
        // exact ID and its repository references; a familiar container name is not proof.
        var immutableId = ReadString(container, "ImageId");
        if (!string.Equals(reference, immutableId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(immutableId, ReadString(identity, "Id"), StringComparison.OrdinalIgnoreCase)) return null;
        List<string> references = [];
        foreach (var property in new[] { "RepoTags", "RepoDigests" })
        {
            if (!identity.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null) continue;
            if (values.ValueKind != JsonValueKind.Array) return null;
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return null;
                references.Add(value.GetString()!);
            }
        }
        return references.Count == 0 ? null : references.Any(value => Regex.IsMatch(value, repository));
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the environment check.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            await error;
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
    }
}
