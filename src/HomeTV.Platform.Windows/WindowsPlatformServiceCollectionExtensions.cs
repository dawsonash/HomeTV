using HomeTV.Core.NowPlaying;
using HomeTV.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace HomeTV.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services) =>
        services
            .AddSingleton<LauncherWindow>()
            .AddSingleton<HomeShortcuts>()
            .AddSingleton<IMediaSession, WindowsMediaSession>()
            .AddSingleton<IInput, WindowsInput>()
            .AddSingleton<ITextFocus, WindowsTextFocus>()
            .AddSingleton<IAppHost, WindowsAppHost>()
            .AddSingleton<IPower, WindowsPower>()
            .AddSingleton<IAudioDevices, WindowsAudioDevices>();
}
