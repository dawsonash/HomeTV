using System.Text.Json;

namespace HomeTV.Core.Apps;

/// <summary>The launcher tiles, in display order.</summary>
public sealed class AppRegistry
{
    private readonly Dictionary<string, AppDefinition> _byId;

    public AppRegistry(IReadOnlyList<AppDefinition> apps)
    {
        var duplicate = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate app id '{duplicate.Key}' in apps config.");

        Apps = apps;
        _byId = apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AppDefinition> Apps { get; }

    public AppDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public static AppRegistry Parse(string json) =>
        new(JsonSerializer.Deserialize<List<AppDefinition>>(json, HomeTvJson.Options)
            ?? throw new InvalidOperationException("Apps config is empty."));

    public static AppRegistry Load(string path) => Parse(File.ReadAllText(path));
}
