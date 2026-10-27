using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolkitLauncher.Core;

namespace ToolkitLauncher.Acceptance;

internal sealed record Candidate(string Product, string Version, string File, long Size, string Sha256);
internal sealed record CandidateManifest(int SchemaVersion, Guid Id, string ProductionSourceCommit, Candidate[] Packages)
{
    public const string SourceCommit = "dfb301ec45ba5941a22c0feb0192601f2640ad82";
    // This test host is intentionally limited to the coordinated, unpublished candidate set.
    // A new candidate requires a reviewed change to test infrastructure, never a product override.
    public static readonly Candidate[] Pins =
    [
        new("environment", "3.1.0", "Kiloview-Environment-Setup-Lifecycle.zip", 16137140, "65bb32838bca0dc34b232ba2c0f7ee3db2db1b4e2bbf5ea4f628b4fcea234064"),
        new("job", "0.8.16-dev.1", "NDI-Job-Configurator-Windows.zip", 212588692, "e0fd2245f7491f955427814188de0ade0ec557578cc560b208e64d9fc803a58a"),
        new("pc-agent", "0.7.5-dev.1", "NDI-Configurator-PC-Agent-win-x64.zip", 133286854, "fee73bf85f55dc3ff9b843b40742386962ca3317eb1a55e3ab3a90a2174d3e9e"),
        new("resolume", "0.3.8", "Resolume-Arena-Configurator-v0.3.8-win-x64.zip", 66340429, "4ae8fb43e592cb3e131e126c557e3dd7753773e8e4030c7a7e909df489d1af0c")
    ];
    internal static readonly JsonSerializerOptions Json = new(OwnedPaths.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static CandidateManifest Read(string path)
    {
        var value = JsonSerializer.Deserialize<CandidateManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Missing acceptance manifest.");
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || ProductionSourceCommit != SourceCommit || Packages is null || Packages.Length != Pins.Length)
            throw new InvalidDataException("Unsupported acceptance manifest or production source.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in Packages)
        {
            var pin = Pins.SingleOrDefault(p => p.Product == package.Product);
            if (pin is null || !seen.Add(package.Product) || package.Version != pin.Version || package.Sha256 != pin.Sha256 ||
                package.Size <= 0 || pin.Size > 0 && package.Size != pin.Size || package.File != "packages/" + pin.File)
                throw new InvalidDataException("Package identity, version, path, size or SHA256 differs from the approved QA candidate: " + package.Product);
            PackageService.ValidateSource(Definition(package), Asset(package));
        }
    }

    public static AppDefinition Definition(Candidate package) => Catalog.Apps.Single(a => a.Id == package.Product);
    public static ReleaseAsset Asset(Candidate package) => new()
    {
        Name = Path.GetFileName(package.File), Size = package.Size, Digest = "sha256:" + package.Sha256,
        DownloadUrl = Definition(package).RepositoryUrl + "/releases/download/v" + package.Version + "/" + Path.GetFileName(package.File)
    };
    public static Release Release(Candidate package) => new()
    {
        Tag = "v" + package.Version, Prerelease = AppVersion.Parse(package.Version)!.IsPrerelease,
        // This is QA feed metadata, not a claim that a public release exists.
        PublishedAt = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), Assets = [Asset(package)]
    };

    public async Task VerifyFilesAsync(string manifestDirectory)
    {
        foreach (var package in Packages)
        {
            var path = OwnedPaths.Resolve(manifestDirectory, package.File);
            if (!await PackageService.VerifyAsync(path, Asset(package), default))
                throw new InvalidDataException("Candidate bytes differ: " + package.Product);
            using var zip = ZipFile.OpenRead(path);
            var descriptors = zip.Entries.Where(e => e.FullName == "toolbox-lifecycle.json").ToArray();
            if (descriptors.Length != 1) throw new InvalidDataException("Expected one package-root lifecycle descriptor.");
            using var stream = descriptors[0].Open();
            var descriptor = await JsonSerializer.DeserializeAsync<LifecycleCapabilities>(stream, OwnedPaths.Json)
                ?? throw new InvalidDataException("Missing lifecycle descriptor.");
            if (descriptor.SchemaVersion != 1 || descriptor.ProductId != package.Product ||
                !new[] { "install", "update", "uninstall" }.All(descriptor.Supports))
                throw new InvalidDataException("Package does not advertise the required product lifecycle.");
        }
    }

    public async Task SeedAsync(string manifestDirectory, string downloadRoot)
    {
        foreach (var package in Packages)
        {
            var source = OwnedPaths.Resolve(manifestDirectory, package.File);
            var directory = Path.Combine(downloadRoot, package.Product, package.Sha256);
            OwnedPaths.CheckNoReparse(directory);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(package.File));
            OwnedPaths.CheckNoReparse(target);
            if (!File.Exists(target) || !await PackageService.VerifyAsync(target, Asset(package), default))
                File.Copy(source, target, true);
            if (!await PackageService.VerifyAsync(target, Asset(package), default)) throw new InvalidDataException("Staged candidate changed.");
        }
    }
}

internal sealed class ManifestFeed(CandidateManifest manifest, Action<string> log) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        if (request.Method != HttpMethod.Get || uri is null || uri.Scheme != "https" || uri.Host != "api.github.com" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query != "?per_page=100&page=1")
            throw new HttpRequestException("QA host rejected an unconfigured feed request.");
        var package = manifest.Packages.SingleOrDefault(p => uri.AbsolutePath == "/repos/JohnDevAc/" + CandidateManifest.Definition(p).Repository + "/releases");
        List<Release> releases = package is not null ? [CandidateManifest.Release(package)]
            : uri.AbsolutePath == "/repos/JohnDevAc/Production-Toolkit/releases" ? []
            : throw new HttpRequestException("QA host rejected an unconfigured repository.");
        log("Manifest feed: " + uri.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(releases, GitHubClient.JsonOptions), Encoding.UTF8, "application/json") });
    }
}

internal sealed class RefuseNetwork(Action<string> log) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        log("Blocked Toolkit network request: " + request.RequestUri?.GetLeftPart(UriPartial.Path));
        throw new HttpRequestException("QA host permits only the manifest's verified local packages. Network transport is disabled.");
    }
}
