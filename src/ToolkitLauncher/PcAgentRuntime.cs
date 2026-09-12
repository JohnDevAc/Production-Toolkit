using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ToolkitLauncher;

public static class PcAgentRuntime
{
    // Match the detected executable in this desktop session. Setup and agents in
    // other sessions must not prevent this user from launching their tray agent.
    public static bool? Read(string executable)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var session = current.SessionId;
            var running = false;
            var unknown = false;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    // MainModule needs VM_READ and QUERY_INFORMATION. Limited identity
                    // queries also work for agents that deny those stronger permissions.
                    using var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
                    if (handle.IsInvalid)
                    {
                        if (Marshal.GetLastWin32Error() != 87) unknown = true; // ERROR_INVALID_PARAMETER: exited PID
                        continue;
                    }
                    if (GetExitCodeProcess(handle, out var exitCode) && exitCode != 259) continue; // STILL_ACTIVE
                    var path = new StringBuilder(32768);
                    var size = path.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, path, ref size))
                    {
                        if (!GetExitCodeProcess(handle, out exitCode) || exitCode == 259) unknown = true;
                    }
                    else if (string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                        running = true;
                }
                catch (InvalidOperationException) { } // The process exited during inspection.
                catch (Win32Exception) { unknown = true; }
            }
            return running ? true : unknown ? null : false;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
