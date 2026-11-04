using HomeTV.Core.Apps;

namespace HomeTV.Tests;

public class AppRegistryTests
{
    [Fact]
    public void Shipped_apps_config_loads()
    {
        var registry = AppRegistry.Load(Path.Combine(AppContext.BaseDirectory, "apps.json"));

        Assert.Equal(["prime", "fubo", "youtube", "games", "spotify"], registry.Apps.Select(a => a.Id));
        Assert.Equal(AppKind.Shell, registry.Find("games")!.Kind);
        Assert.Equal("Spotify", registry.Find("spotify")!.WindowProcess); // Store app; Home closes its window.
        Assert.Equal("youtube", registry.Find("YouTube")!.EdgeProfile);
    }

    [Fact]
    public void Games_tile_knows_its_window_so_home_can_close_it()
    {
        var games = AppRegistry.Load(Path.Combine(AppContext.BaseDirectory, "apps.json")).Find("games")!;

        Assert.Equal("steamwebhelper", games.WindowProcess);
        Assert.Equal("Big Picture", games.WindowTitle);
        Assert.Equal("steam://exit", games.CloseTarget);
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
