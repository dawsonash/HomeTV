using HomeTV.Core.Audio;
using HomeTV.Core.Platform;
using HomeTV.Core.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeTV.Tests;

public class AudioOutputServiceTests : IDisposable
{
    private readonly FakeDevices _devices = new();
    private readonly FakeTv _tv = new();
    private readonly SessionState _session = new();
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"hometv-audio-{Guid.NewGuid():N}.json");

    private static readonly AudioConfig Config = new(
    [
        new("soundbar", "Soundbar", "HISENSE", AudioIcon.Soundbar, "sam 1"),
        new("echo", "Echo Studio", "Echo Studio", AudioIcon.Speaker),
        new("tv", "TV speakers", "HISENSE", AudioIcon.Tv, "sam 0"),
    ]);

    private AudioOutputService Service() =>
        new(Config, _devices, _tv, _session, TimeProvider.System, NullLogger<AudioOutputService>.Instance, _statePath);

    public void Dispose() => File.Delete(_statePath);

    [Fact]
    public void Ships_a_valid_config()
    {
        var config = AudioConfig.Load(Path.Combine(AppContext.BaseDirectory, "audio.json"));
        Assert.Equal(["soundbar", "echo"], config.Outputs.Select(o => o.Id));
        // Neither needs the TV: the soundbar is whatever Windows plays over HDMI, the Echo is Bluetooth.
        Assert.All(config.Outputs, o => Assert.Null(o.TvCommand));
    }

    [Fact]
    public async Task Cycles_soundbar_echo_tv_and_back()
    {
        var service = Service();
        Assert.Equal("soundbar", service.Current.Current); // HDMI is the default and soundbar is listed first.

        Assert.Null(await service.NextAsync());
        Assert.Equal("echo", service.Current.Current);
        Assert.Equal("echo-id", _devices.Default);

        Assert.Null(await service.NextAsync());
        Assert.Equal("tv", service.Current.Current);
        Assert.Equal("hdmi-id", _devices.Default);
        Assert.Equal(["sam 0"], _tv.Commands);

        Assert.Null(await service.NextAsync());
        Assert.Equal("soundbar", service.Current.Current);
        Assert.Equal(["sam 0", "sam 1"], _tv.Commands);
    }

    [Fact]
    public async Task Skips_a_speaker_that_isnt_connected()
    {
        _devices.EchoConnected = false;
        var service = Service();

        Assert.False(service.Current.Outputs.Single(o => o.Id == "echo").Available);
        Assert.Null(await service.NextAsync());
        Assert.Equal("tv", service.Current.Current);
    }

    [Fact]
    public async Task Stays_put_when_the_tv_doesnt_answer()
    {
        var service = Service();
        await service.SelectAsync("echo");
        _tv.Fail = "The TV didn't answer (is it on?)";

        Assert.Equal("The TV didn't answer (is it on?)", await service.NextAsync());
        Assert.Equal("echo", service.Current.Current);
        Assert.Equal("echo-id", _devices.Default);
    }

    [Fact]
    public async Task Tv_outputs_are_unavailable_until_the_tv_is_set_up()
    {
        _tv.IsConfigured = false;
        var service = Service();

        Assert.Equal(["echo"], service.Current.Outputs.Where(o => o.Available).Select(o => o.Id));
        Assert.NotNull(await service.SelectAsync("tv"));
        Assert.Empty(_tv.Commands);
    }

    [Fact]
    public async Task Remembers_tv_speakers_vs_soundbar_across_restarts()
    {
        await Service().SelectAsync("tv");

        Assert.Equal("tv", Service().Current.Current);
    }

    [Fact]
    public void Shows_no_output_for_a_device_not_in_the_config()
    {
        _devices.Default = "headphones-id";
        Assert.Null(Service().Current.Current);
    }

    [Fact]
    public async Task Tells_the_launcher_when_it_switches()
    {
        var service = Service();
        _ = service.Current;
        using var subscription = _session.Subscribe();

        await service.SelectAsync("echo");

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal("echo", Assert.IsType<AudioEvent>(evt).Current);
    }

    private sealed class FakeDevices : IAudioDevices
    {
        public bool EchoConnected { get; set; } = true;
        public string Default { get; set; } = "hdmi-id";

        public IReadOnlyList<AudioDevice> List() =>
        [
            new("hdmi-id", "HISENSE (NVIDIA High Definition Audio)", true),
            new("echo-id", "Speakers (Echo Studio-1AB)", EchoConnected),
            new("headphones-id", "Headphones (Realtek Audio)", true),
        ];

        public string? DefaultId() => Default;

        public void SetDefault(string id) => Default = id;
    }

    private sealed class FakeTv : ITvControl
    {
        public bool IsConfigured { get; set; } = true;
        public string? Fail { get; set; }
        public List<string> Commands { get; } = [];

        public Task RunAsync(string command, CancellationToken ct = default)
        {
            if (Fail is not null)
                throw new TvControlException(Fail);
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
