using HomeTV.Core.Photos;
using ImageMagick;

namespace HomeTV.App;

/// <summary>
/// HEIC → JPEG with ImageMagick (Magick.NET), which carries its own HEIC decoder, so it works on the box without
/// Windows' HEIF/HEVC Store extensions. Big iPhone photos are scaled down to 4K, which is all the TV shows.
/// </summary>
internal sealed class MagickHeicConverter : IHeicConverter
{
    private const uint MaxWidth = 3840;
    private const uint MaxHeight = 2160;

    /// <summary>One at a time: decoding a 48 MP HEIC takes a lot of memory.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task ConvertToJpegAsync(string heicPath, string jpegPath, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    using var image = new MagickImage(heicPath, new MagickReadSettings { Format = MagickFormat.Heic });
                    image.AutoOrient();
                    if (image.Width > MaxWidth || image.Height > MaxHeight)
                        image.Resize(new MagickGeometry(MaxWidth, MaxHeight)); // Keeps the aspect ratio, fits inside.
                    image.Quality = 90;
                    image.Write(jpegPath, MagickFormat.Jpeg);
                }
                catch (MagickException ex)
                {
                    throw new PhotoRejectedException($"That HEIC photo couldn't be read ({ex.Message.Split('\n')[0]}).");
                }
            }, ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
