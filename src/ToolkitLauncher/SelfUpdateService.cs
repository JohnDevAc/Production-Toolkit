using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed class SelfUpdateService(GitHubClient github, HttpClient downloadHttp, string cacheRoot)
{
    public static string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public async Task<ToolkitUpdate?> CheckAsync(CancellationToken cancellationToken, bool userRequested = false) => ToolkitUpdates.Available(CurrentVersion,
        (await github.GetReleasesAsync(ToolkitUpdates.Application, cancellationToken, userRequested)).Releases);

    public async Task<string> DownloadAsync(ToolkitUpdate update, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var package = await new PackageService(downloadHttp, cacheRoot).PrepareAsync(ToolkitUpdates.Application, update.Installer, progress, cancellationToken);
        ValidateInstallerVersion(package.SetupPath, update.Release.Tag);
        return package.SetupPath;
    }

    public static void ValidateInstallerVersion(string path, string releaseTag)
    {
        var actual = AppVersion.Parse(FileVersionInfo.GetVersionInfo(path).FileVersion);
        if (actual is null || actual.CompareTo(AppVersion.Parse(releaseTag)) != 0)
            throw new InvalidDataException("The verified installer does not contain the expected toolkit version.");
    }

    public static ProcessStartInfo StartInfo(string installer)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(installer))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installer))!
        };
        // Inno Setup reuses its registered installation folder. No shell interpolation,
        // elevation, forced termination or reboot is needed for this per-user app.
        foreach (var argument in new[] { "/SILENT", "/SP-", "/NORESTART", "/TOOLKITUPDATE=1", "/LOG=" + installer + ".log" })
            info.ArgumentList.Add(argument);
        return info;
    }
}
