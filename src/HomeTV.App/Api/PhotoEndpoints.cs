using HomeTV.Core.Photos;
using Microsoft.AspNetCore.StaticFiles;

namespace HomeTV.App.Api;

internal static class PhotoEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        // The slideshow behind the launcher. Polled, so photos added to the folder appear within a few minutes.
        app.MapGet("/api/photos", (HttpContext context, PhotoLibrary photos) =>
            Auth.IsAuthorized(context)
                ? Results.Json(new
                {
                    folder = photos.Folder,
                    photos = photos.List().Select(p => new
                    {
                        name = p.Name,
                        url = $"/api/photos/{Uri.EscapeDataString(p.Name)}?v={p.Version}",
                    }),
                })
                : Results.Unauthorized());

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
            return Results.File(path, contentType, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: true);
        });
    }
}
