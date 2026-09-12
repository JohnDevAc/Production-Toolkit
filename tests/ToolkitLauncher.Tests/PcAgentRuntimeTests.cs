using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static void PcAgentRuntimeFlowTests()
    {
        var folder = Path.Combine(Temporary, "agent-runtime");
        var otherFolder = Path.Combine(folder, "unrelated");
        Directory.CreateDirectory(otherFolder);
        var executable = Path.Combine(folder, "NDI Configurator PC Agent.exe");
        var otherExecutable = Path.Combine(otherFolder, Path.GetFileName(executable));
        var marker = Path.Combine(folder, "starts.txt");
        var stop = Path.Combine(folder, "stop");
        var source = Path.Combine(folder, "fixture.cs");
        File.WriteAllText(source, """
            using System;
            using System.IO;
            using System.Threading;
            [assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
            [assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]
            class Fixture {
                static void Main() {
                    string folder = AppDomain.CurrentDomain.BaseDirectory;
                    File.AppendAllText(Path.Combine(folder, "starts.txt"), "started\n");
                    while (!File.Exists(Path.Combine(folder, "stop"))) Thread.Sleep(30);
                }
            }
            """);
        var compiler = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/nologo", "/target:winexe", "/out:" + executable, source }) compiler.ArgumentList.Add(argument);
        using (var build = Process.Start(compiler)!)
        {
            Check(build.WaitForExit(15000) && build.ExitCode == 0, "Harmless PC Agent runtime fixture compiles");
        }
        File.Copy(executable, Path.Combine(folder, "NDI Configurator PC Agent Setup.exe"));
        File.Copy(executable, otherExecutable);
        var stateBefore = File.Exists(AgentStatePath) ? File.ReadAllText(AgentStatePath) : null;
        File.WriteAllText(AgentStatePath, ValidAgentState);
        var definition = Catalog.Apps[3] with { KnownPaths = [executable], RegistryNames = [] };
        var window = new MainWindow(Path.Combine(folder, "toolkit"), true);
        window.Cards.Clear();
        var card = new AppCard(definition)
        {
            Installed = InstallationService.Find(definition, null, null),
            Snapshot = new([Release("v1.0.0")], DateTimeOffset.UtcNow), Offline = true
        };
        window.Cards.Add(card);
        var releases = card.Snapshot;
        var launch = typeof(MainWindow).GetMethod("LaunchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        static void Complete(Task task)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        static Process Start(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true })!;
        static int CountStarts(string path)
        {
            try { return File.ReadAllLines(path).Length; }
            catch (IOException) { return -1; } // The owned child may still hold its startup marker open.
        }
        using var other = Start(otherExecutable);
        Process? external = null;
        try
        {
            Check(SpinWait.SpinUntil(() => CountStarts(Path.Combine(otherFolder, "starts.txt")) == 1, 5000), "Unrelated same-name process starts");
            Complete(window.RefreshPcAgentRuntimeAsync());
            Check(card.PcAgentRuntimeText == "Not running" && card.CanLaunch,
                "A same-name executable in another folder does not disable PC Agent Launch");
            external = Start(executable);
            Check(SpinWait.SpinUntil(() => CountStarts(marker) == 1, 5000), "External PC Agent fixture starts");
            Complete(window.RefreshPcAgentRuntimeAsync());
            Check(card.PcAgentRuntimeText == "Running" && !card.CanLaunch,
                "Local runtime refresh detects an externally started agent and disables Launch");
            card.Installed = card.Installed! with { PcAgentRunning = false };
            Complete((Task)launch.Invoke(window, [card])!);
            Check(card.PcAgentRunning && !card.CanLaunch && CountStarts(marker) == 1,
                "Launch rechecks stale runtime state and does not start a duplicate agent");
            using var security = OpenFixtureProcess(0x40000, false, external.Id); // WRITE_DAC on this owned child only
            Check(!security.IsInvalid, "Owned runtime fixture security handle opens");
            using var debugPrivilege = new FixtureDebugPrivilegeScope();
            static void DenyQuery(SafeProcessHandle handle, string mask)
            {
                if (!ConvertFixtureDescriptor("D:(D;;" + mask + ";;;WD)(A;;GA;;;WD)", 1, out var descriptor, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (!SetFixtureSecurity(handle, 4, descriptor)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { FreeFixtureDescriptor(descriptor); }
            }
            DenyQuery(security, "0x410"); // Deny QUERY_INFORMATION and VM_READ, permit limited identity query.
            using (var denied = OpenFixtureProcess(0x410, false, external.Id))
                Check(denied.IsInvalid && Marshal.GetLastWin32Error() == 5, "Fixture denies the stronger MainModule process rights");
            Complete(window.RefreshPcAgentRuntimeAsync());
            Check(card.PcAgentRunning && !card.CanLaunch, "Limited-query identity still detects the restricted running agent");
            card.Installed = card.Installed! with { PcAgentRunning = false };
            Complete((Task)launch.Invoke(window, [card])!);
            Check(card.PcAgentRunning && !card.CanLaunch && CountStarts(marker) == 1,
                "Click-time limited-query recheck prevents an extra launch under denied MainModule access");
            DenyQuery(security, "0x1410"); // Also deny limited queries to exercise explicitly unknown state.
            Complete(window.RefreshPcAgentRuntimeAsync());
            Check(card.Installed!.PcAgentRunning is null && card.PcAgentRuntimeText == "Running status unavailable" && !card.CanLaunch,
                "Unresolved process identity is explicit and disables Launch");
            card.Installed = card.Installed with { PcAgentRunning = false };
            Complete((Task)launch.Invoke(window, [card])!);
            Check(card.Installed!.PcAgentRunning is null && !card.CanLaunch && CountStarts(marker) == 1,
                "Click-time unknown runtime state also blocks a stale Launch action");
            File.WriteAllText(stop, "stop");
            Check(external.WaitForExit(5000), "External agent fixture exits");
            Complete(window.RefreshPcAgentRuntimeAsync());
            Check(card.PcAgentRuntimeText == "Not running" && card.CanLaunch,
                "Agent exit restores Launch without a release check");
            File.Delete(stop);
            Complete((Task)launch.Invoke(window, [card])!);
            Check(SpinWait.SpinUntil(() => CountStarts(marker) == 2, 5000)
                && card.PcAgentRunning && !card.CanLaunch,
                "Toolkit launch immediately refreshes the agent's running state");
            Check(ReferenceEquals(releases, card.Snapshot) && card.Offline,
                "Runtime checks preserve release data and its original check state");
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            File.WriteAllText(Path.Combine(otherFolder, "stop"), "stop");
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && (path == executable || path == otherExecutable)
                        && !process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(5000); }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (external is not null)
            {
                if (!external.WaitForExit(5000)) { external.Kill(); external.WaitForExit(5000); }
                external.Dispose();
            }
            window.Close();
            if (stateBefore is null) File.Delete(AgentStatePath); else File.WriteAllText(AgentStatePath, stateBefore);
        }
    }

    private sealed class FixtureDebugPrivilegeScope : IDisposable
    {
        private readonly SafeAccessTokenHandle token;
        private TokenPrivilege previous;

        public FixtureDebugPrivilegeScope()
        {
            // Hosted runners can enable SeDebugPrivilege and bypass the child's
            // DACL. Limit only this test process while exercising denied queries.
            using var current = Process.GetCurrentProcess();
            if (!OpenFixtureToken(current.Handle, 0x28, out token)) // TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupFixturePrivilege(null, "SeDebugPrivilege", out var id))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var disabled = new TokenPrivilege { Count = 1, Id = id };
                if (!AdjustFixturePrivilege(token, false, ref disabled, Marshal.SizeOf<TokenPrivilege>(), out previous, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                if (error is not (0 or 1300)) // ERROR_NOT_ALL_ASSIGNED: token did not have this privilege.
                    throw new System.ComponentModel.Win32Exception(error);
                Console.WriteLine(previous.Count > 0 ? "Fixture disabled inherited debug privilege." : "Fixture debug privilege already disabled or absent.");
            }
            catch { token.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (previous.Count > 0 && (!AdjustFixturePrivilege(token, false, ref previous, Marshal.SizeOf<TokenPrivilege>(), out _, out _)
                    || Marshal.GetLastWin32Error() != 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { token.Dispose(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeId { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege { public uint Count; public PrivilegeId Id; public uint Attributes; }
    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    private static extern bool OpenFixtureToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupFixturePrivilege(string? system, string name, out PrivilegeId id);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    private static extern bool AdjustFixturePrivilege(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivilege requested,
        int length, out TokenPrivilege previous, out int returned);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenFixtureProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertFixtureDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", EntryPoint = "SetKernelObjectSecurity", SetLastError = true)]
    private static extern bool SetFixtureSecurity(SafeProcessHandle process, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr FreeFixtureDescriptor(IntPtr descriptor);
}
