using HomeTV.Core;
using HomeTV.Core.Photos;
using HomeTV.Core.Session;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

namespace HomeTV.App.Api;

internal static class PhotoEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        // The slideshow behind the launcher, and the phone's Photos screen. Polled, so files added to the folder by
        // hand appear within a few minutes.
        // Every photo comes back with its tags and whether the current mode shows it; the launcher plays the shown ones.
        app.MapGet("/api/photos", (HttpContext context, PhotoLibrary photos, PhotoTagStore tags, PhotoSettingsStore settings, PhotoTagger tagger) =>
        {
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            var current = settings.Current;
            var allTags = tags.All();
            return Results.Json(new
            {
                folder = photos.Folder,
                mode = current.Mode,
                slideSeconds = current.SlideSeconds,
                sorting = new { pending = tagger.Pending, status = tagger.Status },
                photos = photos.List().Select(p =>
                {
                    var t = allTags.GetValueOrDefault(p.Name);
                    // Tags for an older version of the file are stale unless set by hand.
                    if (t is { Manual: false } && t.Version != p.Version)
                        t = null;
                    return Describe(p, t, PhotoFilter.Matches(current.Mode, t));
                }),
            }, HomeTvJson.Options);
        });

        // Mode (all / people / nature / safe for work) and how long each photo stays up. The launcher or a paired phone.
        app.MapPut("/api/photos/settings", async (HttpContext context, PhotoSettingsStore settings, SessionState session) =>
        {
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            // Read with HomeTV's options so the mode can be a name ("safeForWork").
            PhotoSettingsPatch? patch;
            try { patch = await context.Request.ReadFromJsonAsync<PhotoSettingsPatch>(HomeTvJson.Options); }
            catch (System.Text.Json.JsonException) { patch = null; }
            if (patch is null)
                return Results.BadRequest();
            var saved = settings.Update(s => s with
            {
                Mode = patch.Mode ?? s.Mode,
                SlideSeconds = patch.SlideSeconds ?? s.SlideSeconds,
            });
            session.Publish(new PhotosChangedEvent());
            return Results.Json(new { mode = saved.Mode, slideSeconds = saved.SlideSeconds }, HomeTvJson.Options);
        });

        // Fix the model's call by hand from a phone. Hand-set tags stick.
        app.MapPut("/api/photos/{name}/tags", (HttpContext context, string name, PhotoTagsPatch patch, PhotoLibrary photos, PhotoTagStore tags, SessionState session) =>
        {
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            var photo = photos.List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (photo is null)
                return Results.NotFound();
            var old = tags.Get(photo.Name);
            var updated = new PhotoTags(
                People: patch.People ?? old?.People ?? false,
                Nature: patch.Nature ?? old?.Nature ?? false,
                SafeForWork: patch.SafeForWork ?? old?.SafeForWork ?? false,
                Manual: true,
                Version: photo.Version);
            tags.Set(photo.Name, updated);
            session.Publish(new PhotosChangedEvent());
            return Results.Json(DescribeTags(updated), HomeTvJson.Options);
        });

        app.MapGet("/api/photos/{name}", (HttpContext context, string name, PhotoLibrary photos) =>
        {
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            var path = photos.Resolve(name);
            if (path is null)
                return Results.NotFound();
            // The URL carries the file's version, so the WebView can keep it for good and skip revalidating.
            context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            var contentType = ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
            // .mov is QuickTime to the content-type table; WebView2 plays it as MP4 (it usually is H.264/HEVC in MP4 boxes).
            if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                contentType = "video/mp4";
            return Results.File(path, contentType, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: true);
        });

        // Upload from a phone: the file is the raw request body, its name in ?name=. One file per request, so the
        // phone can show progress per file and a big video doesn't hold up the rest.
        app.MapPost("/api/photos", async (HttpContext context, string? name, PhotoLibrary photos, SessionState session, PhotoTagger tagger, ILogger<Program> logger) =>
        {
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = PhotoLibrary.MaxUploadBytes;

            try
            {
                var saved = await photos.SaveAsync(name ?? "", context.Request.Body, context.RequestAborted);
                session.Publish(new PhotosChangedEvent());
                tagger.Poke();
                logger.LogInformation("Background {Kind} uploaded by {Device}: {Name}", saved.Kind, Auth.Device(context)?.Name ?? "the box", saved.Name);
                return Results.Json(Describe(saved, null, true), HomeTvJson.Options);
            }
            catch (PhotoRejectedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/photos/{name}", (HttpContext context, string name, PhotoLibrary photos, SessionState session, ILogger<Program> logger) =>
        {
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            if (!photos.Delete(name))
                return Results.NotFound();
            session.Publish(new PhotosChangedEvent());
            logger.LogInformation("Background photo deleted by {Device}: {Name}", Auth.Device(context)?.Name ?? "the box", name);
            return Results.NoContent();
        });
    }

    private static object Describe(Photo photo, PhotoTags? tags, bool shown) => new
    {
        name = photo.Name,
        url = $"/api/photos/{Uri.EscapeDataString(photo.Name)}?v={photo.Version}",
        kind = photo.Kind,
        tags = tags is null ? null : DescribeTags(tags),
        shown,
    };

    private static object DescribeTags(PhotoTags tags) => new
    {
        people = tags.People,
        nature = tags.Nature,
        safeForWork = tags.SafeForWork,
        manual = tags.Manual,
    };

    internal sealed record PhotoSettingsPatch(PhotoMode? Mode, int? SlideSeconds);

    internal sealed record PhotoTagsPatch(bool? People, bool? Nature, bool? SafeForWork);
}
