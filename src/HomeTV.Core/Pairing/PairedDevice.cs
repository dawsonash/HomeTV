namespace HomeTV.Core.Pairing;

/// <summary>A phone that has paired with the box. Only a hash of its token is kept.</summary>
/// <param name="PointerSpeed">
/// Touchpad sensitivity for this phone, as a multiplier on what the remote sends. Set from the launcher's
/// settings screen; files written before this existed load as 1.
/// </param>
public sealed record PairedDevice(string Id, string Name, string TokenHash, DateTimeOffset PairedAt, double PointerSpeed = 1.0)
{
    public const double MinPointerSpeed = 0.2;
    public const double MaxPointerSpeed = 3.0;
}
