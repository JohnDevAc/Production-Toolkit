using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed record RegisteredUninstaller(string File, string[] Arguments, string Directory)
{
    public ProcessStartInfo StartInfo()
    {
        OwnedPaths.CheckNoReparse(File);
        if (!System.IO.File.Exists(File)) throw new FileNotFoundException("The registered uninstaller was removed.", File);
        var info = new ProcessStartInfo(File) { UseShellExecute = true, WorkingDirectory = Directory };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public static RegisteredUninstaller? Parse(string? command, string directory)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var args = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray();
            if (args.Length == 0 || !Path.IsPathFullyQualified(args[0]) || !Path.GetExtension(args[0]).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var relative = Path.GetRelativePath(directory, args[0]);
            // Only the registered native uninstaller within this product's exact install directory.
            // Shell/script/MSI command lines need their owner's advertised adapter or Windows Apps UI.
            var file = OwnedPaths.Resolve(directory, relative);
            return new(file, args.Skip(1).ToArray(), Path.GetFullPath(directory));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return null; }
        finally { LocalFree(pointer); }
    }

    public static RegisteredUninstaller? Find(AppDefinition app, string installedDirectory)
    {
        if (app.RegistryNames.Length == 0) return null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = key.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(name);
                    if (product?.GetValue("DisplayName") is not string title || !app.RegistryNames.Contains(title, StringComparer.OrdinalIgnoreCase) ||
                        product.GetValue("InstallLocation") is not string directory || !Path.IsPathFullyQualified(directory) ||
                        !Path.GetFullPath(directory).TrimEnd('\\').Equals(Path.GetFullPath(installedDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    var result = Parse(product.GetValue("UninstallString") as string, directory);
                    if (result is not null) return result;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
