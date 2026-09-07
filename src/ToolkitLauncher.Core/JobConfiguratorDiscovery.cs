using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ToolkitLauncher.Core;

public sealed record NetworkConfigurator(Uri Address)
{
    public string LinkText => $"Open web UI · {Address.Host}";
}

public sealed record JobDiscoveryResult(IReadOnlyList<NetworkConfigurator> Instances, bool TimedOut, int Checked, int Total, int UnsupportedNetworks);

public sealed class JobConfiguratorDiscovery(HttpClient http,
    Func<IEnumerable<(IPAddress Address, int Prefix)>>? networks = null, TimeSpan? scanBudget = null)
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<JobDiscoveryResult> DiscoverAsync(Action<NetworkConfigurator>? found = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var adapters = (networks ?? ActiveNetworks)().Distinct().ToArray();
        var unsupported = adapters.Count(n => n.Prefix is < 20 or > 30);
        var supported = adapters.Where(n => n.Prefix is >= 20 and <= 30).ToArray();
        var local = adapters.Select(n => n.Address).ToHashSet();
        // Try the nearby /24 on every interface first, then finish every supported subnet.
        var candidates = supported.SelectMany(n => SubnetHosts(n.Address, Math.Max(24, n.Prefix)))
            .Concat(supported.SelectMany(n => SubnetHosts(n.Address, n.Prefix)))
            .Where(address => !local.Contains(address)).Distinct().ToArray();
        var instances = new ConcurrentDictionary<string, NetworkConfigurator>();
        var completed = 0;
        var timedOut = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(scanBudget ?? TimeSpan.FromMinutes(4));
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = deadline.Token }, async (address, token) =>
            {
                var instance = await ProbeAsync(new Uri($"http://{address}:8091/"), token).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
                if (instance is not null && instances.TryAdd(instance.Address.AbsoluteUri, instance)) found?.Invoke(instance);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
        ct.ThrowIfCancellationRequested();
        return new(instances.Values.OrderBy(i => i.Address.Host, StringComparer.Ordinal).ToArray(), timedOut, completed, candidates.Length, unsupported);
    }

    public async Task<NetworkConfigurator?> ProbeAsync(Uri address, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(850));
        try
        {
            using var response = await http.GetAsync(new Uri(address, "api/health"), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            // A port listener is insufficient; require this application's public identity.
            await response.Content.LoadIntoBufferAsync(16 * 1024).WaitAsync(deadline.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String ||
                product.GetString() is not ("NDI Job Configurator" or "Kiloview Job Configurator")) return null;
            return new(address);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or JsonException or IOException) { return null; }
    }

    public static IEnumerable<IPAddress> SubnetHosts(IPAddress address, int prefix)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefix is < 20 or > 30)
            throw new ArgumentOutOfRangeException(nameof(prefix), "Automatic discovery supports IPv4 /20 through /30.");
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var broadcast = network | ~mask;
        for (var host = network + 1; host < broadcast; host++)
            yield return new IPAddress(new[] { (byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host });
    }

    private static IEnumerable<(IPAddress Address, int Prefix)> ActiveNetworks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(n => n.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(n.Address)
            && n.Address.GetAddressBytes() is var b && b[0] is > 0 and < 224 && !(b[0] == 169 && b[1] == 254))
        .Select(n => (n.Address, n.PrefixLength));
}
