using System.Text.Json;

namespace HomeTV.Core.Pairing;

public interface IPairingStore
{
    IReadOnlyList<PairedDevice> Load();
    void Save(IReadOnlyList<PairedDevice> devices);
}

public sealed class InMemoryPairingStore : IPairingStore
{
    private IReadOnlyList<PairedDevice> _devices = [];

    public IReadOnlyList<PairedDevice> Load() => _devices;

    public void Save(IReadOnlyList<PairedDevice> devices) => _devices = [.. devices];
}

public sealed class JsonFilePairingStore(string path) : IPairingStore
{
    public IReadOnlyList<PairedDevice> Load()
    {
        if (!File.Exists(path))
            return [];
        return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path), HomeTvJson.Options) ?? [];
    }

    public void Save(IReadOnlyList<PairedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write-then-rename so a crash mid-write can't wipe every pairing.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(devices, HomeTvJson.Options));
        File.Move(temp, path, overwrite: true);
    }
}
