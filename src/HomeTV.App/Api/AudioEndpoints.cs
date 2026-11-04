using HomeTV.Core.Audio;

namespace HomeTV.App.Api;

/// <summary>Where the sound comes out (the launcher's audio button). Changes also go out as an "audio" hub event.</summary>
internal static class AudioEndpoints
{
    public static void MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audio", (HttpContext context, AudioOutputService audio) =>
            Auth.IsAuthorized(context)
                ? Results.Json(audio.Current, HomeTV.Core.HomeTvJson.Options)
                : Results.Unauthorized());

        // One press of the button: the next available output.
        app.MapPost("/api/audio/next", (HttpContext context, AudioOutputService audio) =>
            SwitchAsync(context, audio, ct => audio.NextAsync(ct)));

        app.MapPost("/api/audio/{id}", (HttpContext context, string id, AudioOutputService audio) =>
            SwitchAsync(context, audio, ct => audio.SelectAsync(id, ct)));
    }

    /// <summary>Answers with the new state, or 409 and why it didn't switch (e.g. the TV didn't answer).</summary>
    private static async Task<IResult> SwitchAsync(HttpContext context, AudioOutputService audio, Func<CancellationToken, Task<string?>> change)
    {
        if (!Auth.IsSameOrigin(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!Auth.IsAuthorized(context))
            return Results.Unauthorized();

        // Not the request's token: a switch half done (TV told, Windows not yet) shouldn't stop because the page left.
        var error = await change(CancellationToken.None);
        return error is null
            ? Results.Json(audio.Current, HomeTV.Core.HomeTvJson.Options)
            : Results.Json(new { error }, HomeTV.Core.HomeTvJson.Options, statusCode: StatusCodes.Status409Conflict);
    }
}
