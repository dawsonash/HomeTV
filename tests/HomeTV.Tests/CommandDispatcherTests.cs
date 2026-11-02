using HomeTV.Core.Apps;
using HomeTV.Core.Platform;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class CommandDispatcherTests
{
    private readonly RecordingPlatform _platform = new();
    private readonly SessionState _session = new();
    private readonly CommandDispatcher _dispatcher;

    public CommandDispatcherTests()
    {
        var apps = new AppRegistry([
            new AppDefinition { Id = "youtube", Name = "YouTube", Kind = AppKind.Edge, Target = "https://www.youtube.com/tv" },
        ]);
        _dispatcher = new CommandDispatcher(_platform, _platform, _platform, apps, _session, NullLogger<CommandDispatcher>.Instance);
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

        Assert.Equal(["launch youtube", "key Ok", "home"], _platform.Calls);
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
    public void Parses_wire_format(string json, Type expected) =>
        Assert.IsType(expected, RemoteCommand.TryParse(json));

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"format-c"}""")]
    [InlineData("""{"key":"up"}""")]
    [InlineData("""{"type":"key","key":"selfDestruct"}""")]
    public void Rejects_bad_messages(string json) => Assert.Null(RemoteCommand.TryParse(json));

    private sealed class RecordingPlatform : IInput, IAppHost, IPower
    {
        public List<string> Calls { get; } = [];

        public void PressKey(RemoteKey key) => Calls.Add($"key {key}");
        public void MoveMouse(int dx, int dy) => Calls.Add($"move {dx},{dy}");
        public void Click(MouseButton button) => Calls.Add($"click {button}");
        public void Scroll(int dy) => Calls.Add($"scroll {dy}");
        public void TypeText(string text) => Calls.Add($"type {text}");

        public Task LaunchAsync(AppDefinition app, CancellationToken ct = default)
        {
            Calls.Add($"launch {app.Id}");
            return Task.CompletedTask;
        }

        public Task ReturnHomeAsync(CancellationToken ct = default)
        {
            Calls.Add("home");
            return Task.CompletedTask;
        }

        public void OpenDesktop() => Calls.Add("desktop");
        public event Action<string>? AppExited { add { } remove { } }
        public void Sleep() => Calls.Add("sleep");
    }
}
