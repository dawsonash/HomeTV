namespace HomeTV.App.Api;

internal static class LauncherEndpoints
{
    public static void MapLauncherEndpoints(this IEndpointRouteBuilder app)
    {
        // Weather and sports for the home screen. Polled by the launcher; WidgetService decides when to refetch.
        app.MapGet("/api/widgets", async (HttpContext context, HomeTV.Core.Widgets.WidgetService widgets) =>
            Auth.IsAuthorized(context)
                // HomeTvJson, not the ASP.NET default, so enums go out as "live"/"final" like the TypeScript expects.
                ? Results.Json(await widgets.GetAsync(context.RequestAborted), HomeTV.Core.HomeTvJson.Options)
                : Results.Unauthorized());

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
