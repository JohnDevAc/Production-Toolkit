using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ToolkitLauncher.Core;

public sealed class GitHubClient(HttpClient http)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<ReleaseSnapshot> GetReleasesAsync(AppDefinition app, CancellationToken cancellationToken)
    {
        List<Release> releases = [];
        for (var page = 1; page <= 20; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/JohnDevAc/{app.Repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("Production-Toolkit/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var seconds)
                    ? " Try again after " + DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("HH:mm") + "." : " Please try again later.";
                throw new HttpRequestException("GitHub is limiting release checks." + reset);
            }
            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<List<Release>>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("GitHub returned an empty response.");
            releases.AddRange(batch);
            if (batch.Count < 100) return new(releases, DateTimeOffset.Now);
        }
        throw new InvalidDataException("The release list is too large to check completely. Open the project's releases page.");
    }
}
