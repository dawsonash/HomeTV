using System.Text.Json;
using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging;

namespace HomeTV.Core.Audio;

/// <summary>One output as the launcher shows it.</summary>
/// <param name="Available">Can be picked now: its device is connected and, if it needs the TV, the TV is set up.</param>
public sealed record AudioOutputInfo(string Id, string Name, AudioIcon Icon, bool Available);

/// <summary>
/// Switches sound between the outputs in <c>audio.json</c> (TV speakers, a soundbar on the TV's ARC port, a Bluetooth
/// speaker) and tells the launcher which one is playing.
/// </summary>
/// <remarks>
/// Windows only sees the default playback device, so outputs that share one (TV speakers and the soundbar both play
/// what comes down the HDMI cable) are told apart by remembering which of them was picked last. Changing it with the
/// TV's own remote isn't noticed.
/// </remarks>
public sealed class AudioOutputService(
    AudioConfig config,
    IAudioDevices devices,
    ITvControl tv,
    SessionState session,
    TimeProvider time,
    ILogger<AudioOutputService> logger,
    string? statePath = null) : IDisposable
{
    /// <summary>How often to look for a Bluetooth speaker (dis)connecting, or the default device changing elsewhere.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private AudioEvent? _current;
    private string? _lastPicked = LoadLastPicked(statePath);

    public bool Enabled => config.Outputs.Count > 0;

    /// <summary>What to send a launcher that just connected.</summary>
    public AudioEvent Current
    {
        get
        {
            lock (_lock)
                return _current ??= Read();
        }
    }

    /// <summary>Picks up outside changes every few seconds until disposed.</summary>
    public void Start()
    {
        if (!Enabled)
            return;
        // For filling in audio.json's "device" names. Windows remembers every monitor ever plugged in, so skip the
        // disconnected ones unless an output is looking for them.
        foreach (var device in devices.List())
        {
            var wanted = config.Outputs.Any(o => device.Name.Contains(o.Device, StringComparison.OrdinalIgnoreCase));
            if (device.Active || wanted)
                logger.LogInformation("Playback device: {Name}{Inactive}", device.Name, device.Active ? "" : " (not connected)");
        }
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(RefreshInterval, time);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try { Refresh(); }
                catch (Exception ex) { logger.LogWarning(ex, "Couldn't read the playback devices"); }
            }
        });
    }

    /// <summary>Re-reads the devices and tells the launcher if anything changed.</summary>
    public void Refresh()
    {
        var next = Read();
        lock (_lock)
        {
            if (_current is not null && Same(_current, next))
                return;
            _current = next;
        }
        session.Publish(next);
    }

    /// <summary>Moves to the next available output after the current one, in config order.</summary>
    /// <returns>Null on success, otherwise why it didn't switch.</returns>
    public Task<string?> NextAsync(CancellationToken ct = default) =>
        SwitchAsync(state =>
        {
            // Chosen once the previous switch is done, so pressing twice quickly moves on twice.
            var outputs = state.Outputs;
            var from = outputs.ToList().FindIndex(o => o.Id == state.Current);
            for (var step = 1; step <= outputs.Count; step++)
            {
                var candidate = outputs[(from + step + outputs.Count) % outputs.Count];
                if (candidate.Available && candidate.Id != state.Current)
                    return candidate.Id;
            }
            return null;
        }, ct);

    /// <returns>Null on success, otherwise why it didn't switch.</returns>
    public Task<string?> SelectAsync(string id, CancellationToken ct = default) => SwitchAsync(_ => id, ct);

    private async Task<string?> SwitchAsync(Func<AudioEvent, string?> choose, CancellationToken ct)
    {
        // One switch at a time: TV commands take a moment, and rapid presses should land in order.
        await _switching.WaitAsync(ct);
        AudioOutputConfig? output = null;
        try
        {
            if (choose(Read()) is not { } id)
                return "Nothing else to switch to";
            output = config.Outputs.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (output is null)
                return "Unknown output";

            if (FindDevice(output, devices.List()) is not { Active: true } device)
                return $"{output.Name} isn't connected";

            if (output.TvCommand is { } command)
            {
                if (!tv.IsConfigured)
                    return "Set up the TV first (tv.host in audio.json)";
                try
                {
                    await tv.RunAsync(command, ct);
                }
                catch (TvControlException ex)
                {
                    logger.LogWarning("Couldn't switch the TV to {Output}: {Reason}", output.Name, ex.Message);
                    return ex.Message;
                }
            }

            if (devices.DefaultId() != device.Id)
                devices.SetDefault(device.Id);

            _lastPicked = output.Id;
            SaveLastPicked();
            logger.LogInformation("Sound now plays through {Output} ({Device})", output.Name, device.Name);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Couldn't switch sound to {Output}", output?.Name);
            return $"Couldn't switch to {output?.Name ?? "that"}";
        }
        finally
        {
            _switching.Release();
            Refresh();
        }
    }

    private AudioEvent Read()
    {
        var all = devices.List();
        var defaultId = devices.DefaultId();

        var outputs = config.Outputs
            .Select(o => new AudioOutputInfo(o.Id, o.Name, o.Icon,
                FindDevice(o, all) is { Active: true } && (o.TvCommand is null || tv.IsConfigured)))
            .ToList();

        // The outputs on the default device; when several share it, the one picked last (or else the first).
        var onDefault = config.Outputs.Where(o => FindDevice(o, all)?.Id is { } id && id == defaultId).ToList();
        var current = onDefault.FirstOrDefault(o => o.Id == _lastPicked) ?? onDefault.FirstOrDefault();
        return new AudioEvent(outputs, current?.Id);
    }

    /// <summary>The device whose name contains the output's <see cref="AudioOutputConfig.Device"/>, preferring a connected one.</summary>
    private static AudioDevice? FindDevice(AudioOutputConfig output, IReadOnlyList<AudioDevice> all) =>
        all.Where(d => d.Name.Contains(output.Device, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Active)
            .FirstOrDefault();

    private static bool Same(AudioEvent a, AudioEvent b) => a.Current == b.Current && a.Outputs.SequenceEqual(b.Outputs);

    private static string? LoadLastPicked(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path), HomeTvJson.Options)?.Output
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void SaveLastPicked()
    {
        if (statePath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, JsonSerializer.Serialize(new SavedState(_lastPicked), HomeTvJson.Options));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't save the audio output");
        }
    }

    private sealed record SavedState(string? Output);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
