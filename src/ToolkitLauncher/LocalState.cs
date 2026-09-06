using System.Text.Json;
using ToolkitLauncher.Core;

namespace ToolkitLauncher;

public sealed class Preferences
{
    public Dictionary<string, ReleaseChannel> Channels { get; set; } = [];
    public Dictionary<string, string> LaunchPaths { get; set; } = [];
    public Dictionary<string, SetupRecord> Setups { get; set; } = [];
}

public sealed record SetupRecord(string Path, string Tag, string Digest);

public sealed class LocalState
{
    public string Root { get; }
    public string DownloadRoot => Path.Combine(Root, "Downloads");
    public Preferences Preferences { get; }
    public string? Warning { get; private set; }
    public LocalState(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Production Toolkit");
        Directory.CreateDirectory(Root);
        try { Preferences = Read<Preferences>("settings.json") ?? new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Preferences = new();
            Warning = "Saved preferences could not be read. Using defaults. " + e.Message;
            // Preserve the unreadable original before future preference writes.
            var path = Path.Combine(Root, "settings.json");
            if (File.Exists(path)) File.Copy(path, path + "." + DateTime.UtcNow.Ticks + ".bak");
        }
    }
    private T? Read<T>(string name)
    {
        var path = Path.Combine(Root, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), GitHubClient.JsonOptions) : default;
    }
    private void Write<T>(string name, T data)
    {
        var path = Path.Combine(Root, name); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, GitHubClient.JsonOptions));
        File.Move(temporary, path, true);
    }
    public void Save() => Write("settings.json", Preferences);
    public void SaveCache(AppDefinition app, ReleaseSnapshot snapshot) => Write(app.Id + "-releases.json", snapshot);
    public ReleaseSnapshot? LoadCache(AppDefinition app)
    {
        try { return Read<ReleaseSnapshot>(app.Id + "-releases.json"); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
    public void Log(string message)
    {
        try
        {
            var path = Path.Combine(Root, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
