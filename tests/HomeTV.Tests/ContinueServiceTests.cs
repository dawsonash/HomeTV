using HomeTV.Core.Apps;
using HomeTV.Core.Continue;

namespace HomeTV.Tests;

public class ContinueServiceTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryContinueStore _store = new();
    private readonly AppRegistry _apps = new([
        new AppDefinition
        {
            Id = "youtube", Name = "YouTube", Kind = AppKind.Edge, Target = "https://www.youtube.com",
            EdgeProfile = "youtube", ResumePattern = @"^https://www\.youtube\.com/watch\?", ResumeTimeParam = "t",
        },
        new AppDefinition
        {
            Id = "prime", Name = "Prime Video", Kind = AppKind.Edge, Target = "https://www.primevideo.com",
            EdgeProfile = "prime", ResumePattern = "/detail/",
        },
        new AppDefinition { Id = "fubo", Name = "Fubo", Kind = AppKind.Edge, Target = "https://www.fubo.tv" },
    ]);

    private ContinueService Create() => new(_apps, _store, _time);

    private static PageSnapshot Video(string url, double position, double? duration = 600, string? title = "A video - YouTube") =>
        new(url, title, "https://i.ytimg.com/vi/abc/hqdefault.jpg", position, duration);

    [Fact]
    public void Remembers_a_video_once_it_has_played_a_little()
    {
        var service = Create();

        Assert.False(service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 3)));
        Assert.Empty(service.Entries);

        Assert.True(service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 30)));
        var entry = Assert.Single(service.Entries);
        Assert.Equal("A video", entry.Title); // " - YouTube" dropped.
        Assert.Equal(30, entry.Position);
        Assert.Equal("https://i.ytimg.com/vi/abc/hqdefault.jpg", entry.Image);
    }

    [Fact]
    public void Ignores_pages_outside_the_pattern_and_apps_without_one()
    {
        var service = Create();

        service.Observe("youtube", Video("https://www.youtube.com/", 100));
        service.Observe("youtube", Video("https://www.youtube.com/feed/subscriptions", 100));
        service.Observe("fubo", Video("https://www.fubo.tv/watch/123", 100));
        service.Observe("youtube", Video("javascript:alert(1)//www.youtube.com/watch?", 100));

        Assert.Empty(service.Entries);
    }

    [Fact]
    public void One_entry_per_video_however_the_time_param_and_fragment_differ()
    {
        var service = Create();

        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc&t=10s", 30));
        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc#comments", 60));

        var entry = Assert.Single(service.Entries);
        Assert.Equal("https://www.youtube.com/watch?v=abc", entry.Url);
        Assert.Equal(60, entry.Position);
    }

    [Fact]
    public void Newest_first_and_capped()
    {
        var service = Create();
        for (var i = 0; i < ContinueService.MaxEntries + 2; i++)
            service.Observe("youtube", Video($"https://www.youtube.com/watch?v={i}", 30));

        Assert.Equal(ContinueService.MaxEntries, service.Entries.Count);
        Assert.Equal($"https://www.youtube.com/watch?v={ContinueService.MaxEntries + 1}", service.Entries[0].Url);
    }

    [Fact]
    public void Watching_to_the_credits_removes_it()
    {
        var service = Create();
        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 30));

        Assert.True(service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 590)));

        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Live_streams_keep_no_duration()
    {
        var service = Create();

        service.Observe("prime", new PageSnapshot("https://www.primevideo.com/detail/XYZ/", "Watch Show | Prime Video", null, 300, double.PositiveInfinity));

        var entry = Assert.Single(service.Entries);
        Assert.Null(entry.Duration);
        Assert.Equal("Show", entry.Title);
    }

    [Theory]
    [InlineData("Watch Bluey | Disney+", "Bluey")]
    [InlineData("Watch Abbott Elementary Online | Hulu", "Abbott Elementary")]
    [InlineData("Shogun - Hulu", "Shogun")]
    public void Drops_the_app_name_from_titles(string title, string expected)
    {
        var service = Create();

        service.Observe("prime", new PageSnapshot("https://www.primevideo.com/detail/XYZ/", title, null, 300, 3000));

        Assert.Equal(expected, Assert.Single(service.Entries).Title);
    }

    [Fact]
    public void Resume_url_backs_up_a_few_seconds_when_the_app_supports_a_start_time()
    {
        var service = Create();
        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc&list=PL1", 125));
        service.Observe("prime", new PageSnapshot("https://www.primevideo.com/detail/XYZ/", "Show", null, 300, 3000));

        var youtube = service.Entries.Single(e => e.AppId == "youtube");
        var prime = service.Entries.Single(e => e.AppId == "prime");

        Assert.Equal("https://www.youtube.com/watch?v=abc&list=PL1&t=120s", service.ResumeUrl(youtube));
        Assert.Equal("https://www.primevideo.com/detail/XYZ/", service.ResumeUrl(prime));
    }

    [Fact]
    public void Position_only_updates_are_saved_at_most_every_30_seconds_and_on_flush()
    {
        var service = Create();
        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 30));
        var saves = _store.Saves;

        _time.Advance(TimeSpan.FromSeconds(5));
        service.Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 35));
        Assert.Equal(saves, _store.Saves);

        service.Flush();
        Assert.Equal(saves + 1, _store.Saves);
        Assert.Equal(35, _store.Load()[0].Position);
    }

    [Fact]
    public void Survives_a_restart_and_drops_apps_that_no_longer_opt_in()
    {
        Create().Observe("youtube", Video("https://www.youtube.com/watch?v=abc", 30));
        _store.Save([.. _store.Load(), new ContinueEntry("x", "fubo", "https://www.fubo.tv/watch/1", "Game", null, 10, null, _time.GetUtcNow())]);

        var reloaded = Create();

        Assert.Equal("youtube", Assert.Single(reloaded.Entries).AppId);
    }

    [Fact]
    public void Bad_pattern_fails_at_startup()
    {
        var apps = new AppRegistry([
            new AppDefinition { Id = "x", Name = "X", Kind = AppKind.Edge, Target = "https://x.test", ResumePattern = "(" },
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => new ContinueService(apps, _store, _time));
        Assert.Contains("'x'", ex.Message);
    }
}
