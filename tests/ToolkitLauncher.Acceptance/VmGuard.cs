using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ToolkitLauncher.Acceptance;

internal sealed record VmEvidence(string Computer, string Manufacturer, string Model, int SessionId,
    bool Interactive, bool DesktopVisible, bool Elevated);

internal static class VmGuard
{
    public static VmEvidence Read()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var identity = WindowsIdentity.GetCurrent();
        var flags = new UserObjectFlags();
        var visible = GetUserObjectInformation(GetProcessWindowStation(), 1, ref flags, Marshal.SizeOf<UserObjectFlags>(), out _) && (flags.Flags & 1) != 0;
        var desktop = new StringBuilder(256);
        visible &= GetUserObjectName(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, desktop.Capacity * 2, out _) && desktop.ToString() == "Default";
        return new(Environment.MachineName, bios?.GetValue("SystemManufacturer") as string ?? "", bios?.GetValue("SystemProductName") as string ?? "",
            Process.GetCurrentProcess().SessionId, Environment.UserInteractive, visible,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
    }

    public static void Validate(VmEvidence actual, string? expectedComputer, bool acknowledged)
    {
        if (actual.Computer.Equals("JOHN-PC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real operations are forbidden on JOHN-PC.");
        if (!acknowledged || string.IsNullOrWhiteSpace(expectedComputer) || !actual.Computer.Equals(expectedComputer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Supply --disposable-vm with this VM's exact computer name and --allow-real-os-operations.");
        var hardware = (actual.Manufacturer + " " + actual.Model).ToLowerInvariant();
        if (!new[] { "virtual machine", "vmware", "virtualbox", "qemu", "kvm", "parallels", "xen", "bochs" }.Any(hardware.Contains))
            throw new InvalidOperationException("Hardware is not recognized as a disposable virtual machine.");
        if (actual.SessionId == 0 || !actual.Interactive || !actual.DesktopVisible || actual.Elevated)
            throw new InvalidOperationException("Launch as the ordinary logged-on VM user on the interactive Default desktop, without Run as administrator.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct UserObjectFlags { public int Inherit; public int Reserved; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, ref UserObjectFlags value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectName(IntPtr handle, int index, StringBuilder value, int length, out int needed);
}
