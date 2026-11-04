using HomeTV.Core.Apps;
using HomeTV.Core.Platform;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;

namespace HomeTV.App.Api;

internal static class RemoteEndpoints
{
    /// <summary>How long a tap waits to see where focus goes. The phone screen holds still meanwhile, so keep it short.</summary>
    private static readonly TimeSpan TapFocusTimeout = TimeSpan.FromMilliseconds(250);

    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/apps", (HttpContext context, AppRegistry apps) =>
            Auth.IsAuthorized(context)
                ? Results.Ok(apps.Apps.Select(a => new { a.Id, a.Name, a.Color }))
                : Results.Unauthorized());

        // A touchpad tap, as a request the phone waits on rather than a WebSocket message: the answer says whether
        // the click put focus in a text field, while the phone is still handling the tap. iOS only opens its
        // keyboard for a field focused during a tap, so this is how the keyboard comes up by itself.
        app.MapPost("/api/tap", async (HttpContext context, CommandDispatcher dispatcher, ITextFocus textFocus) =>
        {
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();

            var field = await textFocus.FocusAfterAsync(
                () => dispatcher.DispatchAsync(new ClickCommand(MouseButton.Left), context.RequestAborted),
                TapFocusTimeout,
                context.RequestAborted);
            return Results.Ok(new { field });
        });

        app.Map("/ws/remote", async (HttpContext context, ILogger<Program> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (!Auth.IsSameOrigin(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Auth.IsAuthorized(context))
                return Results.Unauthorized();

            // Remotes only need to know what's on screen and playing; the launcher gets everything.
            // Device is null for an unpaired remote opened on the box itself; its moves go through unscaled.
            await HubSocket.RunAsync(context, evt => evt is StateEvent or NowPlayingEvent or TextFocusEvent or ScrollableEvent, logger, Auth.Device(context)?.Id);
            return Results.Empty;
        });
    }
}
