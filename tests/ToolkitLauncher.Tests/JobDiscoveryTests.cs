using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using ToolkitLauncher;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static HttpResponseMessage Health(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private static async Task JobDiscoveryTestsAsync()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            lock (requests) requests.Add(request.RequestUri!);
            return request.RequestUri!.Host switch
            {
                "192.0.2.2" => Health("{\"product\":\"NDI Job Configurator\"}"),
                "192.0.3.2" => Health("{\"product\":\"Kiloview Job Configurator\"}"),
                "192.0.2.3" => Health("{\"product\":\"NDI Configurator PC Agent\"}"),
                "192.0.2.4" => Health("{\"product\":123}"),
                "192.0.2.5" => Health("<html>unrelated web server</html>"),
                "192.0.2.6" => Health(new string('x', 17000)),
                "192.0.2.7" => Health("{\"product\":\"NDI Job Configurator\"}", HttpStatusCode.Redirect),
                _ => Health("{}")
            };
        }));
        var discovery = new JobConfiguratorDiscovery(http, () => [(IPAddress.Parse("192.0.2.1"), 23), (IPAddress.Parse("192.0.2.1"), 23)]);
        var found = new System.Collections.Concurrent.ConcurrentBag<NetworkConfigurator>();
        var result = await discovery.DiscoverAsync(found.Add);
        Check(result.Instances.Count == 2 && found.Count == 2, "Discovery accepts current/legacy Job Configurator identity and rejects other listeners, malformed, oversized and redirect responses");
        Check(result.Total == 509 && result.Checked == 509 && requests.Select(r => r.Host).Distinct().Count() == 509,
            "Discovery covers the complete /23 once, including hosts beyond /24, and skips this PC");
        Check(requests.All(r => r.Scheme == "http" && r.Port == 8091 && r.AbsolutePath == "/api/health"), "Discovery sends only read-only health probes on the contracted port");
        Check(JobConfiguratorDiscovery.SubnetHosts(IPAddress.Parse("192.0.2.1"), 20).Count() == 4094
            && JobConfiguratorDiscovery.SubnetHosts(IPAddress.Parse("192.0.2.1"), 30).Count() == 2, "Supported subnet boundaries exclude network and broadcast addresses");
        var unsupported = await new JobConfiguratorDiscovery(http, () => [(IPAddress.Parse("10.0.0.1"), 16)]).DiscoverAsync();
        Check(unsupported.Total == 0 && unsupported.UnsupportedNetworks == 1, "Unsupported large networks are reported without truncating or scanning them");
        var empty = await new JobConfiguratorDiscovery(http, () => []).DiscoverAsync();
        Check(empty.Total == 0 && empty.Instances.Count == 0, "Disconnected hosts finish discovery cleanly");
        using var stalled = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "192.0.2.2") return Health("{\"product\":\"NDI Job Configurator\"}");
            await Task.Delay(Timeout.Infinite, token);
            return Health("{}");
        }));
        var bounded = new JobConfiguratorDiscovery(stalled, () => [(IPAddress.Parse("192.0.2.1"), 24)], TimeSpan.FromMilliseconds(150));
        var partial = await bounded.DiscoverAsync();
        Check(partial.TimedOut && partial.Checked < partial.Total && partial.Instances.Count == 1, "Timed-out scans retain discoveries and report partial coverage");
        using var cancel = new CancellationTokenSource(75);
        await ThrowsAsync<OperationCanceledException>(() => bounded.DiscoverAsync(ct: cancel.Token), "Caller cancellation stops outstanding probes");
        using var offline = new HttpClient(new AsyncHandler((_, _) => throw new HttpRequestException("Network unavailable")));
        var offlineResult = await new JobConfiguratorDiscovery(offline, () => [(IPAddress.Parse("192.0.2.1"), 30)]).DiscoverAsync();
        Check(offlineResult.Checked == 1 && offlineResult.Instances.Count == 0, "A failed peer does not fail the network scan");
    }

    private static void JobDiscoveryFlowTests()
    {
        var window = new MainWindow(Path.Combine(Temporary, "network-flow"), true);
        var card = window.Cards.Single(c => c.Definition.IsJob);
        using var http = new HttpClient(new FakeHandler(_ => Health("{\"product\":\"NDI Job Configurator\"}")));
        var discovery = new JobConfiguratorDiscovery(http, () => [(IPAddress.Parse("192.0.2.1"), 30)]);
        void Complete(Task task)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        try
        {
            Complete(window.RefreshJobDiscoveryAsync(card, discovery));
            Check(card.ShowCompact && card.ShowNetworkDiscovery && card.NetworkConfigurators.Count == 1 && card.NetworkDiscoveryStatus == "",
                $"An uninstalled Job Configurator card gains a web UI link without becoming locally installed (compact={card.ShowCompact}, discovery={card.ShowNetworkDiscovery}, links={card.NetworkConfigurators.Count}, status={card.NetworkDiscoveryStatus})");
            Complete(window.RefreshJobDiscoveryAsync(card, new JobConfiguratorDiscovery(http, () => [])));
            Check(card.NetworkConfigurators.Count == 0, "A later check clears stale remote links");
            using var stalled = new HttpClient(new AsyncHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Health("{}"); }));
            var pending = window.RefreshJobDiscoveryAsync(card, new JobConfiguratorDiscovery(stalled, () => [(IPAddress.Parse("192.0.2.1"), 30)]));
            card.Installed = new("C:\\Fixture\\job.exe", "1.0.0");
            Complete(window.RefreshJobDiscoveryAsync(card, discovery));
            Complete(pending);
            Check(!card.ShowNetworkDiscovery && card.NetworkConfigurators.Count == 0 && card.NetworkDiscoveryStatus == "",
                "Installing Job Configurator cancels the old scan and hides network discovery");
        }
        finally { window.Close(); }
    }
}
