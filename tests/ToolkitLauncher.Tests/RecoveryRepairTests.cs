using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void RecoveryRepairTests()
    {
        var root = Path.Combine(Temporary, "complete-repair"); Directory.CreateDirectory(root);
        var native = Path.Combine(root, "retained.exe"); var source = Path.Combine(root, "retained.cs");
        File.WriteAllText(source, """
            using System; using System.IO; using System.Collections.Generic; using System.Web.Script.Serialization;
            [assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
            [assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]
            class Fixture { static int Main(string[] args) {
                var json = new JavaScriptSerializer();
                var request = json.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[1]));
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"retained-repair.json"), json.Serialize(request));
                File.WriteAllText((string)request["resultPath"], json.Serialize(new {schemaVersion=1,operationId=request["operationId"],
                    productId=request["productId"],action=request["action"],outcome="succeeded",updatedUtc=DateTimeOffset.UtcNow.ToString("o"),
                    message="Retained fixture repair",recoveryRequired=false})); return 0;
            } }
            """);
        var compiler = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/nologo", "/target:winexe", "/r:System.Web.Extensions.dll", "/out:" + native, source }) compiler.ArgumentList.Add(arg);
        using (var build = Process.Start(compiler)!) Check(build.WaitForExit(15000) && build.ExitCode == 0, "Versioned harmless retained repair fixture compiles");

        foreach (var scenario in new[] { "recovery", "missing-package", "downgrade", "missing-digest", "corrupt-cache", "unknown-version", "legacy-package", "unverified-inspection", "ordinary", "recheck-restart" })
        {
            var directory = Path.Combine(root, scenario); var maintenance = Path.Combine(directory, "Maintenance"); Directory.CreateDirectory(maintenance);
            var retained = Path.Combine(maintenance, "Repair.exe"); File.Copy(native, retained);
            if (scenario == "unknown-version") File.WriteAllText(retained, "not a versioned executable");
            var requiresRecovery = scenario != "ordinary";
            var inspection = EmitSuccess.Replace("message='Fixture success'", "message='Owner inspected';recoveryRequired=" + (requiresRecovery ? "$true" : "$false") + ";components=@(@{productId='pc-agent';installed=$false;version=$null;setupVersion=$null;recoveryRequired=" + (requiresRecovery ? "$true" : "$false") + "})");
            if (scenario == "unverified-inspection") inspection = inspection.Replace("operationId=$r.operationId", "operationId='00000000-0000-0000-0000-000000000001'");
            FixtureAdapter(directory, "pc-agent", inspection + "\nAdd-Content -LiteralPath " + PsQuote(Path.Combine(directory, "inspect-actions.txt")) + " -Value $r.action");
            var descriptorPath = Path.Combine(directory, "toolbox-lifecycle.json");
            var descriptor = OwnedPaths.Read<LifecycleCapabilities>(descriptorPath);
            descriptor.ActionEntryPoints["repair"] = "Maintenance/Repair.exe";
            OwnedPaths.Write(descriptorPath, descriptor);
            var registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ToolkitRecoveryFixture-" + Guid.NewGuid().ToString("N");
            var displayName = "Toolkit Recovery Fixture " + Guid.NewGuid().ToString("N");
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
            { key.SetValue("DisplayName", displayName); key.SetValue("InstallLocation", directory); key.SetValue("UninstallString", "\"" + retained + "\" --uninstall"); }
            var definition = Catalog.Apps.Single(a => a.IsPcAgent) with
            { KnownPaths = [Path.Combine(directory, "NDI Configurator PC Agent.exe")], RegistryNames = [displayName], MaintenanceDirectories = [] };
            var stateRoot = Path.Combine(directory, "toolkit");
            MainWindow? window = null;
            using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Recovery fixture refuses network")));
            var newCapture = Path.Combine(directory, "complete-repair.json");
            var newSource = Path.Combine(directory, "package-source");
            FixtureAdapter(newSource, "pc-agent", EmitSuccess.Replace("message='Fixture success'", "message='Complete package repair';recoveryRequired=$false")
                + "\nCopy-Item -LiteralPath $LifecycleRequestPath -Destination " + PsQuote(newCapture));
            if (scenario == "legacy-package")
            {
                File.Delete(Path.Combine(newSource, "toolbox-lifecycle.json"));
                File.Copy(native, Path.Combine(newSource, "NDI Configurator PC Agent Setup.exe"));
            }
            var zipPath = Path.Combine(directory, "package.zip"); ZipFile.CreateFromDirectory(newSource, zipPath);
            var bytes = File.ReadAllBytes(zipPath);
            var release = Release(scenario == "downgrade" ? "v0.9.0" : "v1.0.0");
            var asset = new ReleaseAsset { Name = "NDI-Configurator-PC-Agent-win-x64.zip", Size = bytes.Length,
                Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)),
                DownloadUrl = definition.RepositoryUrl + "/releases/download/" + release.Tag + "/NDI-Configurator-PC-Agent-win-x64.zip" };
            release.Assets = [asset];
            var cached = Path.Combine(stateRoot, "Downloads", definition.Id, asset.Digest[7..].ToLowerInvariant(), asset.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.WriteAllBytes(cached, scenario == "corrupt-cache" ? [1, 2, 3] : bytes);
            if (scenario == "missing-digest") asset.Digest = null;
            AppCard Open()
            {
                window = new MainWindow(stateRoot, true); window.Cards.Clear();
                var card = new AppCard(definition) { Installed = InstallationService.Find(definition, null, null),
                    Snapshot = scenario is "missing-package" or "ordinary" ? null : new([release], DateTimeOffset.UtcNow), Offline = true };
                window.Cards.Add(card);
                typeof(MainWindow).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new PackageService(http, Path.Combine(stateRoot, "Downloads")));
                typeof(MainWindow).GetMethod("UpdateOperationCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
                return card;
            }
            void Invoke(AppCard card, string action) => Pump((Task)typeof(MainWindow).GetMethod("MaintainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card, action, true])!);
            try
            {
                var card = Open();
                Check(card.Installed is { IsPresent: false, Capabilities: not null, Uninstaller: not null } && card.CanRecheck,
                    scenario + ": registration does not hide the retained inspection descriptor after executable removal");
                if (scenario == "recheck-restart")
                {
                    Invoke(card, "inspect");
                    Check(card.RequiresCompleteRepair && card.RecoveryVersion == "1.0.0", "Fresh-state Recheck reads owner recovery and validates retained native version without inventing an installed version");
                    window!.Close(); card = Open();
                    Check(card.RequiresCompleteRepair && card.Installed!.Version is null, "Owner recovery survives Toolkit restart while installed version remains unknown");
                    Check(card.Status == "Repair required" && card.InstallText == "Repair" && card.ShowPrimary && card.CanInstall && !card.ShowCompact &&
                        card.InlineReason == "Repair will verify and use the selected complete package." && card.ShortReason == "",
                        "Available recovery package gives a Repair primary action and accurate Details without duplicate default guidance");
                }
                if (scenario == "recheck-restart")
                    Pump((Task)typeof(MainWindow).GetMethod("PrepareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [card])!);
                else Invoke(card, "repair");
                var coordinator = (LifecycleCoordinator)typeof(MainWindow).GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var oldCapture = Path.Combine(maintenance, "retained-repair.json");
                if (scenario is "recovery" or "recheck-restart")
                {
                    var request = OwnedPaths.Read<LifecycleRequest>(newCapture);
                    Check(request.Action == "repair" && request.PreserveData && !File.Exists(oldCapture) &&
                        coordinator.Operations.Last().CapabilityRoot != directory && coordinator.Operations.Last().Result?.IsSuccess == true,
                        scenario + ": verified complete selected package performs repair with preserved data instead of retained old bytes");
                    Check(!card.RequiresCompleteRepair, scenario + ": successful owner result clears recovery requirement");
                }
                else if (scenario == "unverified-inspection")
                    Check(!File.Exists(oldCapture) && !File.Exists(newCapture) && coordinator.Operations.All(o => o.Request.Action == "inspect") &&
                        card.Activity.Contains("verified owner inspection"), "Unverified owner inspection cannot authorize either retained or package repair");
                else if (scenario == "ordinary")
                    Check(File.Exists(oldCapture) && !File.Exists(newCapture) && coordinator.Operations.Last().CapabilityRoot == directory,
                        "Ordinary healthy repair still uses retained maintenance without release/network availability");
                else
                    Check(!File.Exists(oldCapture) && !File.Exists(newCapture) && coordinator.Operations.All(o => o.Request.Action == "inspect") &&
                        card.RequiresCompleteRepair && card.Activity == AppCard.RepairPackageNeeded,
                        scenario + ": unusable complete package stops before mutation with actionable status and no retained fallback");
            }
            finally { window?.Close(); Registry.CurrentUser.DeleteSubKeyTree(registryPath); }
        }
    }
}
