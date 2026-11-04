namespace HomeTV.Core.Photos;

public enum PhotoKind
{
    Image,

    /// <summary>Plays muted behind the launcher.</summary>
    Video,
}

/// <summary>One background photo or video: its file name, a version that changes when the file does (for cache busting), and its kind.</summary>
public sealed record Photo(string Name, string Version, PhotoKind Kind);

/// <summary>Turns an iPhone HEIC/HEIF photo into a JPEG the launcher's WebView can draw.</summary>
public interface IHeicConverter
{
    /// <summary>Writes <paramref name="jpegPath"/> from <paramref name="heicPath"/>, upright (EXIF orientation applied).</summary>
    /// <exception cref="PhotoRejectedException">The file isn't a HEIC image it can read.</exception>
    Task ConvertToJpegAsync(string heicPath, string jpegPath, CancellationToken ct = default);
}

/// <summary>Why an upload was refused.</summary>
public sealed class PhotoRejectedException(string message) : Exception(message);

/// <summary>
/// The launcher's background slideshow: whatever images and videos are in one folder. Re-read on every call, so files
/// dropped in (or deleted, or uploaded from a phone) show up without a restart. Subfolders are ignored.
/// </summary>
/// <param name="heic">Converts HEIC uploads to JPEG; without one, HEIC uploads are refused.</param>
public sealed class PhotoLibrary(string folder, IHeicConverter? heic = null)
{
    /// <summary>iPhone photo formats, converted to JPEG on upload.</summary>
    private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>Image formats the launcher's WebView can draw. HEIC (iPhone default) isn't one of them.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp",
    };

    /// <summary>
    /// Video containers the WebView plays. H.264 plays everywhere; HEVC (iPhone's default) only with Windows' HEVC
    /// extension, so a .mov that won't play is skipped by the slideshow rather than shown broken.
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm",
    };

    /// <summary>Longest upload accepted, per file.</summary>
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Where an upload is written until it's complete, so a half-sent file never shows up.</summary>
    private const string PartialSuffix = ".uploading";

    public string Folder { get; } = folder;

    public IReadOnlyList<Photo> List()
    {
        if (!Directory.Exists(Folder))
            return [];
        return new DirectoryInfo(Folder).EnumerateFiles()
            .Where(IsShowable)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Photo(f.Name, $"{f.LastWriteTimeUtc.Ticks:x}-{f.Length:x}", KindOf(f.Extension)))
            .ToList();
    }

    /// <summary>
    /// The full path of a photo or video by file name, or null. Only plain names of showable files directly in the
    /// folder resolve, so a request can't reach anything else on disk ("..\secrets.txt", "C:\...", other extensions).
    /// </summary>
    public string? Resolve(string name)
    {
        if (!IsPlainName(name))
            return null;
        var file = new FileInfo(Path.Combine(Folder, name));
        return file.Exists && IsShowable(file) ? file.FullName : null;
    }

    /// <summary>
    /// Saves an uploaded file under a cleaned-up version of <paramref name="originalName"/>, adding " (2)" and so on
    /// rather than replacing a file that's already there. Written to a temporary name first, so the slideshow never
    /// picks up half a file.
    /// </summary>
    /// <exception cref="PhotoRejectedException">Not an image or video the slideshow can show, or too big.</exception>
    public async Task<Photo> SaveAsync(string originalName, Stream content, CancellationToken ct = default)
    {
        var name = CleanName(originalName);
        var extension = Path.GetExtension(name);
        var convert = HeicExtensions.Contains(extension);
        if (convert && heic is null)
            throw new PhotoRejectedException("HEIC photos can't be converted on this box. Upload a JPEG instead.");
        if (!convert && !ImageExtensions.Contains(extension) && !VideoExtensions.Contains(extension))
            throw new PhotoRejectedException($"{(extension.Length > 0 ? extension : "That file")} isn't a photo or video the TV can show.");

        Directory.CreateDirectory(Folder);
        var partial = Path.Combine(Folder, $"{Guid.NewGuid():n}{PartialSuffix}");
        var converted = partial + ".jpg" + PartialSuffix;
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes)
                        throw new PhotoRejectedException($"Files over {MaxUploadBytes / (1024 * 1024 * 1024)} GB can't be uploaded.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0)
                    throw new PhotoRejectedException("The file was empty.");
            }

            if (convert)
            {
                await heic!.ConvertToJpegAsync(partial, converted, ct);
                File.Delete(partial);
                File.Move(converted, partial);
                extension = ".jpg";
                name = Path.GetFileNameWithoutExtension(name) + extension;
            }

            var stem = Path.GetFileNameWithoutExtension(name);
            var final = name;
            for (var n = 2; ; n++)
            {
                try
                {
                    File.Move(partial, Path.Combine(Folder, final));
                    break;
                }
                catch (IOException) when (File.Exists(Path.Combine(Folder, final)) && n < 10_000)
                {
                    final = $"{stem} ({n}){extension}";
                }
            }
            var saved = new FileInfo(Path.Combine(Folder, final));
            return new Photo(saved.Name, $"{saved.LastWriteTimeUtc.Ticks:x}-{saved.Length:x}", KindOf(extension));
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
            if (File.Exists(converted))
                File.Delete(converted);
        }
    }

    /// <summary>Deletes a photo or video by file name. False if there's no such file.</summary>
    public bool Delete(string name)
    {
        if (Resolve(name) is not { } path)
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Makes the folder if it's missing, so it's there to drop photos into. Best effort.</summary>
    public void EnsureFolder()
    {
        try { Directory.CreateDirectory(Folder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A safe file name from whatever the phone sent: no path, no odd characters, a sane length.</summary>
    internal static string CleanName(string originalName)
    {
        var name = Path.GetFileName((originalName ?? "").Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '/', '\\', '*', '?', '"', '<', '>', '|']).ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        if (stem.Length == 0)
            stem = $"photo-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (stem.Length > 80)
            stem = stem[..80];
        return stem + extension;
    }

    private static bool IsPlainName(string name) =>
        // ':' would name an NTFS alternate stream; '\\' and '/' a path, on any OS.
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static PhotoKind KindOf(string extension) =>
        VideoExtensions.Contains(extension) ? PhotoKind.Video : PhotoKind.Image;

    private static bool IsShowable(FileInfo file) =>
        (ImageExtensions.Contains(file.Extension) || VideoExtensions.Contains(file.Extension))
        && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
}
