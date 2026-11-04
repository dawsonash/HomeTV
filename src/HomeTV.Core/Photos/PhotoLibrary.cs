namespace HomeTV.Core.Photos;

/// <summary>One background photo: its file name and a version that changes when the file does (for cache busting).</summary>
public sealed record Photo(string Name, string Version);

/// <summary>
/// The launcher's background slideshow: whatever images are in one folder. Re-read on every call, so photos dropped in
/// (or deleted) show up without a restart. Subfolders are ignored.
/// </summary>
public sealed class PhotoLibrary(string folder)
{
    /// <summary>Formats the launcher's WebView can draw. HEIC (iPhone default) isn't one of them.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp",
    };

    public string Folder { get; } = folder;

    public IReadOnlyList<Photo> List()
    {
        if (!Directory.Exists(Folder))
            return [];
        return new DirectoryInfo(Folder).EnumerateFiles()
            .Where(IsPhoto)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Photo(f.Name, $"{f.LastWriteTimeUtc.Ticks:x}-{f.Length:x}"))
            .ToList();
    }

    /// <summary>
    /// The full path of a photo by file name, or null. Only plain names of image files directly in the folder resolve,
    /// so a request can't reach anything else on disk ("..\secrets.txt", "C:\...", other extensions).
    /// </summary>
    public string? Resolve(string name)
    {
        // ':' would name an NTFS alternate stream; '\\' and '/' a path, on any OS.
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var file = new FileInfo(Path.Combine(Folder, name));
        return file.Exists && IsPhoto(file) ? file.FullName : null;
    }

    /// <summary>Makes the folder if it's missing, so it's there to drop photos into. Best effort.</summary>
    public void EnsureFolder()
    {
        try { Directory.CreateDirectory(Folder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsPhoto(FileInfo file) =>
        Extensions.Contains(file.Extension) && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
}
