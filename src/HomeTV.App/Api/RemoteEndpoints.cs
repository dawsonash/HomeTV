using HomeTV.Core.Apps;
using HomeTV.Core.Session;

namespace HomeTV.App.Api;

internal static class RemoteEndpoints
{
    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/apps", (HttpContext context, AppRegistry apps) =>
            Auth.IsAuthorized(context)
                ? Results.Ok(apps.Apps.Select(a => new { a.Id, a.Name, a.Color }))
                : Results.Unauthorized());

        app.Map("/ws/remote", async (HttpContext context, ILogger<Program> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();

            // Remotes only need to know what's on screen; the launcher gets everything.
            // Device is null for an unpaired remote opened on the box itself; its moves go through unscaled.
            await HubSocket.RunAsync(context, evt => evt is StateEvent, logger, Auth.Device(context)?.Id);
            return Results.Empty;
        });
    }
}
