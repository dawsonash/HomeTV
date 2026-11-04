using Microsoft.Win32;

namespace HomeTV.Platform.Windows;

/// <summary>A Steam game that's running, and the window it plays in.</summary>
internal sealed record SteamGame(int AppId, string Name, TopLevelWindow Window);

/// <summary>
/// Spots a game started from Steam (Big Picture, or Steam itself), so HomeTV can treat it as an app: switch back to
/// it, tile it, close it. Steam writes the running game's id to <c>HKCU\Software\Valve\Steam\RunningAppID</c> (0
/// when none), and its name under <c>Apps\&lt;id&gt;</c>. The game's window is the one whose exe is installed under a
/// <c>steamapps\common</c> folder.
/// </summary>
internal static class SteamGames
{
    private const string SteamKey = @"Software\Valve\Steam";

    /// <summary>The game Steam says is running, with its window once it has one; null if none (or no window yet).</summary>
    public static SteamGame? Running()
    {
        if (RunningAppId() is not { } appId)
            return null;
        if (FindWindow() is not { } window)
            return null;
        return new SteamGame(appId, Name(appId) ?? (window.Title.Length > 0 ? window.Title : $"Steam game {appId}"), window);
    }

    private static int? RunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
        return key?.GetValue("RunningAppID") is int id && id > 0 ? id : null;
    }

    private static string? Name(int appId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{SteamKey}\Apps\{appId}");
        return key?.GetValue("Name") is string name && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    /// <summary>A game window: in front if one is, else the first found.</summary>
    private static TopLevelWindow? FindWindow()
    {
        var games = new List<TopLevelWindow>();
        foreach (var window in TopLevelWindows.All())
        {
            if (ProcessInfo.ImagePath(window.ProcessId) is { } path
                && path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
                games.Add(window with { ProcessName = Path.GetFileNameWithoutExtension(path) });
        }
        if (games.Count == 0)
            return null;
        var front = NativeMethods.GetForegroundWindow();
        var focused = games.FindIndex(w => w.Handle == front);
        return games[focused >= 0 ? focused : 0];
    }
}
