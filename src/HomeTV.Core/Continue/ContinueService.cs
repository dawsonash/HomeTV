using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeTV.Core.Apps;

namespace HomeTV.Core.Continue;

/// <summary>What HomeTV last saw on an Edge app's page: its address, how it describes itself, and its video's progress.</summary>
public sealed record PageSnapshot(string Url, string? Title, string? Image, double? Position, double? Duration);

/// <summary>One item in the launcher's "Continue watching" row.</summary>
public sealed record ContinueEntry(
    string Id,
    string AppId,
    string Url,
    string Title,
    string? Image,
    double? Position,
    double? Duration,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Remembers the pages people watched videos on, so the launcher can reopen them. Fed by the app host, which
/// looks at the running Edge app's page every few seconds.
/// </summary>
public sealed class ContinueService
{
    public const int MaxEntries = 8;

    /// <summary>A page only counts once its video has played this far, so browsing past a trailer doesn't add it.</summary>
    public const double MinWatchedSeconds = 10;

    private const int MaxTitleLength = 200;
    private const int MaxUrlLength = 2000;

    /// <summary>Position-only changes are written to disk at most this often; the in-memory list is always current.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly AppRegistry _apps;
    private readonly IContinueStore _store;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private List<ContinueEntry> _entries;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _dirty;

    public ContinueService(AppRegistry apps, IContinueStore store, TimeProvider time)
    {
        _apps = apps;
        _store = store;
        _time = time;
        foreach (var app in apps.Apps)
        {
            if (app.ResumePattern is null)
                continue;
            try
            {
                _patterns[app.Id] = new Regex(app.ResumePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"App '{app.Id}' has an invalid resumePattern: {ex.Message}", ex);
            }
        }
        // Drop entries for apps that were removed or no longer opt in.
        _entries = store.Load().Where(e => _patterns.ContainsKey(e.AppId)).Take(MaxEntries).ToList();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ContinueEntry> Entries
    {
        get
        {
            lock (_lock)
                return [.. _entries];
        }
    }

    /// <summary>True if <paramref name="appId"/> has a resumePattern, i.e. it's worth looking at its pages.</summary>
    public bool Tracks(string appId) => _patterns.ContainsKey(appId);

    public ContinueEntry? Find(string id)
    {
        lock (_lock)
            return _entries.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Records what's on an app's page. Returns true if the row changed.</summary>
    public bool Observe(string appId, PageSnapshot page)
    {
        if (!_patterns.TryGetValue(appId, out var pattern) || _apps.Find(appId) is not { } app)
            return false;
        if (!IsWebUrl(page.Url) || page.Url.Length > MaxUrlLength || !Matches(pattern, page.Url))
            return false;

        var key = Normalize(page.Url, app.ResumeTimeParam);
        var id = MakeId(app.Id, key);
        var duration = page.Duration is > 0 and < 86_400 ? page.Duration : null; // Live streams report Infinity.
        var position = page.Position is >= 0 && double.IsFinite(page.Position.Value) ? page.Position : null;

        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.Id == id);

            if (position is { } p && duration is { } d && IsFinished(p, d))
            {
                if (index < 0)
                    return false;
                _entries.RemoveAt(index);
                SaveLocked(force: true);
                return true;
            }

            var existing = index >= 0 ? _entries[index] : null;
            // A new page only counts once something has actually played on it.
            if (existing is null && position is not >= MinWatchedSeconds)
                return false;

            var entry = new ContinueEntry(
                id,
                app.Id,
                key,
                Clean(page.Title) ?? existing?.Title ?? app.Name,
                IsHttps(page.Image) && page.Image!.Length <= MaxUrlLength ? page.Image : existing?.Image,
                position ?? existing?.Position,
                duration ?? existing?.Duration,
                _time.GetUtcNow());

            var structural = index != 0 || existing!.Title != entry.Title || existing.Image != entry.Image;
            if (index >= 0)
                _entries.RemoveAt(index);
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            SaveLocked(force: structural);
            return structural;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (_entries.RemoveAll(e => e.Id == id) == 0)
                return false;
            SaveLocked(force: true);
            return true;
        }
    }

    /// <summary>Writes any position updates still only in memory (call on shutdown).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_dirty)
                SaveLocked(force: true);
        }
    }

    /// <summary>The address to open: the saved page, starting at the saved time if the app supports that.</summary>
    public string ResumeUrl(ContinueEntry entry)
    {
        var app = _apps.Find(entry.AppId);
        if (app?.ResumeTimeParam is not { } param || entry.Position is not { } position || position < MinWatchedSeconds)
            return entry.Url;

        // Back up a few seconds so you see where you left off.
        var seconds = Math.Max(0, (int)position - 5);
        var builder = new UriBuilder(entry.Url);
        var query = builder.Query.TrimStart('?');
        builder.Query = (query.Length > 0 ? query + "&" : "") + $"{Uri.EscapeDataString(param)}={seconds}s";
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>In the credits: the last 5% or 30 seconds, whichever is longer.</summary>
    private static bool IsFinished(double position, double duration) =>
        duration - position <= Math.Max(30, duration * 0.05);

    private static bool Matches(Regex pattern, string url)
    {
        try
        {
            return pattern.IsMatch(url);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The page's address without its start time or #fragment, so one video is one entry wherever you paused.</summary>
    internal static string Normalize(string url, string? timeParam)
    {
        var builder = new UriBuilder(url) { Fragment = "" };
        if (timeParam is not null && builder.Query.Length > 1)
        {
            var kept = builder.Query[1..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(Uri.UnescapeDataString(part.Split('=')[0]), timeParam, StringComparison.Ordinal));
            builder.Query = string.Join('&', kept);
        }
        // UriBuilder adds :443/:80 only when the port isn't the scheme's default, so this round-trips cleanly.
        return builder.Uri.AbsoluteUri;
    }

    private static string MakeId(string appId, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}\n{key}")))[..12];

    private static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        title = Regex.Replace(title.Trim(), @"\s+", " ");
        // "Some video - YouTube", "Watch Show | Prime Video": the tile already says which app.
        title = Regex.Replace(title, @"\s*[-|–]\s*(YouTube|Prime Video|Amazon\.com|Fubo|Disney\+|Hulu)$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"^\(\d+\)\s*", ""); // YouTube's "(3) " notification count.
        title = Regex.Replace(title, @"^Watch\s+", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+Online$", "", RegexOptions.IgnoreCase); // Hulu's "Watch X Online".
        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    private void SaveLocked(bool force)
    {
        var now = _time.GetUtcNow();
        if (!force && now - _lastSave < SaveInterval)
        {
            _dirty = true;
            return;
        }
        _store.Save(_entries);
        _lastSave = now;
        _dirty = false;
    }
}

public interface IContinueStore
{
    IReadOnlyList<ContinueEntry> Load();
    void Save(IReadOnlyList<ContinueEntry> entries);
}

public sealed class InMemoryContinueStore : IContinueStore
{
    private IReadOnlyList<ContinueEntry> _entries = [];

    public int Saves { get; private set; }

    public IReadOnlyList<ContinueEntry> Load() => _entries;

    public void Save(IReadOnlyList<ContinueEntry> entries)
    {
        _entries = [.. entries];
        Saves++;
    }
}

public sealed class JsonFileContinueStore(string path) : IContinueStore
{
    public IReadOnlyList<ContinueEntry> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<ContinueEntry>>(File.ReadAllText(path), HomeTvJson.Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // Losing the row beats refusing to start.
        }
    }

    public void Save(IReadOnlyList<ContinueEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, HomeTvJson.Options));
        File.Move(temp, path, overwrite: true);
    }
}
