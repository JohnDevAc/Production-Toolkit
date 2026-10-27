using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Serialization;

namespace ToolkitLauncher.Core;

public static class OwnedPaths
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.Strict };

    public static void CreatePrivateDirectory(string path)
    {
        CheckNoReparse(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current user has no Windows SID.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        ValidatePrivateDirectory(path);
    }

    public static void ValidatePrivateDirectory(string path)
    {
        CheckNoReparse(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current user has no Windows SID.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Operation directory belongs to another account.");
        var trusted = new[] { owner.Value, "S-1-5-18", "S-1-5-32-544" };
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("Operation directory allows writes by another account.");
    }

    public static string Resolve(string root, string relative, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("The owned path must be a relative file or directory.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves its owning directory.");
        CheckNoReparse(path);
        if (mustExist && !File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("The product's maintenance file is missing.", path);
        return path;
    }

    public static void CheckNoReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 && !IsVerifiedCloudPlaceholder(current))
                    throw new InvalidDataException("A linked or redirected path cannot be used for lifecycle operations: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au && (tag & 0x20000000u) == 0;
    private static bool IsVerifiedCloudPlaceholder(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8) || !IsCloudTag(info.Tag)) return false;
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) return false;
        var resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return Path.GetFullPath(resolved).TrimEnd('\\').Equals(Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    public static T Read<T>(string path)
    {
        CheckNoReparse(path);
        // Producers atomically replace these snapshots while Toolkit polls them. Permit
        // replacement/deletion, but not in-place writes to the snapshot being parsed.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Lifecycle metadata exceeds its size limit.");
        using var reader = new StreamReader(file);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), Json) ?? throw new InvalidDataException("Lifecycle metadata is empty.");
    }

    public static void Write<T>(string path, T value)
    {
        CheckNoReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void CheckTree(string directory)
    {
        CheckNoReparse(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckNoReparse(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
}
