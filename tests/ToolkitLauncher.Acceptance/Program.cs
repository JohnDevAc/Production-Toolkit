using System.IO;

namespace ToolkitLauncher.Acceptance;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" })) return HarnessTests.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--check-wpf-startup") return WpfStartupCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--validate-manifest")
            {
                var path = Path.GetFullPath(args[1]);
                var manifest = CandidateManifest.Read(path);
                manifest.VerifyFilesAsync(Path.GetDirectoryName(path)!).GetAwaiter().GetResult();
                ProductionHost.CheckSeams();
                Console.WriteLine("PASS: pinned manifest, all four exact package hashes/product descriptors and production source stamp. No products executed.");
                return 0;
            }
            if (args.Length != 5 || args[0] != "--manifest" || args[2] != "--disposable-vm" || args[4] != "--allow-real-os-operations")
                throw new ArgumentException("Usage: Toolkit.QA.exe --manifest <acceptance.json> --disposable-vm <exact computer name> --allow-real-os-operations");
            var guard = VmGuard.Read();
            VmGuard.Validate(guard, args[3], true); // Must run before state creation, feed setup or window construction.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KILOVIEW_AGENT_STATE_PATH")))
                throw new InvalidOperationException("Unset KILOVIEW_AGENT_STATE_PATH for real VM acceptance.");
            var manifestPath = Path.GetFullPath(args[1]);
            var candidates = CandidateManifest.Read(manifestPath);
            candidates.VerifyFilesAsync(Path.GetDirectoryName(manifestPath)!).GetAwaiter().GetResult();
            return ProductionHost.Run(candidates, manifestPath, guard);
        }
        catch (Exception error) { Console.Error.WriteLine("QA launch refused/failed: " + error); return 2; }
    }
}
