using System.Net;
using System.Net.Http;

namespace NdiSuite.Installation;

public static class DownloadReadiness
{
    public static async Task CheckAsync(HttpClient client, Uri source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, source);
            using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, source);
                get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var fallback = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Validate(fallback);
            }
            else Validate(response);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new IOException($"Required download unavailable from {source.Host}. Check internet access, proxy and package-source access, then retry. {ex.Message}", ex);
        }
    }

    private static void Validate(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true)
            throw new HttpRequestException("The package source returned a web page instead of a package.");
    }

    public static async Task<int> ReadAsync(Stream source, Memory<byte> buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { return await source.ReadAsync(buffer, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new IOException("The package transfer stopped responding. Retry when the download source is available.", ex); }
    }
}

