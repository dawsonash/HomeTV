namespace HomeTV.Core.Pairing;

/// <summary>A phone that has paired with the box. Only a hash of its token is kept.</summary>
public sealed record PairedDevice(string Id, string Name, string TokenHash, DateTimeOffset PairedAt);
