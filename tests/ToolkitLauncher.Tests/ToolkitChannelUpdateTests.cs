using System.Diagnostics;
using System.IO;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static async Task ToolkitChannelUpdateTestsAsync()
    {
        static Release Update(string version, bool prerelease)
        {
            var release = Release("v" + version, prerelease);
            release.Assets = [new()
            {
                Name = $"Production-Toolkit-{version}-win-x64-Setup.exe", Size = 100,
                Digest = "sha256:" + new string('a', 64),
                DownloadUrl = $"https://github.com/JohnDevAc/Production-Toolkit/releases/download/v{version}/Production-Toolkit-{version}-win-x64-Setup.exe"
            }];
            return release;
        }
        var stable = Update("1.4.0", false);
        var dev = Update("1.4.0-dev.2", true);
        Check(ToolkitUpdates.Available("1.3.8", [dev, stable])?.Release == stable, "Stable Toolkit remains on stable even when dev was published later");
        Check(ToolkitUpdates.Available("1.4.0-dev.1+build", [stable, dev])?.Release == dev, "Development Toolkit selects the next prerelease without jumping to stable");
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [stable]) is null, "An unavailable development update never falls back to stable");
        Check(ToolkitUpdates.Available("1.3.8", [dev]) is null, "An unavailable stable update never falls back to development");
        Check(ToolkitUpdates.Available("1.4.0-dev.2+different-build", [dev]) is null, "Identical dev versions ignore build metadata");
        Check(ToolkitUpdates.Available("1.4.0-dev.3", [dev]) is null, "Development updates never downgrade");
        var dev10 = Update("1.4.0-dev.10", true); dev10.PublishedAt = dev.PublishedAt.AddDays(-1);
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [dev, dev10])?.Release == dev10, "Numeric prerelease identifiers determine newest version independently of publication time");
        var draft = Update("2.0.0-dev.1", true); draft.Draft = true;
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [draft, dev])?.Release == dev, "Draft development releases remain excluded");
        var mislabeled = Update("2.0.0", true);
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [mislabeled]) is null, "A prerelease flag does not turn a stable semantic version into a dev binary");
        Check(ToolkitUpdates.Available("1.4.0", [mislabeled]) is null, "Stable excludes stable tags marked as prereleases");
        var legacy = Update("1.4.0-dev.3", false);
        Check(ToolkitUpdates.Available("1.3.8", [legacy]) is null, "A legacy dev tag mislabeled stable cannot cross into stable");
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [legacy])?.Release == legacy, "Development preserves recognition of a legacy prerelease tag");
        var wrongName = Update("1.4.0-dev.4", true); wrongName.Assets[0].Name = "Production-Toolkit-1.4.0-win-x64-Setup.exe";
        Check(ToolkitUpdates.Available("1.4.0-dev.1", [wrongName]) is null, "A stable-named installer cannot satisfy a dev release tag");
        Check(ReleaseSelection.Latest([stable, dev], ReleaseChannel.Stable) == stable &&
            ReleaseSelection.Latest([stable, dev], ReleaseChannel.Development) == dev, "Managed-product channel selection stays independent");

        var root = Path.Combine(Temporary, "updater-versions"); Directory.CreateDirectory(root);
        string Binary(string name, string fileVersion, string productVersion)
        {
            var path = Path.Combine(root, name + ".exe"); var source = Path.ChangeExtension(path, ".cs");
            File.WriteAllText(source, "[assembly: System.Reflection.AssemblyFileVersion(\"" + fileVersion + "\")] " +
                "[assembly: System.Reflection.AssemblyInformationalVersion(\"" + productVersion + "\")] class Fixture { static void Main() {} }");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + path, source }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Check(process.WaitForExit(15000) && process.ExitCode == 0, "Harmless updater metadata fixture compiles: " + name);
            return path;
        }
        var stableBinary = Binary("stable", "1.4.0.0", "1.4.0+stable-source");
        var devBinary = Binary("dev", "1.4.0.0", "1.4.0-dev.2+dev-source");
        var wrongNumeric = Binary("wrong-numeric", "1.3.9.0", "1.4.0-dev.2");
        SelfUpdateService.ValidateInstallerVersion(stableBinary, "v1.4.0");
        Check(true, "Stable binary semantic and Windows numeric versions match its release tag");
        SelfUpdateService.ValidateInstallerVersion(devBinary, "v1.4.0-dev.2");
        Check(true, "Development semantic version remains distinct from its numeric Windows version");
        await ThrowsAsync<InvalidDataException>(() => { SelfUpdateService.ValidateInstallerVersion(stableBinary, "v1.4.0-dev.2"); return Task.CompletedTask; }, "Stable binary cannot satisfy a dev tag with the same numeric version");
        await ThrowsAsync<InvalidDataException>(() => { SelfUpdateService.ValidateInstallerVersion(devBinary, "v1.4.0"); return Task.CompletedTask; }, "Dev binary cannot satisfy a stable tag with the same numeric version");
        await ThrowsAsync<InvalidDataException>(() => { SelfUpdateService.ValidateInstallerVersion(devBinary, "v1.4.0-dev.3"); return Task.CompletedTask; }, "A different prerelease binary is rejected");
        await ThrowsAsync<InvalidDataException>(() => { SelfUpdateService.ValidateInstallerVersion(wrongNumeric, "v1.4.0-dev.2"); return Task.CompletedTask; }, "Matching semantic text cannot hide a wrong numeric Windows file version");
    }
}
