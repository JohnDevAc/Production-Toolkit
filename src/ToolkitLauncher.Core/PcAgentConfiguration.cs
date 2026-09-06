using System.Net;
using System.Net.Sockets;

namespace ToolkitLauncher.Core;

public sealed record PcAgentConfiguration(int SchemaVersion, string? EndpointId, string? AdapterId, string? Address, int PrefixLength)
{
    public bool IsValid => SchemaVersion == 1
        && Guid.TryParse(EndpointId, out var endpoint) && endpoint != Guid.Empty
        && Guid.TryParse(AdapterId, out var adapter) && adapter != Guid.Empty
        && PrefixLength is >= 1 and <= 30 && IsUsableAddress(Address, PrefixLength);

    private static bool IsUsableAddress(string? value, int prefix)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254) return false;
        var number = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var hostMask = uint.MaxValue >> prefix;
        return (number & hostMask) != 0 && (number & hostMask) != hostMask;
    }
}
