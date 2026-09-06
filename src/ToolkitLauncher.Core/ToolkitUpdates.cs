namespace ToolkitLauncher.Core;

public sealed record ToolkitUpdate(Release Release, ReleaseAsset Installer);

public static class ToolkitUpdates
{
    public static readonly AppDefinition Application = new("toolkit", "Production Toolkit", "", "", "Production-Toolkit", "", "",
        [@"^Production-Toolkit-[0-9.]+-win-x64-Setup\.exe$"], PackageKind.Executable, [], ["Production Toolkit.exe"], [], []);

    public static ToolkitUpdate? Available(string currentVersion, IEnumerable<Release> releases)
    {
        var current = AppVersion.Parse(currentVersion) ?? throw new InvalidDataException("The toolkit version cannot be read.");
        var newer = releases.Where(r => !r.Draft && !r.IsDevelopment && AppVersion.Parse(r.Tag)?.CompareTo(current) > 0)
            .OrderByDescending(r => AppVersion.Parse(r.Tag));
        foreach (var release in newer)
        {
            var expectedName = $"Production-Toolkit-{release.Tag.TrimStart('v', 'V')}-win-x64-Setup.exe";
            var installer = release.Assets.SingleOrDefault(a => a.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            if (installer is null) continue;
            PackageService.ValidateSource(Application, installer);
            return new(release, installer);
        }
        return null;
    }
}
