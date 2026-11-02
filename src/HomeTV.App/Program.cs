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

app.Run();
