namespace HomeTV.Core.Platform;

/// <summary>A playback device the OS knows about (on Windows, one row of Settings → System → Sound).</summary>
/// <param name="Id">The OS's id for it; stable across restarts.</param>
/// <param name="Name">e.g. "HISENSE (NVIDIA High Definition Audio)", "Speakers (Echo Studio-1AB)".</param>
/// <param name="Active">Plugged in or connected, so sound can go to it. A Bluetooth speaker that's out of range isn't.</param>
public sealed record AudioDevice(string Id, string Name, bool Active);

/// <summary>The OS's playback devices, and which one is the default that apps play through.</summary>
public interface IAudioDevices
{
    IReadOnlyList<AudioDevice> List();

    /// <summary>The default playback device's id, or null if there's none.</summary>
    string? DefaultId();

    /// <summary>Makes <paramref name="id"/> the default for everything (media, games, calls).</summary>
    void SetDefault(string id);
}

/// <summary>Runs commands on the TV itself, e.g. to switch it between its own speakers and a soundbar.</summary>
public interface ITvControl
{
    /// <summary>False until the TV's address is set up; outputs needing the TV are unavailable meanwhile.</summary>
    bool IsConfigured { get; }

    /// <summary>Runs <paramref name="command"/> on the TV. Throws <see cref="TvControlException"/> with a readable reason.</summary>
    Task RunAsync(string command, CancellationToken ct = default);
}

public sealed class TvControlException(string message) : Exception(message);
