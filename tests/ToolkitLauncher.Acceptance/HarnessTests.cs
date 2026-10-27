using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolkitLauncher.Core;

namespace ToolkitLauncher.Acceptance;

internal static class HarnessTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Pass(string text) { count++; Console.WriteLine("PASS " + text); }
        void Reject(Action action, string text)
        {
            try { action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException or JsonException) { Pass(text); return; }
            throw new Exception("FAIL expected rejection: " + text);
        }
        async Task RejectHttp(Func<Task> action, string text)
        {
            try { await action(); } catch (HttpRequestException) { Pass(text); return; }
            throw new Exception("FAIL expected transport rejection: " + text);
        }
        var vm = new VmEvidence("TOOLKIT-QA", "QEMU", "Standard PC", 1, true, true, false);
        VmGuard.Validate(vm, "TOOLKIT-QA", true); Pass("Explicit ordinary interactive VM guard passes");
        Reject(() => VmGuard.Validate(vm with { Computer = "JOHN-PC" }, "JOHN-PC", true), "JOHN-PC refused even with matching acknowledgment");
        Reject(() => VmGuard.Validate(vm, "TOOLKIT-QA", false), "Real-operation acknowledgment required");
        Reject(() => VmGuard.Validate(vm, "OTHER-VM", true), "Exact VM computer identity required");
        Reject(() => VmGuard.Validate(vm with { Manufacturer = "Dell", Model = "OptiPlex" }, "TOOLKIT-QA", true), "Physical hardware refused");
        Reject(() => VmGuard.Validate(vm with { SessionId = 0 }, "TOOLKIT-QA", true), "Service session refused");
        Reject(() => VmGuard.Validate(vm with { Interactive = false }, "TOOLKIT-QA", true), "Noninteractive session refused");
        Reject(() => VmGuard.Validate(vm with { DesktopVisible = false }, "TOOLKIT-QA", true), "Hidden SSH/service desktop refused");
        Reject(() => VmGuard.Validate(vm with { Elevated = true }, "TOOLKIT-QA", true), "Elevated launch refused");
        var manifest = new CandidateManifest(1, Guid.NewGuid(), CandidateManifest.SourceCommit,
            CandidateManifest.Pins.Select(p => p with { File = "packages/" + p.File }).ToArray());
        manifest.Validate(); Pass("Exact coordinated candidate set accepted");
        CandidateManifest Replace(Candidate candidate) => manifest with { Packages = [candidate, .. manifest.Packages.Skip(1)] };
        Reject(() => (manifest with { SchemaVersion = 2 }).Validate(), "Unknown manifest schema refused");
        Reject(() => (manifest with { ProductionSourceCommit = "other" }).Validate(), "Different product source refused");
        Reject(() => (manifest with { Packages = manifest.Packages.Take(3).ToArray() }).Validate(), "Incomplete candidate set refused");
        Reject(() => Replace(manifest.Packages[1]).Validate(), "Duplicate product refused");
        Reject(() => Replace(manifest.Packages[0] with { Product = "udp-to-ndi" }).Validate(), "Unconfigured product refused");
        Reject(() => Replace(manifest.Packages[0] with { Version = "99.0.0" }).Validate(), "Artificial update version refused");
        Reject(() => Replace(manifest.Packages[0] with { Sha256 = new string('0', 64) }).Validate(), "Unapproved package digest refused");
        Reject(() => Replace(manifest.Packages[0] with { Size = 1 }).Validate(), "Incorrect candidate size refused");
        Reject(() => Replace(manifest.Packages[0] with { File = "../candidate.zip" }).Validate(), "Traversing package path refused");
        var json = JsonSerializer.SerializeToNode(manifest, CandidateManifest.Json)!.AsObject();
        json["schemaVersion"] = "1";
        Reject(() => JsonSerializer.Deserialize<CandidateManifest>(json.ToJsonString(), CandidateManifest.Json), "Coerced JSON schema type refused");
        json["schemaVersion"] = 1; json["feedUrl"] = "https://example.invalid/";
        Reject(() => JsonSerializer.Deserialize<CandidateManifest>(json.ToJsonString(), CandidateManifest.Json), "Unrecognized feed override refused");
        using var http = new HttpClient(new ManifestFeed(manifest, _ => { }));
        var github = new GitHubClient(http);
        foreach (var package in manifest.Packages)
        {
            var releases = await github.GetReleasesAsync(CandidateManifest.Definition(package), default);
            var selected = ReleaseSelection.ManagedPackage(CandidateManifest.Definition(package), releases.Releases.Single());
            if (selected?.Digest != "sha256:" + package.Sha256 || releases.Releases[0].Tag != "v" + package.Version) throw new Exception("Wrong pinned release selected.");
            PackageService.ValidateSource(CandidateManifest.Definition(package), selected);
            Pass("Real release selector retains exact " + package.Product + " identity/version/digest and official URL validation");
        }
        if ((await github.GetReleasesAsync(ToolkitUpdates.Application, default)).Releases.Count != 0) throw new Exception("Self update not disabled.");
        Pass("Toolkit self-update feed explicitly empty");
        await RejectHttp(() => github.GetReleasesAsync(Catalog.Apps.Single(a => a.Id == "udp-to-ndi"), default), "Public repository fallback refused");
        await RejectHttp(() => http.GetAsync("https://example.invalid/"), "Alternate feed origin refused");
        await RejectHttp(() => http.GetAsync("https://api.github.com/repos/JohnDevAc/Kiloview-PC-Onboarding/releases?per_page=100&page=2"), "Unconfigured feed query refused");
        using var denied = new HttpClient(new RefuseNetwork(_ => { }));
        await RejectHttp(() => denied.GetAsync(CandidateManifest.Asset(manifest.Packages[0]).DownloadUrl), "Missing local package cannot fall back to real GitHub download");
        ProductionHost.CheckSeams(); Pass("Unchanged 1.4.0 production assembly and required injection seams present");
        var evidence = Path.Combine(Path.GetTempPath(), "ToolkitQaFailureCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var expected = new InvalidOperationException("Injected startup failure", new InvalidDataException("Inner diagnostic"));
        try { LaunchFailure.Capture(evidence, () => "resource load", _ => { }, () => throw expected); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, expected)) { Pass("Launch failure preserves the original exception"); }
        using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "launch-error.json")));
        if (failure.RootElement.GetProperty("stage").GetString() != "resource load" ||
            !failure.RootElement.GetProperty("fullException").GetString()!.Contains("Inner diagnostic")) throw new Exception("Launch error lost stage or inner exception.");
        Pass("Durable launch error includes startup stage, stack and nested exception");
        Console.WriteLine($"PASS: {count} QA infrastructure checks. No GUI, product execution or OS settings changes.");
        return 0;
    }
}
