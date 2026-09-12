using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void PreferenceRecoveryTests()
    {
        foreach (var broken in new[] { "Channels", "LaunchPaths", "Setups", "all", "root" })
        {
            var folder = Path.Combine(Temporary, "preferences-" + broken);
            Directory.CreateDirectory(folder);
            var original = new Preferences();
            original.Channels["job"] = ReleaseChannel.Development;
            original.LaunchPaths["job"] = Path.Combine(folder, "custom.exe");
            original.Setups["environment"] = new("setup.exe", "v2.1.6", "sha256:fixture");
            var json = JsonSerializer.SerializeToNode(original)!;
            foreach (var name in new[] { "Channels", "LaunchPaths", "Setups" })
                if (broken == name || broken == "all") json[name] = null;
            var contents = broken == "root" ? "null" : json.ToJsonString();
            var settings = Path.Combine(folder, "settings.json");
            File.WriteAllText(settings, contents);
            var recovered = new LocalState(folder);
            Check(recovered.Warning is not null && recovered.Preferences.Channels is not null &&
                recovered.Preferences.LaunchPaths is not null && recovered.Preferences.Setups is not null,
                $"Null {broken} preferences recover with a warning and usable collections");
            var backup = Directory.GetFiles(folder, "settings.json.*.bak").Single();
            Check(File.ReadAllText(backup) == contents && File.ReadAllText(settings) == contents,
                $"Null {broken} recovery preserves the exact original without rewriting it");
            recovered.Save();
            var reloaded = new LocalState(folder);
            Check(reloaded.Warning is null, $"Repaired {broken} preferences save and reload cleanly");
            Check(broken is "Channels" or "all" or "root" ? reloaded.Preferences.Channels.Count == 0 :
                reloaded.Preferences.Channels["job"] == ReleaseChannel.Development, $"Recovery preserves valid channel choices ({broken})");
            Check(broken is "LaunchPaths" or "all" or "root" ? reloaded.Preferences.LaunchPaths.Count == 0 :
                reloaded.Preferences.LaunchPaths["job"] == original.LaunchPaths["job"], $"Recovery preserves valid launch paths ({broken})");
            Check(broken is "Setups" or "all" or "root" ? reloaded.Preferences.Setups.Count == 0 :
                reloaded.Preferences.Setups["environment"] == original.Setups["environment"], $"Recovery preserves valid setup records ({broken})");
        }
    }

    private static void PreferenceRecoveryWindowTests()
    {
        var folder = Path.Combine(Temporary, "preferences-window");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{"Channels":null,"LaunchPaths":{},"Setups":{}}""");
        var window = new MainWindow(folder, true);
        try { Check(window.Cards.Count == 4 && window.Footer.Contains("preferences"), "Dashboard opens after semantic preference corruption and exposes recovery warning"); }
        finally { window.Close(); }
    }

    private static void CachedSetupVersionTests()
    {
        var folder = Path.Combine(Temporary, "cached-environment");
        Directory.CreateDirectory(folder);
        string Compile(string name, string version)
        {
            var executable = Path.Combine(folder, name + ".exe");
            var source = Path.ChangeExtension(executable, ".cs");
            File.WriteAllText(source, "[assembly:System.Reflection.AssemblyFileVersion(\"" + version + ".0\")] [assembly:System.Reflection.AssemblyInformationalVersion(\"" + version + "\")] class Fixture { static void Main() {} }");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/nologo", "/target:winexe", "/out:" + executable, source }) info.ArgumentList.Add(arg);
            using var compiler = Process.Start(info)!;
            Check(compiler.WaitForExit(15000) && compiler.ExitCode == 0, "Versioned Environment fixture compiles: " + version);
            return executable;
        }
        var persistent = Compile("persistent", "2.1.5");
        var download = Compile("download", "2.1.6");
        var setup = new SetupRecord(download, "v2.1.6", "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(download))));
        var definition = Catalog.Apps[0] with { KnownPaths = [persistent], RegistryNames = [] };
        var release = Release("v2.1.6");
        release.Assets = [new() { Name = "Kiloview-Environment-Setup.exe", Size = new FileInfo(download).Length }];
        var card = new AppCard(definition) { Installed = InstallationService.Find(definition, null, setup),
            Snapshot = new([release], DateTimeOffset.UtcNow), EnvironmentStatus = EnvironmentSnapshot.From(FullEnvironment(), DateTimeOffset.UtcNow) };
        Check(card.InstalledText == "2.1.5" && card.Installed!.Path == persistent && !card.Installed.IsSavedSetup,
            "A newer downloaded Environment setup does not become the installed version after cancellation");
        Check(card.Status == "Needs updating" && card.CanInstall && card.ShowPrimary && card.InstallText == "Update",
            "The unapplied Environment update remains visible and enabled");
        Check(InstallationService.Find(definition, persistent, setup)?.Version == "2.1.5", "Explicit installed launcher paths also remain authoritative");
        File.Copy(download, persistent, true);
        card.Installed = InstallationService.Find(definition, null, setup);
        Check(card.InstalledText == "2.1.6" && card.Status == "Up to date" && !card.CanInstall && !card.ShowPrimary && card.CanLaunch,
            "An actual installed update hides Update and keeps Open setup available");
        File.Delete(persistent);
        card.Installed = InstallationService.Find(definition, null, setup);
        Check(card.Installed is { IsSavedSetup: true, Version: null } && !card.HasInstallation && card.InstalledText == "Not detected" && card.State != UpdateState.Current,
            "A verified cached setup without a persistent launcher never claims an installed version");
        Check(card.ExistingSetupPath == download && card.CanInstall && card.ShowPrimary,
            "The selected verified setup can still be reused without a persistent launcher");
        card.Snapshot = null;
        Check(card.ExistingSetupPath == download && card.CanInstall && card.CanLaunch, "Verified cached setup remains usable offline without release metadata");
        var newer = Release("v2.1.7"); newer.Assets = release.Assets;
        card.Snapshot = new([newer], DateTimeOffset.UtcNow);
        Check(card.ExistingSetupPath is null && card.CanInstall, "A cached older setup does not substitute for the selected newer release");
        File.AppendAllText(download, "tampered");
        Check(InstallationService.Find(definition, null, setup) is null, "A tampered cached setup is not offered as a fallback");
    }
}
