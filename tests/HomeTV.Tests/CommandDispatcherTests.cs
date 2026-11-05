using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.NowPlaying;
using HomeTV.Core.Platform;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class CommandDispatcherTests
{
    private readonly RecordingPlatform _platform = new();
    private readonly SessionState _session = new();
    private readonly ContinueService _continue;
    private readonly CommandDispatcher _dispatcher;
    private readonly RecordingAppHost _host;

    public CommandDispatcherTests()
    {
        var apps = new AppRegistry([
            new AppDefinition
            {
                Id = "youtube", Name = "YouTube", Kind = AppKind.Edge, Target = "https://www.youtube.com",
                EdgeProfile = "youtube", ResumePattern = @"youtube\.com/watch", ResumeTimeParam = "t",
            },
            new AppDefinition { Id = "prime", Name = "Prime Video", Kind = AppKind.Edge, Target = "https://www.primevideo.com" },
        ]);
        _continue = new ContinueService(apps, new InMemoryContinueStore(), TimeProvider.System);
        var nowPlaying = new NowPlayingService(_platform, _session, TimeProvider.System, NullLogger<NowPlayingService>.Instance);
        _host = new RecordingAppHost(_platform.Calls);
        var host = _host;
        var switcher = new AppSwitcher(host, apps, _session, new AppSwitcherOptions(), NullLogger<AppSwitcher>.Instance);
        _dispatcher = new CommandDispatcher(_platform, host, switcher, _platform, apps, _session, _continue, nowPlaying, NullLogger<CommandDispatcher>.Instance);
    }

    [Fact]
    public async Task Continue_entry_opens_its_page_at_the_saved_time()
    {
        _continue.Observe("youtube", new PageSnapshot("https://www.youtube.com/watch?v=abc", "A video", null, 125, 600));
        var entry = Assert.Single(_continue.Entries);

        await _dispatcher.DispatchAsync(new LaunchCommand("youtube", entry.Id));

        Assert.Equal(["launch youtube https://www.youtube.com/watch?v=abc&t=120s"], _platform.Calls);
    }

    [Fact]
    public async Task Continue_entry_for_the_video_still_open_brings_that_tab_back()
    {
        // Watching a video, then Home, then its Continue watching card: no second copy of the video.
        await _dispatcher.DispatchAsync(new LaunchCommand("youtube"));
        _host.Pages["youtube"] = "https://www.youtube.com/watch?v=abc&t=90s#comments";
        _continue.Observe("youtube", new PageSnapshot("https://www.youtube.com/watch?v=abc", "A video", null, 125, 600));
        await _dispatcher.DispatchAsync(new HomeCommand());
        _platform.Calls.Clear();

        await _dispatcher.DispatchAsync(new LaunchCommand("youtube", Assert.Single(_continue.Entries).Id));

        Assert.Equal(["show youtube"], _platform.Calls);
    }

    [Fact]
    public async Task Unknown_or_mismatched_continue_entry_opens_the_start_page()
    {
        _continue.Observe("youtube", new PageSnapshot("https://www.youtube.com/watch?v=abc", "A video", null, 125, 600));
        var entry = Assert.Single(_continue.Entries);

        await _dispatcher.DispatchAsync(new LaunchCommand("youtube", "nope"));
        await _dispatcher.DispatchAsync(new LaunchCommand("prime", entry.Id));

        // Leaving YouTube for Prime pauses it; it stays open in the background.
        Assert.Equal(["launch youtube", "pause youtube", "launch prime"], _platform.Calls);
    }

    [Fact]
    public async Task Seek_goes_to_the_media_session()
    {
        await _dispatcher.DispatchAsync(new SeekCommand(42.5));
        await _dispatcher.DispatchAsync(new SeekCommand(-3));

        Assert.Equal(["seek 42.5", "seek 0"], _platform.Calls);
    }

    [Fact]
    public async Task Dpad_goes_to_launcher_page_when_launcher_is_in_front()
    {
        using var subscription = _session.Subscribe();

        await _dispatcher.DispatchAsync(new KeyCommand(RemoteKey.Right));

        Assert.Empty(_platform.Calls);
        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal(RemoteKey.Right, Assert.IsType<LauncherKeyEvent>(evt).Key);
    }

    [Fact]
    public async Task Volume_always_goes_to_the_os()
    {
        await _dispatcher.DispatchAsync(new KeyCommand(RemoteKey.VolumeUp));

        Assert.Equal(["key VolumeUp"], _platform.Calls);
    }

    [Fact]
    public async Task Launch_then_keys_are_injected_then_home_returns_to_launcher()
    {
        await _dispatcher.DispatchAsync(new LaunchCommand("youtube"));
        Assert.Equal("youtube", _session.ForegroundAppId);

        await _dispatcher.DispatchAsync(new KeyCommand(RemoteKey.Ok));
        await _dispatcher.DispatchAsync(new HomeCommand());

        Assert.Equal(["launch youtube", "key Ok", "pause youtube", "launcher"], _platform.Calls);
        Assert.True(_session.LauncherInFront);
    }

    [Fact]
    public async Task Unknown_app_is_ignored()
    {
        await _dispatcher.DispatchAsync(new LaunchCommand("netflix"));

        Assert.Empty(_platform.Calls);
        Assert.True(_session.LauncherInFront);
    }

    [Fact]
    public async Task Oversized_text_is_dropped()
    {
        await _dispatcher.DispatchAsync(new TextCommand(new string('a', CommandDispatcher.MaxTextLength + 1)));
        await _dispatcher.DispatchAsync(new TextCommand("stranger things"));

        Assert.Equal(["type stranger things"], _platform.Calls);
    }

    [Fact]
    public async Task Mouse_deltas_are_clamped()
    {
        await _dispatcher.DispatchAsync(new MouseMoveCommand(1_000_000, -5));

        Assert.Equal(["move 2000,-5"], _platform.Calls);
    }

    [Theory]
    [InlineData("""{"type":"key","key":"playPause"}""", typeof(KeyCommand))]
    [InlineData("""{"type":"mouseMove","dx":3,"dy":-4}""", typeof(MouseMoveCommand))]
    [InlineData("""{"type":"click","button":"right"}""", typeof(ClickCommand))]
    [InlineData("""{"type":"text","text":"hi"}""", typeof(TextCommand))]
    [InlineData("""{"type":"launch","app":"prime"}""", typeof(LaunchCommand))]
    [InlineData("""{"type":"home"}""", typeof(HomeCommand))]
    [InlineData("""{"type":"sleep"}""", typeof(SleepCommand))]
    [InlineData("""{"type":"launch","app":"youtube","continue":"0123456789ab"}""", typeof(LaunchCommand))]
    [InlineData("""{"type":"seek","position":12.5}""", typeof(SeekCommand))]
    [InlineData("""{"type":"close","app":"prime"}""", typeof(CloseCommand))]
    [InlineData("""{"type":"split","apps":["youtube","prime"]}""", typeof(SplitCommand))]
    public void Parses_wire_format(string json, Type expected) =>
        Assert.IsType(expected, RemoteCommand.TryParse(json));

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"format-c"}""")]
    [InlineData("""{"key":"up"}""")]
    [InlineData("""{"type":"key","key":"selfDestruct"}""")]
    public void Rejects_bad_messages(string json) => Assert.Null(RemoteCommand.TryParse(json));

    private sealed class RecordingPlatform : IInput, IPower, IMediaSession
    {
        public List<string> Calls { get; } = [];

        public void PressKey(RemoteKey key) => Calls.Add($"key {key}");
        public void MoveMouse(int dx, int dy) => Calls.Add($"move {dx},{dy}");
        public void Click(MouseButton button) => Calls.Add($"click {button}");
        public void Scroll(int dy) => Calls.Add($"scroll {dy}");
        public void TypeText(string text) => Calls.Add($"type {text}");

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
        public Task<MediaReading?> ReadAsync(CancellationToken ct = default) => Task.FromResult<MediaReading?>(null);

        public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
        {
            Calls.Add($"seek {position.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.FromResult(true);
        }

        public void Sleep() => Calls.Add("sleep");
        public void Restart() => Calls.Add("restart");
    }
}
