using System.Net;
using HomeTV.Core.Pairing;

namespace HomeTV.App.Api;

internal static class Auth
{
    public const string TokenCookie = "hometv_token";

    /// <summary>The launcher runs on the box itself, so loopback requests are trusted without pairing.</summary>
    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public static PairedDevice? Device(HttpContext context) =>
        context.RequestServices.GetRequiredService<PairingService>().Authenticate(context.Request.Cookies[TokenCookie]);

    public static bool IsAuthorized(HttpContext context) => IsLocal(context) || Device(context) is not null;

    /// <summary>
    /// Rejects WebSocket upgrades started by other sites. Browsers attach our cookie to cross-origin
    /// WebSocket requests, so the Origin header is the real check.
    /// </summary>
    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true; // Non-browser clients don't send Origin.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetTokenCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(TokenCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromDays(400), // Browsers cap cookie lifetime at 400 days.
            IsEssential = true,
        });
}
