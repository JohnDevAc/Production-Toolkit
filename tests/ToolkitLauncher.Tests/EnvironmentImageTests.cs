using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    // Exact Config.Image / status recorded in the master's read-only Server health capture.
    private const string CapturedKiloLinkImage = "sha256:55bd35acb6378eda2a540fe5ba6ee88af7494d8e6ba5d478c48b6da8bddf0eb7";

    private static void EnvironmentImageTests(string? capturedRoot = null, string? probeOutput = null)
    {
        var evidence = FullEnvironment(); evidence.Roles = ["server"];
        EnvironmentStatusService.ApplyContainerInspection(evidence, JsonSerializer.Serialize(new
        { Present = true, Status = "running", Image = CapturedKiloLinkImage, Version = (string?)null }));
        Check(evidence.Container is null && evidence.ContainerRunning == true && evidence.WebResponding == true &&
            EnvironmentSnapshot.From(evidence, DateTimeOffset.Now).State == EnvironmentInstallationState.Unknown,
            "Captured digest-only Config.Image is unverified rather than falsely absent without repository evidence");

        JsonObject Captured() => JsonSerializer.SerializeToNode(new
        {
            Container = new { Present = true, Status = "running", Image = CapturedKiloLinkImage, ImageId = CapturedKiloLinkImage, Version = (string?)null },
            ImageIdentity = new { Id = CapturedKiloLinkImage, RepoTags = new[] { "kiloview/klnk-pro:latest" },
                RepoDigests = new[] { "kiloview/klnk-pro@" + CapturedKiloLinkImage } }
        })!.AsObject();
        EnvironmentEvidence Inspect(JsonNode value)
        {
            var result = FullEnvironment(); result.Roles = ["server"];
            EnvironmentStatusService.ApplyContainerInspection(result, value.ToJsonString());
            return result;
        }
        var captured = Captured(); evidence = Inspect(captured);
        var snapshot = EnvironmentSnapshot.From(evidence, DateTimeOffset.Now);
        Check(evidence.Container == true && evidence.ContainerRunning == true && snapshot.State == EnvironmentInstallationState.Full &&
            snapshot.Components[0].Status == "Installed · running", "Exact captured immutable ID and repository metadata establish the installed running KiloLink container");
        var card = new AppCard(Catalog.Apps.Single(a => a.IsEnvironment))
        { Installed = new("C:\\Fixture\\setup.exe", "3.1.0"), Snapshot = new([Release("v3.1.0")], DateTimeOffset.UtcNow), EnvironmentStatus = snapshot };
        Check(card.EnvironmentSummary == "Server · Installed" && !card.NeedsEnvironmentSetup && !card.ShowPrimary && card.CanLaunch,
            "Verified Server evidence removes false Complete setup while retaining Open setup");

        foreach (var foreign in new[] { "kiloview/klnk-pro-other:latest", "other/klnk-pro:latest", "evil.example/kiloview/klnk-pro:latest", "docker.io.evil/kiloview/klnk-pro:latest", "kiloview/klnk-pro:latest\n" })
        {
            var value = Captured(); value["ImageIdentity"]!["RepoTags"] = new JsonArray(foreign); value["ImageIdentity"]!["RepoDigests"] = new JsonArray();
            Check(Inspect(value).Container == false, "An unrelated image with a similar repository reference is rejected: " + foreign.Trim());
        }
        var anotherId = "sha256:" + new string('a', 64);
        foreach (var section in new[] { "Container", "ImageIdentity" })
        {
            var value = Captured(); value[section]![section == "Container" ? "ImageId" : "Id"] = anotherId;
            Check(Inspect(value).Container is null, "Mismatched immutable " + section + " identity stays unknown even with a KiloLink tag");
        }
        foreach (var identity in new JsonNode?[] { null, new JsonObject(), JsonValue.Create("unreadable"),
            new JsonObject { ["Id"] = CapturedKiloLinkImage, ["RepoTags"] = new JsonArray(), ["RepoDigests"] = null },
            new JsonObject { ["Id"] = CapturedKiloLinkImage, ["RepoTags"] = "kiloview/klnk-pro:latest" } })
        {
            var value = Captured(); value["ImageIdentity"] = identity;
            evidence = Inspect(value); snapshot = EnvironmentSnapshot.From(evidence, DateTimeOffset.Now);
            Check(evidence.Container is null && snapshot.State == EnvironmentInstallationState.Unknown && snapshot.Components[0].Status == "Not verified",
                "Missing or unverifiable repository metadata stays unknown instead of absent or installed");
            card.EnvironmentStatus = snapshot;
            Check(card.Status == "Installation not verified" && !card.NeedsEnvironmentSetup,
                "Unknown container identity does not invent an incomplete setup");
        }
        var digestOnly = Captured(); digestOnly["ImageIdentity"]!["RepoTags"] = null;
        digestOnly["ImageIdentity"]!["RepoDigests"] = new JsonArray("docker.io/kiloview/klnk-pro@" + anotherId);
        Check(Inspect(digestOnly).Container == true, "Repository digest can establish identity; its manifest digest need not equal the image config ID");
        foreach (var reference in new[] { "kiloview/klnk-pro", "kiloview/klnk-pro:latest", "docker.io/kiloview/klnk-pro:1.0", "kiloview/klnk-pro@" + anotherId })
        {
            var value = Captured(); value["Container"]!["Image"] = reference; value["ImageIdentity"] = null;
            Check(Inspect(value).Container == true, "Existing repository-qualified image detection is preserved: " + reference);
        }
        var noHealth = Captured(); noHealth["Container"]!["Status"] = null;
        evidence = Inspect(noHealth); evidence.WebResponding = null;
        snapshot = EnvironmentSnapshot.From(evidence, DateTimeOffset.Now);
        Check(evidence.Container == true && evidence.ContainerRunning is null && evidence.WebResponding is null &&
            snapshot.Components[0].Status == "Installed · state unknown" && snapshot.Components[0].Detail.Contains("Web interface: not checked"),
            "Verified installation does not invent unknown runtime or HTTP health");
        var stopped = Captured(); stopped["Container"]!["Status"] = "exited";
        Check(Inspect(stopped).ContainerRunning == false, "Explicit stopped state remains stopped");
        var absent = Inspect(new JsonObject { ["Present"] = false });
        Check(absent.Container == false && absent.ContainerRunning == false, "Confirmed missing named container remains absent");
        foreach (var malformed in new[] { "{broken", "[]", "{}", "{\"Present\":null}", "{\"Container\":[]}" })
        {
            evidence = FullEnvironment(); EnvironmentStatusService.ApplyContainerInspection(evidence, malformed);
            Check(evidence.Container is null && evidence.ContainerRunning is null, "Malformed container evidence cannot retain a false installed/running result");
        }

        var method = typeof(EnvironmentStatusService).GetMethod("ReadContainerAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        List<string[]> calls = [];
        void Probe(EnvironmentEvidence value, string distro, int listCode, string listOutput, int inspectCode = 0)
        {
            calls.Clear();
            Func<string[], CancellationToken, Task<(int Code, string Output)>> runner = (arguments, token) =>
            {
                calls.Add(arguments);
                return Task.FromResult(calls.Count == 1 ? (listCode, listOutput) : (inspectCode, Captured().ToJsonString()));
            };
            ((Task)method.Invoke(null, [value, distro, runner, CancellationToken.None])!).GetAwaiter().GetResult();
        }
        evidence = new() { Distro = true };
        Probe(evidence, "KiloLink-Ubuntu", 0, "KiloLink-Ubuntu\r\n");
        Check(calls.Count == 2 && calls[0].SequenceEqual(new[] { "--list", "--running", "--quiet" }) && evidence.Container == true,
            "Actual runtime reader verifies the distro is already running before its single read-only Docker invocation");
        Check(calls[1][^1].Contains("docker image inspect") && calls[1][^1].Contains("\"$image_id\"") &&
            !calls[1][^1].Contains("docker pull") && !calls[1][^1].Contains("docker start") && !calls[1][^1].Contains("systemctl") &&
            !calls[1].Contains("--update") && !calls[1].Contains("--install"), "Image lookup uses the observed immutable ID with no start, pull, update or service mutation");
        var script = probeOutput ?? Path.Combine(Temporary, "environment-container-probe.sh"); File.WriteAllText(script, calls[1][^1]);
        Console.WriteLine("Read-only shell probe fixture: " + script);
        Probe(new() { Distro = true }, "KiloLink-Ubuntu", 0, "Other-Distro\r\n");
        Check(calls.Count == 1, "Stopped target distro is never activated for inspection");
        Probe(new() { Distro = true }, "KiloLink-Ubuntu", 1, "");
        Check(calls.Count == 1, "Unreadable running-distro list cannot authorize a distro invocation");
        foreach (var value in new bool?[] { false, null })
        {
            Probe(new() { Distro = value }, "KiloLink-Ubuntu", 0, "KiloLink-Ubuntu");
            Check(calls.Count == 0, "Absent or unknown distro registration cannot invoke WSL");
        }
        Probe(new() { Distro = true }, "invalid/distro", 0, "invalid/distro");
        Check(calls.Count == 0, "Invalid distro identifier cannot invoke WSL");
        evidence = new() { Distro = true }; Probe(evidence, "KiloLink-Ubuntu", 0, "KiloLink-Ubuntu", 2);
        Check(evidence.Container is null && evidence.ContainerRunning is null, "Failed Docker inspection leaves presence and running state unknown");
        if (capturedRoot is not null) ReplayCapturedEnvironmentImage(capturedRoot);
    }

    private static void ReplayCapturedEnvironmentImage(string root)
    {
        var healthPath = Path.Combine(root, "qa8-server-candidate7-success", "health.json");
        var identityPath = Path.Combine(root, "vm-environment7-image-identity.json");
        var inspectPath = Path.Combine(root, "qa8-server-candidate7-wrapper", "76ace37d-4edb-41a3-95ed-a87d918538e3", "result.json");
        var journalPath = Path.Combine(root, "qa8-server-candidate7-wrapper", "af6319ca-9005-4b7c-a194-17196b91dd37", "journal.json");
        var before = new[] { healthPath, identityPath, inspectPath, journalPath }.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        using var health = JsonDocument.Parse(File.ReadAllText(healthPath));
        using var identity = JsonDocument.Parse(File.ReadAllText(identityPath));
        using var inspect = JsonDocument.Parse(File.ReadAllText(inspectPath));
        var runtime = Regex.Match(health.RootElement.GetProperty("linux").GetProperty("stdout").GetString()!, @"(?m)^running true 0 (sha256:[a-f0-9]{64})\r?$");
        var immutable = identity.RootElement.GetProperty("container").GetProperty("stdout").GetString()!.Trim();
        var image = identity.RootElement.GetProperty("image").GetProperty("stdout").GetString()!.Trim().Split('|');
        Check(runtime.Success && runtime.Groups[1].Value == immutable && immutable == CapturedKiloLinkImage &&
            identity.RootElement.GetProperty("container").GetProperty("exitCode").GetInt32() == 0 &&
            identity.RootElement.GetProperty("image").GetProperty("exitCode").GetInt32() == 0 &&
            !identity.RootElement.GetProperty("image").GetProperty("timedOut").GetBoolean(),
            "Raw master health and image probes correlate the exact immutable image successfully");
        var captured = new JsonObject
        {
            ["Container"] = new JsonObject { ["Present"] = true, ["Status"] = "running", ["Image"] = runtime.Groups[1].Value, ["ImageId"] = immutable, ["Version"] = null },
            ["ImageIdentity"] = new JsonObject { ["Id"] = JsonNode.Parse(image[0]), ["RepoTags"] = JsonNode.Parse(image[1]), ["RepoDigests"] = JsonNode.Parse(image[2]) }
        };
        var evidence = new EnvironmentEvidence();
        EnvironmentStatusService.ApplyContainerInspection(evidence, captured.ToJsonString());
        var kilo = inspect.RootElement.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("productId").GetString() == "kilolink");
        Check(evidence.Container == true && evidence.ContainerRunning == true && evidence.WebResponding is null &&
            kilo.GetProperty("installed").ValueKind == JsonValueKind.Null && kilo.GetProperty("running").ValueKind == JsonValueKind.Null &&
            kilo.GetProperty("healthy").ValueKind == JsonValueKind.Null,
            "Actual captured image evidence verifies presence/running independently without upgrading owner unknowns or inferring HTTP health");
        Check(before.All(item => item.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(item.Key)))),
            "Evidence replay leaves owner result and original unobserved-exit journal bytes unchanged");
    }
}
