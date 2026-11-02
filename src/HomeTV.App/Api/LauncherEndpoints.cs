namespace HomeTV.App.Api;

internal static class LauncherEndpoints
{
    public static void MapLauncherEndpoints(this IEndpointRouteBuilder app)
    {
        // The launcher page shows pairing PINs, so only the box itself may connect.
        app.Map("/ws/launcher", async (HttpContext context, ILogger<Program> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (!Auth.IsLauncher(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            await HubSocket.RunAsync(context, _ => true, logger);
            return Results.Empty;
        });
    }
}
