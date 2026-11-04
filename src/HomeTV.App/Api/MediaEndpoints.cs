using HomeTV.Core.Apps;
using HomeTV.Core.Continue;
using HomeTV.Core.NowPlaying;

namespace HomeTV.App.Api;

/// <summary>"Continue watching" and the now-playing artwork, for the launcher and paired phones.</summary>
internal static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // Newest first. The page address stays on the box: launching an entry goes by its id.
        app.MapGet("/api/continue", (HttpContext context, ContinueService continueWatching, AppRegistry apps) =>
            Auth.IsAuthorized(context)
                ? Results.Json(
                    continueWatching.Entries
                        .Where(e => apps.Find(e.AppId) is not null)
                        .Select(e => new { e.Id, e.AppId, e.Title, e.Image, e.Position, e.Duration, e.UpdatedAt }),
                    HomeTV.Core.HomeTvJson.Options)
                : Results.Unauthorized());

        app.MapDelete("/api/continue/{id}", (HttpContext context, string id, ContinueService continueWatching) =>
        {
            if (!Auth.IsAuthorized(context) || !Auth.IsSameOrigin(context))
                return Results.Unauthorized();
            return continueWatching.Remove(id) ? Results.NoContent() : Results.NotFound();
        });

        // The URL carries ?v=<artVersion>, so a given URL's image never changes and can be cached.
        app.MapGet("/api/now-playing/art", (HttpContext context, NowPlayingService nowPlaying) =>
        {
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();
            if (nowPlaying.Art is not { } art)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Bytes(art.Bytes, art.Type);
        });
    }
}
