using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.NowPlaying;
using HomeTV.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeTV.Platform.Fake;

/// <summary>Logs what would happen on the TV box. Used when developing on the Mac.</summary>
public sealed class FakeInput(ILogger<FakeInput> logger) : IInput
{
    public void PressKey(RemoteKey key) => logger.LogInformation("Key {Key}", key);

    public void MoveMouse(int dx, int dy) => logger.LogDebug("Mouse move {Dx},{Dy}", dx, dy);

    public void Click(MouseButton button) => logger.LogInformation("Click {Button}", button);

    public void Scroll(int dy) => logger.LogDebug("Scroll {Dy}", dy);

    public void TypeText(string text) => logger.LogInformation("Type {Text}", text);
}

public sealed class FakeAppHost(ILogger<FakeAppHost> logger) : IAppHost
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    public Task LaunchAsync(AppDefinition app, string? url = null, CancellationToken ct = default)
    {
        logger.LogInformation("Launch {App} ({Kind}) → {Target}", app.Name, app.Kind, url ?? app.Target);
        lock (_running)
            _running.Add(app.Id);
        return Task.CompletedTask;
    }

    public Task<bool> WaitForWindowAsync(string appId, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(IsRunning(appId));

    public Task<bool> ShowAsync(string appId, CancellationToken ct = default)
    {
        logger.LogInformation("Show {App}", appId);
        return Task.FromResult(IsRunning(appId));
    }

    public Task ShowLauncherAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Show launcher");
        return Task.CompletedTask;
    }

    public Task CloseAsync(string appId, CancellationToken ct = default)
    {
        logger.LogInformation("Close {App}", appId);
        lock (_running)
            _running.Remove(appId);
        return Task.CompletedTask;
    }

    public Task PauseAsync(string appId, CancellationToken ct = default)
    {
        logger.LogInformation("Pause {App}", appId);
        return Task.CompletedTask;
    }

    public Task ArrangeAsync(IReadOnlyList<string> appIds, string active, CancellationToken ct = default)
    {
        logger.LogInformation("Tile {Apps}, {Active} active", string.Join(", ", appIds), active);
        return Task.CompletedTask;
    }

    public void OpenDesktop() => logger.LogInformation("Open desktop");

    public Task<bool?> CanScrollAtPointerAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);

    private bool IsRunning(string appId)
    {
        lock (_running)
            return _running.Contains(appId);
    }

    // Fake apps never exit on their own, and nobody clicks into them.
    public event Action<string>? AppExited { add { } remove { } }

    public event Action<string>? AppActivated { add { } remove { } }

    // Fake apps have no pages to look at.
    public event Action<string, PageSnapshot>? PageObserved { add { } remove { } }
}

public sealed class FakePower(ILogger<FakePower> logger) : IPower
{
    public void Sleep() => logger.LogInformation("Sleep");

    public void Restart() => logger.LogInformation("Restart PC");
}

/// <summary>Nothing ever plays on the Mac; seeking just logs.</summary>
public sealed class FakeMediaSession(ILogger<FakeMediaSession> logger) : IMediaSession
{
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public event Action? Changed { add { } remove { } }

    public Task<MediaReading?> ReadAsync(CancellationToken ct = default) => Task.FromResult<MediaReading?>(null);

    public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        logger.LogInformation("Seek to {Position}", position);
        return Task.FromResult(false);
    }
}

/// <summary>Nothing on the Mac ever takes focus.</summary>
public sealed class FakeTextFocus : ITextFocus
{
    public void Start() { }

    public TextField? Current => null;

    public event Action<TextField?>? Changed { add { } remove { } }

    public async Task<TextField?> FocusAfterAsync(Func<Task> action, TimeSpan timeout, CancellationToken ct = default)
    {
        await action();
        return null;
    }
}

/// <summary>The TV's HDMI audio and a Bluetooth speaker, as the box would see them. Switching just logs.</summary>
public sealed class FakeAudioDevices(ILogger<FakeAudioDevices> logger) : IAudioDevices
{
    private readonly AudioDevice[] _devices =
    [
        new("hdmi", "HISENSE (NVIDIA High Definition Audio)", true),
        new("echo", "Speakers (Echo Studio-1AB)", true),
    ];

    private string _default = "hdmi";

    public IReadOnlyList<AudioDevice> List() => _devices;

    public string? DefaultId() => _default;

    public void SetDefault(string id)
    {
        logger.LogInformation("Default playback device → {Device}", id);
        _default = id;
    }
}

/// <summary>Logs the commands it would send the TV.</summary>
public sealed class FakeTvControl(ILogger<FakeTvControl> logger) : ITvControl
{
    public bool IsConfigured => true;

    public Task RunAsync(string command, CancellationToken ct = default)
    {
        logger.LogInformation("TV: {Command}", command);
        return Task.CompletedTask;
    }
}

public static class FakePlatformServiceCollectionExtensions
{
    public static IServiceCollection AddFakePlatform(this IServiceCollection services) =>
        services
            .AddSingleton<IMediaSession, FakeMediaSession>()
            .AddSingleton<IInput, FakeInput>()
            .AddSingleton<ITextFocus, FakeTextFocus>()
            .AddSingleton<IAppHost, FakeAppHost>()
            .AddSingleton<IPower, FakePower>()
            .AddSingleton<IAudioDevices, FakeAudioDevices>()
            .AddSingleton<ITvControl, FakeTvControl>();
}
