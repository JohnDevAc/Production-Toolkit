using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ToolkitLauncher.Core;

public sealed class GitHubClient
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly HttpClient http;
    private readonly string? statePath;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim queue = new(1, 1);
    private readonly CheckState state;

    public GitHubClient(HttpClient http, string? statePath = null, TimeProvider? clock = null)
    {
        this.http = http;
        this.statePath = statePath;
        this.clock = clock ?? TimeProvider.System;
        try
        {
            state = statePath is not null && File.Exists(statePath)
                ? JsonSerializer.Deserialize<CheckState>(File.ReadAllText(statePath), JsonOptions) ?? new() : new();
            state.Snapshots ??= [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { state = new(); }
    }

    public async Task<ReleaseSnapshot> GetReleasesAsync(AppDefinition app, CancellationToken cancellationToken, bool userRequested = false)
    {
        // All five applications share this queue and the persisted rate-limit deadline.
        await queue.WaitAsync(cancellationToken);
        try
        {
            ThrowIfLimited();
            var minimumAge = userRequested ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10);
            if (state.Snapshots.TryGetValue(app.Repository, out var cached) && cached?.Releases is not null &&
                clock.GetUtcNow() - cached.CheckedAt is var age && age >= TimeSpan.Zero && age < minimumAge)
                return cached with { IsCached = true };
            var snapshot = await FetchAsync(app, cancellationToken);
            state.Snapshots[app.Repository] = snapshot;
            state.LimitFailures = 0;
            SaveState();
            return snapshot;
        }
        finally { queue.Release(); }
    }

    private async Task<ReleaseSnapshot> FetchAsync(AppDefinition app, CancellationToken cancellationToken)
    {
        List<Release> releases = [];
        for (var page = 1; page <= 20; page++)
        {
            ThrowIfLimited();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/JohnDevAc/{Uri.EscapeDataString(app.Repository)}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("Production-Toolkit/1.2");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, cancellationToken);
            var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
                (exhausted || response.Headers.RetryAfter is not null ||
                 (await response.Content.ReadAsStringAsync(cancellationToken)).Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            {
                state.LimitFailures = Math.Clamp(state.LimitFailures, 0, 6) + 1;
                state.RetryAt = RetryAt(response, exhausted, TimeSpan.FromMinutes(Math.Pow(2, state.LimitFailures - 1)));
                SaveState();
                ThrowIfLimited();
            }
            response.EnsureSuccessStatusCode();
            if (exhausted)
            {
                state.RetryAt = RetryAt(response, true, TimeSpan.FromMinutes(1));
                SaveState();
            }
            var batch = await response.Content.ReadFromJsonAsync<List<Release>>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("GitHub returned an empty response.");
            releases.AddRange(batch);
            if (batch.Count < 100) return new(releases, clock.GetUtcNow());
        }
        throw new InvalidDataException("The release list is too large to check completely. Open the project's releases page.");
    }

    private DateTimeOffset RetryAt(HttpResponseMessage response, bool exhausted, TimeSpan fallback)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero) retry = now + delta;
        if (exhausted && response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var seconds) && seconds is >= -62135596800 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (retry is null || reset > retry) retry = reset;
        }
        return retry > now ? retry.Value : now + fallback;
    }

    private void ThrowIfLimited()
    {
        if (state.RetryAt is { } retry && retry > clock.GetUtcNow())
            throw new HttpRequestException("GitHub checks are paused until " + retry.ToLocalTime().ToString("HH:mm") +
                ". Saved releases remain available. Check again afterward.");
    }

    private void SaveState()
    {
        if (statePath is null) return;
        try
        {
            var path = Path.GetFullPath(statePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, JsonOptions));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public sealed class CheckState
    {
        public Dictionary<string, ReleaseSnapshot> Snapshots { get; set; } = [];
        public DateTimeOffset? RetryAt { get; set; }
        public int LimitFailures { get; set; }
    }
}
