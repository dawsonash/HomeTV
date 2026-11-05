using HomeTV.Core.Apps;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class AppSwitcherTests
{
    private readonly RecordingAppHost _host = new();
    private readonly SessionState _session = new();
    private readonly AppRegistry _apps = new([
        new AppDefinition { Id = "youtube", Name = "YouTube", Kind = AppKind.Edge, Target = "https://www.youtube.com" },
        new AppDefinition { Id = "prime", Name = "Prime Video", Kind = AppKind.Edge, Target = "https://www.primevideo.com" },
        new AppDefinition { Id = "fubo", Name = "Fubo", Kind = AppKind.Edge, Target = "https://www.fubo.tv" },
        new AppDefinition { Id = "spotify", Name = "Spotify", Kind = AppKind.Shell, Target = "spotify:", PlayInBackground = true },
        new AppDefinition { Id = "games", Name = "Steam", Kind = AppKind.Shell, Target = "steam://open/bigpicture" },
    ]);
    private readonly AppSwitcher _switcher;

    public AppSwitcherTests() =>
        _switcher = new AppSwitcher(_host, _apps, _session, new AppSwitcherOptions(MaxBackgroundApps: 2), NullLogger<AppSwitcher>.Instance);

    private AppDefinition App(string id) => _apps.Find(id)!;

    [Fact]
    public async Task Going_back_to_an_open_app_shows_it_instead_of_starting_it_again()
    {
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.LaunchAsync(App("prime"));
        await _switcher.LaunchAsync(App("youtube"));

        Assert.Equal(["launch youtube", "pause youtube", "launch prime", "pause prime", "show youtube"], _host.Calls);
        Assert.Equal("youtube", _session.ForegroundAppId);
        Assert.Equal(["youtube", "prime"], _session.State.Running);
    }

    [Fact]
    public async Task Home_pauses_video_but_music_keeps_playing()
    {
        await _switcher.LaunchAsync(App("spotify"));
        await _switcher.HomeAsync();
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.HomeAsync();

        Assert.Equal(["launch spotify", "launcher", "launch youtube", "pause youtube", "launcher"], _host.Calls);
        Assert.True(_session.LauncherInFront);
        Assert.Equal(["youtube", "spotify"], _session.State.Running);
    }

    [Fact]
    public async Task Least_recently_used_app_closes_past_the_background_limit()
    {
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.LaunchAsync(App("prime"));
        await _switcher.LaunchAsync(App("fubo"));
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("games"));

        // Prime and Fubo stay in the background; YouTube, used longest ago, goes.
        Assert.Equal(["pause fubo", "launch games", "close youtube"], _host.Calls);
        Assert.Equal(["games", "fubo", "prime"], _session.State.Running);
    }

    [Fact]
    public async Task Opening_the_page_an_open_app_is_already_on_just_brings_it_back()
    {
        await _switcher.LaunchAsync(App("youtube"));
        _host.Pages["youtube"] = "https://www.youtube.com/watch?v=abc&t=42s";
        await _switcher.HomeAsync();
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("youtube"), "https://www.youtube.com/watch?v=abc&t=120s",
            isSamePage: url => url.StartsWith("https://www.youtube.com/watch?v=abc", StringComparison.Ordinal));

        Assert.Equal(["show youtube"], _host.Calls);
    }

    [Fact]
    public async Task Opening_another_page_in_an_open_app_uses_its_tab()
    {
        await _switcher.LaunchAsync(App("youtube"));
        _host.Pages["youtube"] = "https://www.youtube.com/watch?v=other";
        await _switcher.HomeAsync();
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("youtube"), "https://www.youtube.com/watch?v=abc", isSamePage: _ => false);

        Assert.Equal(["navigate youtube https://www.youtube.com/watch?v=abc", "show youtube"], _host.Calls);
    }

    [Fact]
    public async Task Opening_a_page_restarts_an_open_app_whose_page_is_out_of_reach()
    {
        await _switcher.LaunchAsync(App("youtube"));
        _host.Unreachable.Add("youtube");
        await _switcher.HomeAsync();
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("youtube"), "https://www.youtube.com/watch?v=abc");

        Assert.Equal(["navigate youtube https://www.youtube.com/watch?v=abc", "close youtube", "launch youtube https://www.youtube.com/watch?v=abc"], _host.Calls);
    }

    [Fact]
    public async Task Window_that_vanished_is_started_again()
    {
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.HomeAsync();
        _host.Open.Clear(); // Closed, and the exit not reported yet.
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("youtube"));

        Assert.Equal(["show youtube", "launch youtube"], _host.Calls);
    }

    [Fact]
    public async Task Split_screen_starts_missing_apps_and_keeps_the_one_on_screen_active()
    {
        await _switcher.LaunchAsync(App("youtube"));
        _host.Calls.Clear();

        await _switcher.SplitAsync(["fubo", "youtube"]);

        Assert.Equal(["launch fubo", "tile fubo,youtube active youtube"], _host.Calls);
        Assert.Equal(["fubo", "youtube"], _session.State.Split);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public async Task Picking_a_tile_moves_focus_without_leaving_split_screen()
    {
        await _switcher.SplitAsync(["fubo", "youtube"]);
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.OnAppActivatedAsync("fubo");

        Assert.Equal(["tile fubo,youtube active youtube", "tile fubo,youtube active fubo"], _host.Calls);
        Assert.Equal("fubo", _session.ForegroundAppId);
        Assert.NotNull(_session.State.Split);
    }

    [Fact]
    public async Task Opening_another_app_from_split_screen_pauses_the_tiles()
    {
        await _switcher.SplitAsync(["fubo", "youtube"]);
        _host.Calls.Clear();

        await _switcher.LaunchAsync(App("prime"));

        Assert.Equal(["pause fubo", "pause youtube", "launch prime"], _host.Calls);
        Assert.Null(_session.State.Split);
    }

    [Fact]
    public async Task Ending_split_screen_leaves_one_app_full_screen()
    {
        await _switcher.SplitAsync(["fubo", "youtube"]);
        _host.Calls.Clear();

        await _switcher.SplitAsync(["youtube"]);

        Assert.Equal(["pause fubo", "show youtube"], _host.Calls);
        Assert.Null(_session.State.Split);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public async Task Tile_closing_leaves_the_other_one_full_screen()
    {
        await _switcher.SplitAsync(["fubo", "youtube"]);
        _host.Calls.Clear();

        await _switcher.OnAppExitedAsync("fubo");

        Assert.Equal(["show youtube"], _host.Calls);
        Assert.Null(_session.State.Split);
        Assert.Equal("youtube", _session.ForegroundAppId);
        Assert.Equal(["youtube"], _session.State.Running);
    }

    [Fact]
    public async Task Tile_closing_out_of_three_re_tiles_the_rest()
    {
        await _switcher.SplitAsync(["fubo", "youtube", "prime"]);
        _host.Calls.Clear();

        await _switcher.CloseAsync("fubo");

        Assert.Equal(["close fubo", "tile youtube,prime active youtube"], _host.Calls);
        Assert.Equal(["youtube", "prime"], _session.State.Split);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public async Task Split_screen_without_two_windows_falls_back_to_one_app()
    {
        _host.NeverOpens.Add("fubo");

        await _switcher.SplitAsync(["fubo", "youtube"]);

        Assert.Equal(["launch fubo", "launch youtube", "show youtube"], _host.Calls);
        Assert.Null(_session.State.Split);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public async Task Front_app_exiting_brings_the_launcher_back_but_a_background_one_does_not()
    {
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.LaunchAsync(App("prime"));
        _host.Calls.Clear();

        await _switcher.OnAppExitedAsync("youtube");
        Assert.Empty(_host.Calls);
        Assert.Equal("prime", _session.ForegroundAppId);

        await _switcher.OnAppExitedAsync("prime");
        Assert.Equal(["launcher"], _host.Calls);
        Assert.True(_session.LauncherInFront);
        Assert.Empty(_session.State.Running);
    }

    [Fact]
    public async Task Unknown_apps_and_too_many_tiles_are_ignored()
    {
        await _switcher.SplitAsync(["netflix", "youtube", "prime", "fubo", "games", "spotify"]);

        Assert.Equal(["youtube", "prime", "fubo", "games"], _session.State.Split);
    }

    private static readonly AppDefinition Game = new()
    {
        Id = "steam-570", Name = "Dota 2", Kind = AppKind.Shell, Target = "steam://rungameid/570", WindowProcess = "dota2",
    };

    [Fact]
    public async Task A_game_started_from_Big_Picture_becomes_an_open_app_in_front()
    {
        await _switcher.LaunchAsync(App("games"));
        await _switcher.OnAppDiscoveredAsync(Game);

        Assert.Equal("steam-570", _session.ForegroundAppId);
        Assert.Equal(["steam-570", "games"], _session.State.Running);
        Assert.Equal("Dota 2", _apps.Find("steam-570")?.Name);
        // Not a launcher tile.
        Assert.DoesNotContain(_apps.Apps, a => a.Id == "steam-570");
    }

    [Fact]
    public async Task A_game_can_be_tiled_and_closed_like_an_app()
    {
        _host.Open.Add("steam-570");
        await _switcher.LaunchAsync(App("youtube"));
        await _switcher.OnAppDiscoveredAsync(Game);

        await _switcher.SplitAsync(["steam-570", "youtube"]);
        Assert.Equal(["steam-570", "youtube"], _session.State.Split);

        _host.Calls.Clear();
        await _switcher.CloseAsync("steam-570");

        Assert.Contains("close steam-570", _host.Calls);
        Assert.Null(_apps.Find("steam-570"));
        Assert.Equal(["youtube"], _session.State.Running);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public async Task Sign_in_opens_the_apps_sign_in_page_in_front()
    {
        var apps = new AppRegistry([
            new AppDefinition { Id = "youtube", Name = "YouTube", Kind = AppKind.Edge, Target = "https://www.youtube.com", EdgeProfile = "youtube", SignInUrl = "https://accounts.google.com/" },
        ]);
        var switcher = new AppSwitcher(_host, apps, _session, new AppSwitcherOptions(), NullLogger<AppSwitcher>.Instance);

        await switcher.SignInAsync(apps.Find("youtube")!);

        Assert.Equal(["sign in youtube"], _host.Calls);
        Assert.Equal("youtube", _session.ForegroundAppId);
    }

    [Fact]
    public void An_untrusted_app_needs_its_own_Edge_profile()
    {
        Assert.Throws<InvalidOperationException>(() => new AppRegistry([
            new AppDefinition { Id = "sketchy", Name = "Sketchy", Kind = AppKind.Edge, Target = "https://example.com", Untrusted = true },
        ]));
    }
}
