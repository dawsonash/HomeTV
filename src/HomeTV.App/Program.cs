using HomeTV.App.Api;
using Microsoft.AspNetCore.Http;
using HomeTV.Core.Apps;
using HomeTV.Core.Pairing;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;
using HomeTV.Core.Widgets;
#if WINDOWS
using HomeTV.Platform.Windows;
#else
using HomeTV.Platform.Fake;
#endif

#if WINDOWS
var isWorker = args.Contains(Watchdog.WorkerFlag);
args = args.Where(a => a != Watchdog.WorkerFlag).ToArray();
#endif

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["HomeTV:DataDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV");
var appsPath = builder.Configuration["HomeTV:AppsPath"] ?? Path.Combine(AppContext.BaseDirectory, "apps.json");
var widgetsPath = builder.Configuration["HomeTV:WidgetsPath"] ?? Path.Combine(AppContext.BaseDirectory, "widgets.json");

var urls = builder.Configuration["HomeTV:Urls"] ?? "http://0.0.0.0:8888";
var port = BindingAddress.Parse(urls.Split(';')[0]).Port;
builder.WebHost.UseUrls(urls);

#if WINDOWS
// The installed exe supervises a child copy of itself (HomeTV:Watchdog=false turns this off, as `dotnet run` does).
if (!isWorker && builder.Configuration.GetValue("HomeTV:Watchdog", true))
    return await Watchdog.RunAsync(args, port);
#endif

// One line per entry, timestamped: easier to read in the watchdog's log file.
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services
    .AddSingleton(TimeProvider.System)
    .AddSingleton<SessionState>()
    .AddSingleton<IPairingStore>(new JsonFilePairingStore(Path.Combine(dataDir, "pairings.json")))
    .AddSingleton<PairingService>()
    .AddSingleton(AppRegistry.Load(appsPath))
    .AddSingleton<CommandDispatcher>()
    .AddSingleton(WidgetConfig.Load(widgetsPath))
    .AddSingleton(sp => new WidgetService(
        sp.GetRequiredService<WidgetConfig>(),
        new HttpClient { Timeout = TimeSpan.FromSeconds(10), DefaultRequestHeaders = { { "User-Agent", "HomeTV/1.0" } } },
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<WidgetService>>()))
#if WINDOWS
    .AddSingleton(new RuntimeInfo(Supervised: isWorker));
#else
    .AddSingleton(new RuntimeInfo(Supervised: false));
#endif

#if WINDOWS
builder.Services.AddWindowsPlatform();
#else
builder.Services.AddFakePlatform();
#endif

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Without this, browsers (the TV's WebView included) guess a freshness lifetime and keep showing the old
    // launcher/remote after an update. Revalidating is a cheap 304 on the LAN thanks to the ETag.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/", () => Results.Redirect("/remote/"));

// Polled by the watchdog: answers only if the server is up and (on the box) the launcher window isn't frozen.
app.MapGet("/healthz", (HttpContext context) =>
{
    if (!Auth.IsLocal(context))
        return Results.NotFound();
#if WINDOWS
    if (!context.RequestServices.GetRequiredService<LauncherWindow>().IsResponsive)
        return Results.Text("launcher not responding", statusCode: StatusCodes.Status503ServiceUnavailable);
#endif
    return Results.Text("ok");
});
app.MapPairingEndpoints();
app.MapRemoteEndpoints();
app.MapLauncherEndpoints();
app.MapSystemEndpoints();

// An app the user quits by other means (Alt+F4, the app's own exit, a crash) hands the TV back to the launcher.
var session = app.Services.GetRequiredService<SessionState>();
app.Services.GetRequiredService<HomeTV.Core.Platform.IAppHost>().AppExited += appId => session.ClearForeground(appId);

#if WINDOWS
// HomeTV:Launcher = Fullscreen (the TV), Windowed (development), or None (use a browser).
var launcherMode = Enum.TryParse<LauncherMode>(app.Configuration["HomeTV:Launcher"], ignoreCase: true, out var mode)
    ? mode
    : LauncherMode.Fullscreen;

// Getting home from the box itself, without the phone. Disposed (unregistered) by the DI container on shutdown.
var dispatcher = app.Services.GetRequiredService<CommandDispatcher>();
app.Services.GetRequiredService<HomeShortcuts>().Start(() => dispatcher.DispatchAsync(new HomeCommand()).GetAwaiter().GetResult());

await app.StartAsync();
if (launcherMode == LauncherMode.None)
{
    await app.WaitForShutdownAsync();
}
else
{
    // Kestrel listens on 0.0.0.0; the launcher talks to it over loopback, which skips pairing.
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
return 0;
