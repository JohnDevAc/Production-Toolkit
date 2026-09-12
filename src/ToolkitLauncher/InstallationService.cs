using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed record Installation(string Path, string? Version, bool IsSavedSetup = false)
{
    public SetupRecord? SavedSetup { get; init; }
    public PcAgentReadiness? PcAgent { get; init; }
    public bool? PcAgentRunning { get; init; }
}

public sealed class InstallationService(LocalState state)
{
    public Installation? Find(AppDefinition app)
    {
        return Find(app, state.Preferences.LaunchPaths.GetValueOrDefault(app.Id), state.Preferences.Setups.GetValueOrDefault(app.Id));
    }

    // Inputs are captured on the UI thread before an asynchronous check starts.
    public static Installation? Find(AppDefinition app, string? custom, SetupRecord? setup)
    {
        if (File.Exists(custom)) return ReadInstallation(app, custom);
        var paths = RegistryPaths(app).Concat(app.KnownPaths.Select(Environment.ExpandEnvironmentVariables));
        var installedPath = paths.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
        Installation? installed = installedPath is null ? null : ReadInstallation(app, installedPath);
        if (installed is null && app.IsEnvironment && setup is not null && File.Exists(setup.Path))
        {
            try
            {
                using var file = File.OpenRead(setup.Path);
                var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(file));
                if (string.Equals(digest, setup.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    // A verified download is reusable, but is not an installed version.
                    return new(setup.Path, null, true) { SavedSetup = setup };
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return installed;
    }

    private static Installation ReadInstallation(AppDefinition app, string path)
    {
        var installation = new Installation(path, ReadVersion(path));
        return app.IsPcAgent ? installation with
        {
            PcAgent = PcAgentReadiness.Read(app, installation),
            PcAgentRunning = PcAgentRuntime.Read(path)
        } : installation;
    }

    public static string? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            // ProductVersion retains dev identifiers; FileVersion commonly strips them.
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static IEnumerable<string> RegistryPaths(AppDefinition app)
    {
        List<string> paths = [];
        if (app.RegistryNames.Length == 0) return paths;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display || !app.RegistryNames.Contains(display, StringComparer.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("InstallLocation") is string directory && Path.IsPathFullyQualified(directory))
                        paths.AddRange(app.ExecutableNames.Select(exe => Path.Combine(directory, exe)));
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return paths;
    }

    public static Process StartSetup(string path) => Process.Start(new ProcessStartInfo(path)
    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! }) ?? throw new IOException("Windows did not start the installer.");

    public static void Launch(AppDefinition app, Installation installation)
    {
        if (!File.Exists(installation.Path)) throw new FileNotFoundException("The application has moved or was removed. Select Check for updates to refresh its location.");
        if (app.IsJob)
        {
            var script = Path.Combine(Path.GetDirectoryName(installation.Path)!, "Launch-NDIJobConfigurator.ps1");
            if (File.Exists(script))
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! };
                foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script }) info.ArgumentList.Add(arg);
                Process.Start(info)?.Dispose();
                return;
            }
        }
        Process.Start(new ProcessStartInfo(installation.Path)
        { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installation.Path)! })?.Dispose();
        if (app.IsJob) OpenUrl("http://localhost:8091");
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
