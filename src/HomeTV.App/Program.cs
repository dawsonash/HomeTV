using HomeTV.App.Api;
using HomeTV.Core.Apps;
using HomeTV.Core.Pairing;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;
#if WINDOWS
using HomeTV.Platform.Windows;
#else
using HomeTV.Platform.Fake;
#endif

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["HomeTV:DataDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV");
var appsPath = builder.Configuration["HomeTV:AppsPath"] ?? Path.Combine(AppContext.BaseDirectory, "apps.json");

builder.WebHost.UseUrls(builder.Configuration["HomeTV:Urls"] ?? "http://0.0.0.0:8888");

builder.Services
    .AddSingleton(TimeProvider.System)
    .AddSingleton<SessionState>()
    .AddSingleton<IPairingStore>(new JsonFilePairingStore(Path.Combine(dataDir, "pairings.json")))
    .AddSingleton<PairingService>()
    .AddSingleton(AppRegistry.Load(appsPath))
    .AddSingleton<CommandDispatcher>();

#if WINDOWS
builder.Services.AddWindowsPlatform();
#else
builder.Services.AddFakePlatform();
#endif

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/remote/"));
app.MapPairingEndpoints();
app.MapRemoteEndpoints();
app.MapLauncherEndpoints();

// An app the user quits by other means (Alt+F4, the app's own exit, a crash) hands the TV back to the launcher.
var session = app.Services.GetRequiredService<SessionState>();
app.Services.GetRequiredService<HomeTV.Core.Platform.IAppHost>().AppExited += appId => session.ClearForeground(appId);

#if WINDOWS
// HomeTV:Launcher = Fullscreen (the TV), Windowed (development), or None (use a browser).
var launcherMode = Enum.TryParse<LauncherMode>(app.Configuration["HomeTV:Launcher"], ignoreCase: true, out var mode)
    ? mode
    : LauncherMode.Fullscreen;

await app.StartAsync();
if (launcherMode == LauncherMode.None)
{
    await app.WaitForShutdownAsync();
}
else
{
    // Kestrel listens on 0.0.0.0; the launcher talks to it over loopback, which skips pairing.
    var port = new Uri(app.Urls.First()).Port;
    var launcherUrl = new Uri($"http://127.0.0.1:{port}/launcher/");
    try
    {
        await app.Services.GetRequiredService<LauncherWindow>().RunAsync(launcherUrl, launcherMode, app.Lifetime.ApplicationStopping);
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Launcher window crashed");
    }
    await app.StopAsync();
}
#else
app.Run();
#endif
