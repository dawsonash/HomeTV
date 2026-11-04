using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using HomeTV.Core.Platform;

namespace HomeTV.App.Api;

/// <summary>Whether a watchdog will bring HomeTV back after it exits (so "Restart HomeTV" is safe).</summary>
internal sealed record RuntimeInfo(bool Supervised);

/// <summary>Box info and maintenance actions for the launcher's settings screen. Launcher-only.</summary>
internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system", (HttpContext context, RuntimeInfo runtime) =>
        {
            if (!Auth.IsLauncher(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Ok(new
            {
                hostName = Dns.GetHostName(),
                addresses = LanAddresses(),
                port = context.Connection.LocalPort,
                version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0],
                supervised = runtime.Supervised,
            });
        });

        app.MapPost("/api/system/restart-app", (HttpContext context, IHostApplicationLifetime lifetime, ILogger<Program> logger) =>
        {
            if (!Auth.IsLauncher(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            logger.LogWarning("Restart requested from the settings screen");
            // Exits after the response is sent; the watchdog starts a fresh copy.
            lifetime.StopApplication();
            return Results.Accepted();
        });

        // Opens an app's sign-in page in a normal Edge window (see AppDefinition.SignInUrl).
        app.MapPost("/api/system/sign-in/{appId}", async (HttpContext context, string appId, HomeTV.Core.Apps.AppRegistry apps, HomeTV.Core.Apps.AppSwitcher switcher) =>
        {
            if (!Auth.IsLauncher(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (apps.Find(appId) is not { SignInUrl: not null, Untrusted: false } app)
                return Results.NotFound();

            await switcher.SignInAsync(app, context.RequestAborted);
            return Results.Accepted();
        });

        app.MapPost("/api/system/restart-pc", (HttpContext context, IPower power) =>
        {
            if (!Auth.IsLauncher(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            power.Restart();
            return Results.Accepted();
        });
    }

    /// <summary>IPv4 addresses phones on the home network can reach: up, non-loopback adapters with a gateway.</summary>
    private static string[] LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToArray();
}
