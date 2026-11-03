using HomeTV.Core.Pairing;
using HomeTV.Core.Session;

namespace HomeTV.Tests;

public class PairingServiceTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryPairingStore _store = new();
    private readonly SessionState _session = new();
    private readonly PairingService _pairing;

    public PairingServiceTests() => _pairing = new PairingService(_store, _time, _session);

    [Fact]
    public void Correct_pin_pairs_and_token_authenticates()
    {
        var pin = RequestPin();

        var result = _pairing.TryPair(pin, "Dawson's phone");

        Assert.Equal(PairOutcome.Paired, result.Outcome);
        Assert.Equal("Dawson's phone", result.Device!.Name);
        Assert.Equal(result.Device, _pairing.Authenticate(result.Token));
        Assert.Single(_store.Load());
    }

    [Fact]
    public void Store_never_holds_the_raw_token()
    {
        var result = _pairing.TryPair(RequestPin(), "phone");

        Assert.DoesNotContain(_store.Load(), d => d.TokenHash == result.Token);
    }

    [Fact]
    public void Pin_is_single_use()
    {
        var pin = RequestPin();
        _pairing.TryPair(pin, "first");

        Assert.Equal(PairOutcome.NoActivePin, _pairing.TryPair(pin, "second").Outcome);
    }

    [Fact]
    public void Pin_expires()
    {
        var pin = RequestPin();
        _time.Advance(PairingService.PinLifetime);

        Assert.Equal(PairOutcome.NoActivePin, _pairing.TryPair(pin, "phone").Outcome);
    }

    [Fact]
    public void Too_many_wrong_guesses_burn_the_pin()
    {
        var pin = RequestPin();
        var wrong = pin == "000000" ? "111111" : "000000";

        for (var i = 0; i < PairingService.MaxAttemptsPerPin; i++)
            Assert.Equal(PairOutcome.WrongPin, _pairing.TryPair(wrong, "attacker").Outcome);

        Assert.Equal(PairOutcome.NoActivePin, _pairing.TryPair(pin, "phone").Outcome);
    }

    [Fact]
    public void Pin_requests_are_rate_limited()
    {
        RequestPin();

        var second = _pairing.RequestPin();
        Assert.False(second.Issued);
        Assert.True(second.RetryAfter > TimeSpan.Zero);

        _time.Advance(PairingService.RequestCooldown);
        Assert.True(_pairing.RequestPin().Issued);
    }

    [Fact]
    public void Revoked_device_no_longer_authenticates()
    {
        var result = _pairing.TryPair(RequestPin(), "phone");

        Assert.True(_pairing.Revoke(result.Device!.Id));
        Assert.Null(_pairing.Authenticate(result.Token));
        Assert.Empty(_store.Load());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Unknown_tokens_are_rejected(string? token) => Assert.Null(_pairing.Authenticate(token));

    [Fact]
    public void Device_names_are_cleaned()
    {
        var blank = _pairing.TryPair(RequestPin(), "  \n ");
        _time.Advance(PairingService.RequestCooldown);
        var longName = _pairing.TryPair(RequestPin(), new string('x', 100));

        Assert.Equal("Phone", blank.Device!.Name);
        Assert.Equal(PairingService.MaxDeviceNameLength, longName.Device!.Name.Length);
    }

    [Fact]
    public void Pin_is_shown_on_request_and_hidden_after_pairing()
    {
        using var subscription = _session.Subscribe();

        var pin = RequestPin();
        _pairing.TryPair(pin, "phone");

        Assert.True(subscription.Reader.TryRead(out var shown));
        Assert.Equal(pin, Assert.IsType<PinShownEvent>(shown).Pin);
        Assert.True(subscription.Reader.TryRead(out var hidden));
        Assert.IsType<PinHiddenEvent>(hidden);
    }

    [Fact]
    public void Pointer_speed_defaults_to_1_and_is_saved_clamped()
    {
        var device = _pairing.TryPair(RequestPin(), "phone").Device!;
        Assert.Equal(1.0, device.PointerSpeed);

        Assert.True(_pairing.SetPointerSpeed(device.Id, 1.5));
        Assert.Equal(1.5, _store.Load().Single().PointerSpeed);

        Assert.True(_pairing.SetPointerSpeed(device.Id, 99));
        Assert.Equal(PairedDevice.MaxPointerSpeed, _pairing.Find(device.Id)!.PointerSpeed);

        Assert.False(_pairing.SetPointerSpeed(device.Id, double.NaN));
        Assert.False(_pairing.SetPointerSpeed("nope", 1));
    }

    [Fact]
    public void Pairing_files_from_before_pointer_speed_load_as_1()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hometv-pairing-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """[{"id":"a","name":"phone","tokenHash":"x","pairedAt":"2026-11-01T00:00:00Z"}]""");
        try
        {
            Assert.Equal(1.0, new JsonFilePairingStore(path).Load().Single().PointerSpeed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.3)]
    public void Pointer_scaler_keeps_slow_drags_moving(double speed)
    {
        var scaler = new HomeTV.Core.Remote.PointerScaler();
        var total = 0;
        for (var i = 0; i < 10; i++)
            total += scaler.Scale(new HomeTV.Core.Remote.MouseMoveCommand(1, 0), speed).Dx;

        Assert.Equal((int)Math.Round(10 * speed), total);
    }

    private string RequestPin()
    {
        using var subscription = _session.Subscribe();
        Assert.True(_pairing.RequestPin().Issued);
        Assert.True(subscription.Reader.TryRead(out var evt));
        var pin = Assert.IsType<PinShownEvent>(evt).Pin;
        Assert.Matches("^[0-9]{6}$", pin);
        return pin;
    }
}
