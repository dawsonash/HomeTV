using System.Text.Json;

namespace HomeTV.Core.Audio;

/// <summary>
/// Where sound can come out, as declared in <c>config/audio.json</c>, in the order the launcher's audio button
/// cycles through them.
/// </summary>
/// <param name="Tv">How to reach the TV, for outputs that need it to switch (TV speakers vs a soundbar on ARC).</param>
public sealed record AudioConfig(IReadOnlyList<AudioOutputConfig>? Outputs = null, TvConfig? Tv = null)
{
    public static readonly AudioConfig Empty = new();

    public IReadOnlyList<AudioOutputConfig> Outputs { get; init; } = Outputs ?? [];

    public static AudioConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<AudioConfig>(json, HomeTvJson.Options) ?? Empty;
        foreach (var output in config.Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Id) || string.IsNullOrWhiteSpace(output.Name) || string.IsNullOrWhiteSpace(output.Device))
                throw new InvalidOperationException($"Audio config: every output needs an id, a name and a device. Got {output}.");
        }
        if (config.Outputs.Select(o => o.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Outputs.Count)
            throw new InvalidOperationException("Audio config: output ids must be unique.");
        return config;
    }

    /// <summary>A missing file means no audio button.</summary>
    public static AudioConfig Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;
}

/// <param name="Id">e.g. <c>soundbar</c>.</param>
/// <param name="Name">Shown on the TV, e.g. "Soundbar".</param>
/// <param name="Device">
/// Part of the Windows playback device's name (Settings → System → Sound), case-insensitive. Outputs that share a
/// device (the TV's speakers and a soundbar on its ARC port both play whatever comes down the PC's HDMI cable) are
/// told apart by <paramref name="TvCommand"/>.
/// </param>
/// <param name="TvCommand">A shell command run on the TV (over ADB) to make it play through this output.</param>
public sealed record AudioOutputConfig(string Id, string Name, string Device, AudioIcon Icon = AudioIcon.Speaker, string? TvCommand = null);

public enum AudioIcon
{
    Tv,
    Soundbar,
    Speaker,
}

/// <summary>A Fire TV (or other Android TV) with network debugging on.</summary>
/// <param name="Host">The TV's IP address. Empty until set up: outputs with a TV command stay unavailable.</param>
/// <param name="Port">ADB's network port; Fire TV uses 5555.</param>
/// <param name="Adb">Path to adb.exe, or just <c>adb</c> if it's on PATH (winget's Google.PlatformTools puts it there).</param>
public sealed record TvConfig(string Host = "", int Port = 5555, string Adb = "adb");
