using System.Security.Cryptography;
using System.Text;
using HomeTV.Core.Session;

namespace HomeTV.Core.Pairing;

public enum PairOutcome
{
    Paired,
    WrongPin,

    /// <summary>No PIN is showing on the TV (never requested, expired, or burned by too many wrong guesses).</summary>
    NoActivePin,
}

public sealed record PairResult(PairOutcome Outcome, PairedDevice? Device = null, string? Token = null);

public sealed record PinRequestResult(bool Issued, TimeSpan RetryAfter = default);

/// <summary>
/// PIN pairing: a phone asks for a PIN, the TV shows it, the phone types it back and gets a long-lived token.
/// A PIN dies after <see cref="MaxAttemptsPerPin"/> wrong guesses or <see cref="PinLifetime"/>, and new PINs
/// can only be requested every <see cref="RequestCooldown"/>, so brute force is slow and visible on the TV.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(10);
    public const int MaxAttemptsPerPin = 5;
    public const int MaxDeviceNameLength = 40;

    private readonly Lock _lock = new();
    private readonly IPairingStore _store;
    private readonly TimeProvider _time;
    private readonly SessionState _session;
    private List<PairedDevice> _devices;

    private string? _pin;
    private DateTimeOffset _pinExpiresAt;
    private int _failedAttempts;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public PairingService(IPairingStore store, TimeProvider time, SessionState session)
    {
        _store = store;
        _time = time;
        _session = session;
        _devices = [.. store.Load()];
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_lock)
                return [.. _devices];
        }
    }

    public PinRequestResult RequestPin()
    {
        PinShownEvent shown;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var wait = _lastRequestAt + RequestCooldown - now;
            if (wait > TimeSpan.Zero)
                return new PinRequestResult(false, wait);

            _lastRequestAt = now;
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _pinExpiresAt = now + PinLifetime;
            _failedAttempts = 0;
            shown = new PinShownEvent(_pin, _pinExpiresAt);
        }

        _session.Publish(shown);
        return new PinRequestResult(true);
    }

    public PairResult TryPair(string pin, string deviceName)
    {
        PairResult result;
        bool pinBurned;
        lock (_lock)
        {
            if (_pin is null || _time.GetUtcNow() >= _pinExpiresAt)
            {
                _pin = null;
                return new PairResult(PairOutcome.NoActivePin);
            }

            if (!FixedTimeEquals(pin, _pin))
            {
                pinBurned = ++_failedAttempts >= MaxAttemptsPerPin;
                if (pinBurned)
                    _pin = null;
                result = new PairResult(PairOutcome.WrongPin);
            }
            else
            {
                var token = Base64Url(RandomNumberGenerator.GetBytes(32));
                var device = new PairedDevice(
                    Id: Base64Url(RandomNumberGenerator.GetBytes(9)),
                    Name: CleanName(deviceName),
                    TokenHash: Hash(token),
                    PairedAt: _time.GetUtcNow());

                _devices.Add(device);
                _store.Save(_devices);
                _pin = null;
                pinBurned = true;
                result = new PairResult(PairOutcome.Paired, device, token);
            }
        }

        if (pinBurned)
            _session.Publish(new PinHiddenEvent());
        return result;
    }

    public PairedDevice? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var hash = Hash(token);
        lock (_lock)
            return _devices.FirstOrDefault(d => d.TokenHash == hash);
    }

    public PairedDevice? Find(string deviceId)
    {
        lock (_lock)
            return _devices.FirstOrDefault(d => d.Id == deviceId);
    }

    /// <summary>Sets a phone's touchpad sensitivity, clamped to the allowed range. False if it isn't paired.</summary>
    public bool SetPointerSpeed(string deviceId, double speed)
    {
        if (!double.IsFinite(speed))
            return false;

        lock (_lock)
        {
            var index = _devices.FindIndex(d => d.Id == deviceId);
            if (index < 0)
                return false;
            _devices[index] = _devices[index] with
            {
                PointerSpeed = Math.Round(Math.Clamp(speed, PairedDevice.MinPointerSpeed, PairedDevice.MaxPointerSpeed), 2),
            };
            _store.Save(_devices);
            return true;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.Id == deviceId) == 0)
                return false;
            _store.Save(_devices);
            return true;
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CleanName(string name)
    {
        var trimmed = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (trimmed.Length == 0)
            return "Phone";
        return trimmed.Length > MaxDeviceNameLength ? trimmed[..MaxDeviceNameLength] : trimmed;
    }
}
