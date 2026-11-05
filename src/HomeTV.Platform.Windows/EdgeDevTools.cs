using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeTV.Core.Continue;

namespace HomeTV.Platform.Windows;

/// <summary>Where an Edge app window goes, in screen DIPs (what CSS calls pixels at 100% zoom).</summary>
internal abstract record EdgeWindowPlacement
{
    public sealed record Fullscreen : EdgeWindowPlacement;

    /// <summary>The page fills this rectangle; the window's title bar and borders hang outside it.</summary>
    public sealed record Tile(double X, double Y, double Width, double Height) : EdgeWindowPlacement;
}

/// <summary>Screen size and window chrome as an Edge page sees them, in DIPs.</summary>
internal sealed record EdgeWindowMetrics(double ScreenWidth, double ScreenHeight, double Border, double TitleBar);

/// <summary>
/// Talks to an Edge app window through the Chrome DevTools Protocol: reads what its page shows, pauses or tiles it,
/// and moves its window. Edge only opens the DevTools port for a non-default <c>--user-data-dir</c> (Chromium 136+),
/// and with <c>--remote-debugging-port=0</c> it picks a free port and writes it to <c>DevToolsActivePort</c> in that
/// directory. The port listens on loopback only.
/// </summary>
internal static class EdgeDevTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly HttpClient Http = new() { Timeout = Timeout };

    /// <summary>
    /// Runs in the page. Prefers Media Session metadata over og: tags, because single-page sites (YouTube) update it
    /// as you move between videos but leave the og: tags from whatever page loaded first.
    /// </summary>
    private const string SnapshotScript = """
        (() => {
          const meta = (name) => document.querySelector(`meta[property="${name}"],meta[name="${name}"]`)?.content || null;
          const ogUrl = meta("og:url");
          const ogCurrent = !ogUrl || ogUrl.split(/[?#]/)[0] === location.origin + location.pathname;
          const media = navigator.mediaSession?.metadata;
          const art = media?.artwork?.length ? media.artwork[media.artwork.length - 1].src : null;
          const video = [...document.querySelectorAll("video")]
            .filter((v) => v.readyState > 0)
            .sort((a, b) => b.videoWidth * b.videoHeight - a.videoWidth * a.videoHeight)[0];
          return {
            url: location.href,
            title: media?.title || (ogCurrent && meta("og:title")) || document.title || null,
            image: art || (ogCurrent ? meta("og:image") : null),
            position: video ? video.currentTime : null,
            duration: video && Number.isFinite(video.duration) ? video.duration : null,
          };
        })()
        """;

    private const string PauseScript = """document.querySelectorAll("video,audio").forEach((m) => m.pause())""";

    private const string MetricsScript = """
        (() => {
          const border = Math.max(0, (outerWidth - innerWidth) / 2);
          return { screenWidth: screen.width, screenHeight: screen.height, border, titleBar: Math.max(0, outerHeight - innerHeight - border) };
        })()
        """;

    /// <summary>
    /// Installs (once per page) and updates split-screen behaviour. <c>__TILE__</c> is replaced with
    /// <c>{ muted, fit }</c> for a tile, or <c>null</c> to undo everything. A fitted video fills the tile, shown in
    /// the top layer (as a popover) so the site's own layout and stacking can't clip it. The site's full-screen
    /// button fits the video instead of covering the whole TV, and Escape (the remote's Back) shows the page again.
    /// Re-applied every second, because sites swap their video elements and unmute them.
    /// </summary>
    private const string TileScript = """
        ((tile) => {
          const s = (window.__hometv ??= {});
          const apply = () => {
            const t = s.tile;
            const videos = [...document.querySelectorAll("video")];
            const main = videos
              .filter((v) => v.readyState > 0)
              .sort((a, b) => b.videoWidth * b.videoHeight - a.videoWidth * a.videoHeight)[0];
            for (const v of videos) {
              const fit = !!(t?.fit && v === main);
              if (fit === v.hasAttribute("data-hometv-fit")) continue;
              v.toggleAttribute("data-hometv-fit", fit);
              try {
                if (fit) { v.popover = "manual"; v.showPopover(); }
                else if (v.hasAttribute("popover")) { v.hidePopover(); v.removeAttribute("popover"); }
              } catch {}
            }
            for (const m of document.querySelectorAll("video,audio")) {
              if (t?.muted) { if (!m.muted) { m.muted = true; m.dataset.hometvMuted = ""; } }
              else if ("hometvMuted" in m.dataset) { m.muted = false; delete m.dataset.hometvMuted; }
            }
          };
          if (!s.installed) {
            s.installed = true;
            const style = document.createElement("style");
            style.textContent = "video[data-hometv-fit]{position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;margin:0!important;padding:0!important;border:0!important;object-fit:contain!important;background:#000!important;transform:none!important;z-index:2147483647!important}video[data-hometv-fit]::backdrop{background:#000}";
            document.documentElement.append(style);
            for (const name of ["requestFullscreen", "webkitRequestFullscreen"]) {
              const original = Element.prototype[name];
              if (!original) continue;
              Element.prototype[name] = function (...args) {
                if (!s.tile) return original.apply(this, args);
                s.tile.fit = true;
                s.apply();
                return Promise.resolve();
              };
            }
            addEventListener("keydown", (e) => {
              if (!s.tile?.fit || e.key !== "Escape") return;
              s.tile.fit = false;
              s.apply();
              e.preventDefault();
              e.stopImmediatePropagation();
            }, true);
            setInterval(() => s.apply(), 1000);
          }
          // A tile keeps the fit the user chose when only the focus moves.
          if (tile && tile.fit == null) tile.fit = s.tile ? s.tile.fit : true;
          s.tile = tile;
          s.apply = apply;
          apply();
        })(__TILE__)
        """;

    /// <summary>The port Edge is listening on for this profile, or null if it isn't (yet).</summary>
    public static int? ReadPort(string userDataDir)
    {
        try
        {
            var path = Path.Combine(userDataDir, "DevToolsActivePort");
            return File.Exists(path) && int.TryParse(File.ReadLines(path).FirstOrDefault(), out var port) && port is > 0 and < 65536
                ? port
                : null;
        }
        catch (IOException)
        {
            return null; // Edge is mid-write.
        }
    }

    /// <summary>What the profile's open page shows, or null if Edge isn't answering.</summary>
    public static async Task<PageSnapshot?> SnapshotAsync(int port, CancellationToken ct)
    {
        var value = await EvaluateAsync(port, SnapshotScript, ct);
        if (value is not { ValueKind: JsonValueKind.Object } json)
            return null;
        var page = json.Deserialize<PageValue>(JsonOptions);
        return page?.Url is { } url ? new PageSnapshot(url, page.Title, page.Image, page.Position, page.Duration) : null;
    }

    public static Task PauseAsync(int port, CancellationToken ct) => EvaluateAsync(port, PauseScript, ct);

    /// <summary>Loads <paramref name="url"/> in the profile's page (same tab). False if Edge isn't answering.</summary>
    public static async Task<bool> NavigateAsync(int port, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            if (await FindPageAsync(port, cts.Token) is not { WebSocketDebuggerUrl: { } socketUrl })
                return false;
            await using var page = await Cdp.ConnectAsync(socketUrl, cts.Token);
            var result = await page.CallAsync("Page.navigate", new { url }, cts.Token);
            // A navigation that fails outright (bad address, offline) comes back with errorText.
            return !(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errorText", out _));
        }
        catch (Exception ex) when (IsEdgeGone(ex, ct))
        {
            return false;
        }
    }

    /// <summary>Tiles the page (see <see cref="TileScript"/>), or with <paramref name="muted"/> null, untiles it.</summary>
    /// <param name="fit">Fill the tile with the video; null keeps what it was (a new tile starts fitted).</param>
    public static Task SetTileAsync(int port, bool? muted, bool? fit, CancellationToken ct)
    {
        var tile = muted is { } m
            ? JsonSerializer.Serialize(new { muted = m, fit })
            : "null";
        return EvaluateAsync(port, TileScript.Replace("__TILE__", tile), ct);
    }

    /// <summary>
    /// Moves the profile's window. A tile leaves full screen first and is sized so the page, not the window, fills
    /// the rectangle: the title bar goes above it (off screen, or under the tile above) and the borders, which are
    /// invisible, outside it. False if Edge isn't answering.
    /// </summary>
    public static async Task<bool> PlaceAsync(int port, EdgeWindowPlacement placement, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout * 2);
        try
        {
            var version = await Http.GetFromJsonAsync<BrowserVersion>($"http://127.0.0.1:{port}/json/version", cts.Token);
            if (await FindPageAsync(port, cts.Token) is not { } page || version?.WebSocketDebuggerUrl is not { } browserUrl)
                return false;

            await using var browser = await Cdp.ConnectAsync(browserUrl, cts.Token);
            var window = await browser.CallAsync("Browser.getWindowForTarget", new { targetId = page.Id }, cts.Token);
            var windowId = window.GetProperty("windowId").GetInt64();
            var state = window.GetProperty("bounds").GetProperty("windowState").GetString();

            if (placement is EdgeWindowPlacement.Fullscreen)
            {
                if (state != "fullscreen")
                    await browser.CallAsync("Browser.setWindowBounds", new { windowId, bounds = new { windowState = "fullscreen" } }, cts.Token);
                return true;
            }

            var tile = (EdgeWindowPlacement.Tile)placement;
            if (state != "normal")
            {
                // Bounds only apply to a normal window, and the window chrome only shows up in one.
                await browser.CallAsync("Browser.setWindowBounds", new { windowId, bounds = new { windowState = "normal" } }, cts.Token);
                await Task.Delay(250, cts.Token);
            }
            if (await EvaluateAsync(port, MetricsScript, cts.Token) is not { ValueKind: JsonValueKind.Object } json
                || json.Deserialize<EdgeWindowMetrics>(JsonOptions) is not { } metrics)
                return false;

            var bounds = new
            {
                left = (int)Math.Round(tile.X - metrics.Border),
                top = (int)Math.Round(tile.Y - metrics.TitleBar),
                width = (int)Math.Round(tile.Width + 2 * metrics.Border),
                height = (int)Math.Round(tile.Height + metrics.TitleBar + metrics.Border),
            };
            await browser.CallAsync("Browser.setWindowBounds", new { windowId, bounds }, cts.Token);
            return true;
        }
        catch (Exception ex) when (IsEdgeGone(ex, ct))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the mouse wheel would scroll anything at a screen point (physical pixels): an element there with its
    /// own scrollbar, or else the page itself. <c>__X__</c>/<c>__Y__</c> are replaced with the point.
    /// </summary>
    private const string CanScrollScript = """
        ((x, y) => {
          const border = Math.max(0, (outerWidth - innerWidth) / 2);
          const titleBar = Math.max(0, outerHeight - innerHeight - border);
          const cx = x / devicePixelRatio - screenX - border;
          const cy = y / devicePixelRatio - screenY - titleBar;
          const scrolls = (el) => {
            if (el.scrollHeight <= el.clientHeight + 1) return false;
            const overflow = getComputedStyle(el).overflowY;
            return overflow === "auto" || overflow === "scroll" || overflow === "overlay";
          };
          const inside = cx >= 0 && cy >= 0 && cx < innerWidth && cy < innerHeight;
          for (let el = inside ? document.elementFromPoint(cx, cy) : null; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
            if (scrolls(el)) return true;
          }
          const root = document.scrollingElement || document.documentElement;
          const locked = [document.documentElement, document.body].some((el) => el && getComputedStyle(el).overflowY === "hidden");
          return !locked && root.scrollHeight > innerHeight + 1;
        })(__X__, __Y__)
        """;

    /// <summary>Whether the wheel would scroll something at a screen point, or null if Edge isn't answering.</summary>
    public static async Task<bool?> CanScrollAtAsync(int port, int x, int y, CancellationToken ct) =>
        await EvaluateAsync(port, CanScrollScript.Replace("__X__", x.ToString()).Replace("__Y__", y.ToString()), ct) is { } value
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// Closes the profile's whole browser (every window, then the process), the way quitting Edge does, so cookies
    /// and the session are saved. False if Edge isn't answering.
    /// </summary>
    public static async Task<bool> CloseBrowserAsync(int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var version = await Http.GetFromJsonAsync<BrowserVersion>($"http://127.0.0.1:{port}/json/version", cts.Token);
            if (version?.WebSocketDebuggerUrl is not { } browserUrl)
                return false;
            await using var browser = await Cdp.ConnectAsync(browserUrl, cts.Token);
            await browser.CallAsync("Browser.close", new { }, cts.Token);
            return true;
        }
        catch (Exception ex) when (IsEdgeGone(ex, ct))
        {
            // Edge may drop the connection before answering, because it's closing. Either way it's on its way out.
            return ex is CdpException or WebSocketException;
        }
    }

    /// <summary>
    /// For an untrusted site: closes every tab and window in the profile except <paramref name="keep"/> (the app's
    /// own page), which is what pop-up and pop-under ads open. With <paramref name="keep"/> null it first picks the
    /// page to keep (the oldest) and returns it. Null if Edge isn't answering.
    /// </summary>
    public static async Task<string?> CloseOtherPagesAsync(int port, string? keep, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var version = await Http.GetFromJsonAsync<BrowserVersion>($"http://127.0.0.1:{port}/json/version", cts.Token);
            if (version?.WebSocketDebuggerUrl is not { } browserUrl)
                return null;
            await using var browser = await Cdp.ConnectAsync(browserUrl, cts.Token);
            var result = await browser.CallAsync("Target.getTargets", new { }, cts.Token);
            // Oldest first: the order targets were created in isn't promised, but a page's id doesn't change on
            // navigation, so the one kept from the first look stays the app's page.
            var pages = result.GetProperty("targetInfos").EnumerateArray()
                .Where(t => t.GetProperty("type").GetString() == "page")
                .Select(t => t.GetProperty("targetId").GetString()!)
                .ToList();
            if (pages.Count == 0)
                return keep;
            keep ??= pages[^1];
            if (!pages.Contains(keep))
                return keep; // The app's page is gone (the window's closing); don't close what's left.
            foreach (var id in pages.Where(id => id != keep))
                await browser.CallAsync("Target.closeTarget", new { targetId = id }, cts.Token);
            return keep;
        }
        catch (Exception ex) when (IsEdgeGone(ex, ct))
        {
            return keep;
        }
    }

    /// <summary>The screen size in DIPs, as the profile's page sees it.</summary>
    public static async Task<EdgeWindowMetrics?> MetricsAsync(int port, CancellationToken ct) =>
        await EvaluateAsync(port, MetricsScript, ct) is { ValueKind: JsonValueKind.Object } json
            ? json.Deserialize<EdgeWindowMetrics>(JsonOptions)
            : null;

    /// <summary>Runs <paramref name="expression"/> in the profile's page and returns its value, or null if Edge isn't answering.</summary>
    private static async Task<JsonElement?> EvaluateAsync(int port, string expression, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            if (await FindPageAsync(port, cts.Token) is not { WebSocketDebuggerUrl: { } socketUrl })
                return null;
            await using var page = await Cdp.ConnectAsync(socketUrl, cts.Token);
            var result = await page.CallAsync("Runtime.evaluate", new { expression, returnByValue = true }, cts.Token);
            return result.TryGetProperty("result", out var remote) && remote.TryGetProperty("value", out var value) ? value : null;
        }
        catch (Exception ex) when (IsEdgeGone(ex, ct))
        {
            return null;
        }
    }

    /// <summary>The profile belongs to one app, so its page is the one page there (most recently used first).</summary>
    private static async Task<Target?> FindPageAsync(int port, CancellationToken ct)
    {
        var targets = await Http.GetFromJsonAsync<List<Target>>($"http://127.0.0.1:{port}/json/list", ct);
        return targets?.FirstOrDefault(t => t.Type == "page" && t.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Edge closed, timed out, or answered with something unexpected: not worth more than a null.</summary>
    private static bool IsEdgeGone(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && !ct.IsCancellationRequested
        || ex is HttpRequestException or WebSocketException or JsonException or IOException or UriFormatException
            or InvalidOperationException or FormatException or KeyNotFoundException or CdpException;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Target(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

    private sealed record BrowserVersion([property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

    private sealed record PageValue(string? Url, string? Title, string? Image, double? Position, double? Duration);

    private sealed class CdpException(string message) : Exception(message);

    /// <summary>One DevTools WebSocket: sends a command and waits for its answer, skipping events.</summary>
    private sealed class Cdp(ClientWebSocket socket) : IAsyncDisposable
    {
        private const int MaxMessageBytes = 256 * 1024;
        private int _nextId;

        public static async Task<Cdp> ConnectAsync(string url, CancellationToken ct)
        {
            // Only loopback: never follow a debugger URL somewhere else.
            var uri = new Uri(url);
            if (!uri.IsLoopback)
                throw new CdpException($"Not a loopback DevTools URL: {uri.Host}");
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, ct);
                return new Cdp(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
        {
            var id = ++_nextId;
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(request, WebSocketMessageType.Text, true, ct);

            var buffer = new byte[MaxMessageBytes];
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                        throw new CdpException("DevTools message too large");
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new CdpException("DevTools closed the connection");
                    length += result.Count;
                } while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (!doc.RootElement.TryGetProperty("id", out var answerId) || answerId.GetInt32() != id)
                    continue; // An event, not our answer.
                if (doc.RootElement.TryGetProperty("error", out var error))
                    throw new CdpException($"{method}: {error}");
                return doc.RootElement.TryGetProperty("result", out var value) ? value.Clone() : default;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Edge went first.
            }
            socket.Dispose();
        }
    }
}
