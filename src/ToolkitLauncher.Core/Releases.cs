using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ToolkitLauncher.Core;

public sealed class ReleaseAsset
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string? Digest { get; set; }
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
}

public sealed class Release
{
    [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    public List<ReleaseAsset> Assets { get; set; } = [];
    [JsonIgnore] public bool IsDevelopment => Prerelease || AppVersion.Parse(Tag)?.IsPrerelease == true ||
        Regex.IsMatch(Tag, @"(?i)(^|[-_.])(dev|alpha|beta|rc|preview|nightly)([-_.\d]|$)");
}

public sealed record ReleaseSnapshot(List<Release> Releases, DateTimeOffset CheckedAt)
{
    [JsonIgnore] public bool IsCached { get; init; }
}

public static class ReleaseSelection
{
    public static Release? Latest(IEnumerable<Release> releases, ReleaseChannel channel) => releases
        .Where(r => !r.Draft && r.IsDevelopment == (channel == ReleaseChannel.Development))
        .OrderByDescending(r => r.PublishedAt).FirstOrDefault();

    public static ReleaseAsset? Installer(AppDefinition app, Release release)
    {
        foreach (var pattern in app.AssetPatterns)
        {
            var asset = release.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, pattern, RegexOptions.IgnoreCase));
            if (asset is not null) return asset;
        }
        return null;
    }
}

public sealed record AppVersion(int Major, int Minor, int Patch, int Revision, string? Pre) : IComparable<AppVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Pre);
    public static AppVersion? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.Trim(), @"^[vV]?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) return null;
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
            if (match.Groups[i + 1].Success && !int.TryParse(match.Groups[i + 1].Value, out numbers[i])) return null;
        return new(numbers[0], numbers[1], numbers[2], numbers[3], match.Groups[5].Success ? match.Groups[5].Value : null);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var a = new[] { Major, Minor, Patch, Revision };
        var b = new[] { other.Major, other.Minor, other.Patch, other.Revision };
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        var left = Pre!.Split('.'); var right = other.Pre!.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] == right[i]) continue;
            var ln = long.TryParse(left[i], out var l); var rn = long.TryParse(right[i], out var r);
            return ln && rn ? l.CompareTo(r) : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
        }
        return left.Length.CompareTo(right.Length);
    }
}

public enum UpdateState { NotInstalled, Unknown, Current, UpdateAvailable, SwitchChannel, NewerInstalled, NoRelease }

public static class VersionStatus
{
    public static bool UsesEquivalentPackage(AppDefinition app, string? installedVersion, Release selected, IEnumerable<Release> releases)
    {
        var local = AppVersion.Parse(installedVersion);
        var digest = ReleaseSelection.Installer(app, selected)?.Digest;
        if (local is null || string.IsNullOrWhiteSpace(digest)) return false;
        return releases.Any(r => !r.Draft && r.IsDevelopment == local.IsPrerelease &&
            AppVersion.Parse(r.Tag)?.CompareTo(local) == 0 &&
            string.Equals(ReleaseSelection.Installer(app, r)?.Digest, digest, StringComparison.OrdinalIgnoreCase));
    }

    public static UpdateState Evaluate(bool installed, string? installedVersion, Release? release, ReleaseChannel channel)
    {
        if (!installed) return release is null ? UpdateState.NoRelease : UpdateState.NotInstalled;
        if (release is null) return UpdateState.NoRelease;
        var local = AppVersion.Parse(installedVersion); var remote = AppVersion.Parse(release.Tag);
        if (local is null || remote is null) return UpdateState.Unknown;
        if (local.IsPrerelease != (channel == ReleaseChannel.Development)) return UpdateState.SwitchChannel;
        var comparison = local.CompareTo(remote);
        return comparison == 0 ? UpdateState.Current : comparison < 0 ? UpdateState.UpdateAvailable : UpdateState.NewerInstalled;
    }
}
