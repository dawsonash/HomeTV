using HomeTV.Core.Apps;
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
    public Task LaunchAsync(AppDefinition app, CancellationToken ct = default)
    {
        logger.LogInformation("Launch {App} ({Kind}) → {Target}", app.Name, app.Kind, app.Target);
        return Task.CompletedTask;
    }

    public Task ReturnHomeAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Return home");
        return Task.CompletedTask;
    }

    public void OpenDesktop() => logger.LogInformation("Open desktop");

    // Fake apps never exit on their own.
    public event Action<string>? AppExited { add { } remove { } }
}

public sealed class FakePower(ILogger<FakePower> logger) : IPower
{
    public void Sleep() => logger.LogInformation("Sleep");
}

public static class FakePlatformServiceCollectionExtensions
{
    public static IServiceCollection AddFakePlatform(this IServiceCollection services) =>
        services
            .AddSingleton<IInput, FakeInput>()
            .AddSingleton<IAppHost, FakeAppHost>()
            .AddSingleton<IPower, FakePower>();
}
