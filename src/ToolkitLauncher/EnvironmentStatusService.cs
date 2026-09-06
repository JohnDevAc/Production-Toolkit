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
            using var details = JsonDocument.Parse(local.Output);
            var distro = details.RootElement.GetProperty("DistroName").GetString()!;
            var webPort = details.RootElement.GetProperty("WebPort").GetInt32();
            if (evidence.Distro == false)
            {
                evidence.DistroRunning = false; evidence.Container = false; evidence.ContainerRunning = false;
            }
            else if (evidence.Distro == true && Regex.IsMatch(distro, "^[a-zA-Z0-9_.-]{1,64}$"))
            {
                var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
                var running = await RunAsync(wsl, ["--list", "--running", "--quiet"], timeout.Token);
                if (running.Code == 0)
                {
                    evidence.DistroRunning = running.Output.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Any(name => string.Equals(name.Trim(), distro, StringComparison.OrdinalIgnoreCase));
                    // Inspect only a distribution already reported running. Never start Docker or the container.
                    if (evidence.DistroRunning == true)
                    {
                        const string command = "names=$(docker container ls -a --filter 'name=^/KLNKSVR-pro$' --format '{{.Names}}') || exit 2; " +
                            "if [ \"$names\" != 'KLNKSVR-pro' ]; then printf '{\"Present\":false}'; else " +
                            "docker container inspect --format '{\"Present\":true,\"Status\":{{json .State.Status}},\"Image\":{{json .Config.Image}},\"Version\":{{json (index .Config.Labels \"org.opencontainers.image.version\")}}}' KLNKSVR-pro; fi";
                        var inspected = await RunAsync(wsl, ["--distribution", distro, "--user", "root", "--exec", "/bin/sh", "-c", command], timeout.Token);
                        if (inspected.Code == 0)
                        {
                            using var container = JsonDocument.Parse(inspected.Output);
                            evidence.Container = container.RootElement.GetProperty("Present").GetBoolean();
                            if (evidence.Container == true)
                            {
                                evidence.ContainerImage = container.RootElement.GetProperty("Image").GetString();
                                evidence.ContainerVersion = container.RootElement.GetProperty("Version").GetString();
                                if (!Regex.IsMatch(evidence.ContainerImage ?? "", @"^(?:docker\.io/)?kiloview/klnk-pro(?::[\w.-]+|@sha256:[a-fA-F0-9]{64})?$"))
                                { evidence.Container = false; evidence.Note += " The named container does not use the KiloLink image."; }
                                evidence.ContainerRunning = container.RootElement.GetProperty("Status").GetString() == "running";
                            }
                        }
                        else evidence.Note += " Docker or the KiloLink container could not be inspected.";
                    }
                }
                else evidence.Note += " WSL running state could not be read.";
            }
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
