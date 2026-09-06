using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed record Installation(string Path, string? Version, bool IsSavedSetup = false);

public sealed class InstallationService(LocalState state)
{
    public Installation? Find(AppDefinition app)
    {
        List<string> paths = [];
        if (state.Preferences.LaunchPaths.TryGetValue(app.Id, out var custom)) paths.Add(custom);
        if (app.IsEnvironment && !File.Exists(custom) && state.Preferences.Setups.TryGetValue(app.Id, out var setup) && File.Exists(setup.Path))
        {
            try
            {
                using var file = File.OpenRead(setup.Path);
                var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(file));
                if (string.Equals(digest, setup.Digest, StringComparison.OrdinalIgnoreCase)) return new(setup.Path, setup.Tag, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        paths.AddRange(app.KnownPaths.Select(Environment.ExpandEnvironmentVariables));
        paths.AddRange(RegistryPaths(app));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(path)) return new(path, ReadVersion(path));
        return null;
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
        if (!File.Exists(installation.Path)) throw new FileNotFoundException("The application has moved or was removed. Use Locate app to choose its executable.");
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
