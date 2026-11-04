using System.Text.Json;

namespace HomeTV.Core.Photos;

/// <summary>Which background photos the slideshow shows.</summary>
public enum PhotoMode
{
    All,
    People,
    Nature,

    /// <summary>Leaves out anything flagged as not safe for work, and anything not checked yet.</summary>
    SafeForWork,
}

/// <summary>
/// What's in a photo, from the on-box image model (<see cref="IPhotoClassifier"/>) or set by hand on a phone.
/// <paramref name="Version"/> is the file version the tags are for, so an edited file is looked at again.
/// </summary>
public sealed record PhotoTags(bool People, bool Nature, bool SafeForWork, bool Manual, string Version);

/// <summary>Slideshow settings, changed from the launcher's Settings or a phone.</summary>
public sealed record PhotoSettings(PhotoMode Mode = PhotoMode.All, int SlideSeconds = PhotoSettings.DefaultSlideSeconds)
{
    public const int DefaultSlideSeconds = 30;
    public const int MinSlideSeconds = 5;
    public const int MaxSlideSeconds = 600;

    public PhotoSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PhotoMode.All,
        SlideSeconds = Math.Clamp(SlideSeconds, MinSlideSeconds, MaxSlideSeconds),
    };
}

/// <summary>Looks at a photo and says whether it shows people or nature and whether it's safe for work.</summary>
public interface IPhotoClassifier
{
    /// <summary>False until the model is downloaded and loaded (or if it can't be).</summary>
    bool IsReady { get; }

    /// <summary>A short status for Settings: "Downloading the image model (40%)", an error, or null when fine.</summary>
    string? Status { get; }

    /// <summary>Tags for one image file. <see cref="PhotoTags.Version"/> is left empty for the caller to fill.</summary>
    Task<PhotoTags> ClassifyAsync(string imagePath, CancellationToken ct = default);
}

public static class PhotoFilter
{
    /// <summary>
    /// Whether a photo belongs in the slideshow in this mode. Photos the model hasn't seen yet (and videos, which it
    /// doesn't look at) only show in "All", unless someone has tagged them by hand.
    /// </summary>
    public static bool Matches(PhotoMode mode, PhotoTags? tags) => mode switch
    {
        PhotoMode.All => true,
        PhotoMode.People => tags?.People == true,
        PhotoMode.Nature => tags?.Nature == true,
        PhotoMode.SafeForWork => tags?.SafeForWork == true,
        _ => true,
    };
}

/// <summary>Photo settings in a JSON file (in HomeTV's data folder).</summary>
public sealed class PhotoSettingsStore(string path)
{
    private readonly Lock _lock = new();
    private PhotoSettings? _current;

    public PhotoSettings Current
    {
        get
        {
            lock (_lock)
                return _current ??= Load();
        }
    }

    public PhotoSettings Update(Func<PhotoSettings, PhotoSettings> change)
    {
        lock (_lock)
        {
            var next = change(_current ??= Load()).Normalized();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, HomeTvJson.Options));
            File.Move(temp, path, overwrite: true);
            return _current = next;
        }
    }

    private PhotoSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<PhotoSettings>(File.ReadAllText(path), HomeTvJson.Options) ?? new()).Normalized()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>
/// Tags for the photos in one folder, kept beside them in a hidden <c>.hometv-tags.json</c> so they move with the
/// photos. Keyed by file name.
/// </summary>
public sealed class PhotoTagStore(string folder)
{
    public const string FileName = ".hometv-tags.json";

    private readonly Lock _lock = new();
    private Dictionary<string, PhotoTags>? _tags;

    private string FilePath => Path.Combine(folder, FileName);

    public PhotoTags? Get(string name)
    {
        lock (_lock)
            return Loaded().GetValueOrDefault(name);
    }

    public IReadOnlyDictionary<string, PhotoTags> All()
    {
        lock (_lock)
            return new Dictionary<string, PhotoTags>(Loaded(), StringComparer.OrdinalIgnoreCase);
    }

    public void Set(string name, PhotoTags tags)
    {
        lock (_lock)
        {
            Loaded()[name] = tags;
            Save();
        }
    }

    /// <summary>Forgets tags for files that are gone. True if anything was removed.</summary>
    public bool Prune(IEnumerable<string> existing)
    {
        lock (_lock)
        {
            var keep = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tags = Loaded();
            var gone = tags.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var name in gone)
                tags.Remove(name);
            if (gone.Count > 0)
                Save();
            return gone.Count > 0;
        }
    }

    private Dictionary<string, PhotoTags> Loaded()
    {
        if (_tags is not null)
            return _tags;
        try
        {
            _tags = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, PhotoTags>>(File.ReadAllText(FilePath), HomeTvJson.Options)
                : null;
        }
        catch (JsonException)
        {
            _tags = null;
        }
        return _tags = new Dictionary<string, PhotoTags>(_tags ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_tags, HomeTvJson.Options));
        // A hidden file can't be overwritten by File.Move on Windows; clear the flag first.
        if (File.Exists(FilePath))
            File.SetAttributes(FilePath, FileAttributes.Normal);
        File.Move(temp, FilePath, overwrite: true);
        try { File.SetAttributes(FilePath, FileAttributes.Hidden); }
        catch (IOException) { }
    }
}
