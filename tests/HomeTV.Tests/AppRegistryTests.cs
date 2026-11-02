using HomeTV.Core.Apps;

namespace HomeTV.Tests;

public class AppRegistryTests
{
    [Fact]
    public void Shipped_apps_config_loads()
    {
        var registry = AppRegistry.Load(Path.Combine(AppContext.BaseDirectory, "apps.json"));

        Assert.Equal(["prime", "fubo", "youtube", "games"], registry.Apps.Select(a => a.Id));
        Assert.Equal(AppKind.Shell, registry.Find("games")!.Kind);
        Assert.Equal("youtube", registry.Find("YouTube")!.EdgeProfile);
    }

    [Fact]
    public void Duplicate_ids_are_rejected() =>
        Assert.Throws<InvalidOperationException>(() => AppRegistry.Parse("""
            [
              { "id": "a", "name": "A", "kind": "edge", "target": "https://a" },
              { "id": "A", "name": "A2", "kind": "edge", "target": "https://a2" }
            ]
            """));
}
