using System.Text.Json;

namespace HomeTV.Core.Apps;

/// <summary>
/// The launcher tiles, in display order, plus apps found running that aren't tiles (Steam games), which can be
/// switched to, tiled and closed like any other app while they're open.
/// </summary>
public sealed class AppRegistry
{
    private readonly Dictionary<string, AppDefinition> _byId;
    private readonly Dictionary<string, AppDefinition> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public AppRegistry(IReadOnlyList<AppDefinition> apps)
    {
        var duplicate = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate app id '{duplicate.Key}' in apps config.");
        if (apps.FirstOrDefault(a => a.Untrusted && (a.Kind != AppKind.Edge || a.EdgeProfile is null)) is { } unsafeApp)
            throw new InvalidOperationException(
                $"App '{unsafeApp.Id}' is untrusted, so it must be an Edge app with its own edgeProfile (that's how HomeTV closes its pop-ups).");

        Apps = apps;
        _byId = apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The launcher tiles.</summary>
    public IReadOnlyList<AppDefinition> Apps { get; }

    /// <summary>The tiles, then the apps found running.</summary>
    public IReadOnlyList<AppDefinition> All
    {
        get
        {
            lock (_lock)
                return [.. Apps, .. _discovered.Values];
        }
    }

    public AppDefinition? Find(string id)
    {
        if (_byId.TryGetValue(id, out var app))
            return app;
        lock (_lock)
            return _discovered.GetValueOrDefault(id);
    }

    /// <summary>Adds an app found running (it can't replace a tile).</summary>
    public void AddDiscovered(AppDefinition app)
    {
        if (_byId.ContainsKey(app.Id))
            return;
        lock (_lock)
            _discovered[app.Id] = app with { Discovered = true };
    }

    /// <summary>Forgets an app found running once it has closed. Tiles stay.</summary>
    public void RemoveDiscovered(string id)
    {
        lock (_lock)
            _discovered.Remove(id);
    }

    public static AppRegistry Parse(string json) =>
        new(JsonSerializer.Deserialize<List<AppDefinition>>(json, HomeTvJson.Options)
            ?? throw new InvalidOperationException("Apps config is empty."));

    public static AppRegistry Load(string path) => Parse(File.ReadAllText(path));
}
