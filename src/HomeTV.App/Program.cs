using HomeTV.App.Api;
using Microsoft.AspNetCore.Http;
using HomeTV.Core.Apps;
using HomeTV.Core.Audio;
using HomeTV.Core.Continue;
using HomeTV.Core.NowPlaying;
using HomeTV.Core.Pairing;
using HomeTV.Core.Photos;
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
var audioPath = builder.Configuration["HomeTV:AudioPath"] ?? Path.Combine(AppContext.BaseDirectory, "audio.json");
// Background photos: Pictures\HomeTV of whoever runs HomeTV (the tv user on the box), outside the app folder deploys replace.
var photosDir = builder.Configuration["HomeTV:PhotosDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HomeTV");

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
    .AddSingleton(new AppSwitcherOptions(builder.Configuration.GetValue("HomeTV:MaxBackgroundApps", 2)))
    .AddSingleton<AppSwitcher>()
    .AddSingleton<ScrollHint>()
    .AddSingleton<IContinueStore>(new JsonFileContinueStore(Path.Combine(dataDir, "continue.json")))
    .AddSingleton<ContinueService>()
    .AddSingleton<NowPlayingService>()
    .AddSingleton(WidgetConfig.Load(widgetsPath))
    .AddSingleton(new PhotoLibrary(photosDir))
    .AddSingleton(AudioConfig.Load(audioPath))
    .AddSingleton(sp => new AudioOutputService(
        sp.GetRequiredService<AudioConfig>(),
        sp.GetRequiredService<HomeTV.Core.Platform.IAudioDevices>(),
        sp.GetRequiredService<HomeTV.Core.Platform.ITvControl>(),
        sp.GetRequiredService<SessionState>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<AudioOutputService>>(),
        Path.Combine(dataDir, "audio-output.json")))
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
// adb runs anywhere, but on the Mac the fake platform logs TV commands instead.
builder.Services.AddSingleton<HomeTV.Core.Platform.ITvControl>(sp =>
    new AdbTvControl(sp.GetRequiredService<AudioConfig>().Tv, sp.GetRequiredService<ILogger<AdbTvControl>>()));
#else
builder.Services.AddFakePlatform();
#endif

var app = builder.Build();
app.Services.GetRequiredService<PhotoLibrary>().EnsureFolder(); // So it's there to drop photos into.

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
app.MapMediaEndpoints();
app.MapLauncherEndpoints();
app.MapPhotoEndpoints();
app.MapAudioEndpoints();
app.MapSystemEndpoints();

// An app the user quits by other means (Alt+F4, the app's own exit, a crash) leaves the open-apps list, and the
// launcher (or the remaining tile) takes its place. Clicking into a tile makes it the active one.
var session = app.Services.GetRequiredService<SessionState>();
var appHost = app.Services.GetRequiredService<HomeTV.Core.Platform.IAppHost>();
var switcher = app.Services.GetRequiredService<AppSwitcher>();
appHost.AppExited += appId => _ = switcher.SafelyAsync(() => switcher.OnAppExitedAsync(appId));
appHost.AppActivated += appId => _ = switcher.SafelyAsync(() => switcher.OnAppActivatedAsync(appId));

// "Continue watching": the app host looks at Edge apps' pages every few seconds and reports them here.
var continueWatching = app.Services.GetRequiredService<ContinueService>();
appHost.PageObserved += (appId, page) => continueWatching.Observe(appId, page);
app.Lifetime.ApplicationStopping.Register(continueWatching.Flush);

// What's playing, for the phone remote. Runs in the background; failing only hides the card on the phone.
_ = app.Services.GetRequiredService<NowPlayingService>().StartAsync();

// A text field focused on the TV brings up the phone's keyboard.
var textFocus = app.Services.GetRequiredService<HomeTV.Core.Platform.ITextFocus>();
textFocus.Changed += field => session.Publish(new TextFocusEvent(field));
textFocus.Start();

// The phone shows its scroll strip only when there's something under the pointer to scroll.
app.Services.GetRequiredService<ScrollHint>().Start();

// The launcher's audio button: notices a Bluetooth speaker connecting, or the default device changing elsewhere.
app.Services.GetRequiredService<AudioOutputService>().Start();

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
