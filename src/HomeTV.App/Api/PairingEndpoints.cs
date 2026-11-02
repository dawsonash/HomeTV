using HomeTV.Core.Pairing;

namespace HomeTV.App.Api;

internal static class PairingEndpoints
{
    public sealed record PairRequest(string Pin, string? DeviceName);

    public static void MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pair/status", (HttpContext context) =>
            Results.Ok(new { paired = Auth.Device(context) is not null }));

        // Asks the TV to show a fresh PIN.
        app.MapPost("/api/pair/request", (PairingService pairing) =>
        {
            var result = pairing.RequestPin();
            return result.Issued
                ? Results.Accepted()
                : Results.Json(new { retryAfterSeconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds) }, statusCode: StatusCodes.Status429TooManyRequests);
        });

        app.MapPost("/api/pair", (PairRequest request, HttpContext context, PairingService pairing) =>
        {
            var result = pairing.TryPair(request.Pin ?? "", request.DeviceName ?? "");
            switch (result.Outcome)
            {
                case PairOutcome.Paired:
                    Auth.SetTokenCookie(context, result.Token!);
                    return Results.Ok(new { deviceId = result.Device!.Id, name = result.Device.Name });
                case PairOutcome.WrongPin:
                    return Results.Json(new { error = "wrongPin" }, statusCode: StatusCodes.Status401Unauthorized);
                default:
                    return Results.Json(new { error = "noActivePin" }, statusCode: StatusCodes.Status410Gone);
            }
        });

        app.MapGet("/api/devices", (HttpContext context, PairingService pairing) =>
            Auth.IsAuthorized(context)
                ? Results.Ok(pairing.Devices.Select(d => new { d.Id, d.Name, d.PairedAt }))
                : Results.Unauthorized());

        // Revoking is done from the launcher's settings screen, i.e. physically at the TV.
        app.MapDelete("/api/devices/{id}", (string id, HttpContext context, PairingService pairing) =>
        {
            if (!Auth.IsLocal(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return pairing.Revoke(id) ? Results.NoContent() : Results.NotFound();
        });
    }
}
