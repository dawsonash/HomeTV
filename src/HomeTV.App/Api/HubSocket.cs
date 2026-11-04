using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HomeTV.Core;
using HomeTV.Core.NowPlaying;
using HomeTV.Core.Pairing;
using HomeTV.Core.Platform;
using HomeTV.Core.Remote;
using HomeTV.Core.Session;

namespace HomeTV.App.Api;

/// <summary>
/// Runs one WebSocket connection: incoming text frames are dispatched as <see cref="RemoteCommand"/>s, and
/// session events matching <paramref name="forward"/> are pushed back out. Pass <c>deviceId</c> for a paired
/// phone so its touchpad moves are scaled by that phone's sensitivity.
/// </summary>
internal static class HubSocket
{
    private const int MaxMessageBytes = 8 * 1024;

    public static async Task RunAsync(HttpContext context, Func<HubEvent, bool> forward, ILogger logger, string? deviceId = null)
    {
        var session = context.RequestServices.GetRequiredService<SessionState>();
        var dispatcher = context.RequestServices.GetRequiredService<CommandDispatcher>();
        var pairing = context.RequestServices.GetRequiredService<PairingService>();
        var scaler = new PointerScaler();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var subscription = session.Subscribe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        // Tell the client what's on screen (and playing) as soon as it connects.
        await SendAsync(socket, session.State, cts.Token);
        var nowPlaying = context.RequestServices.GetRequiredService<NowPlayingService>().Current;
        await SendAsync(socket, new NowPlayingEvent(nowPlaying), cts.Token);
        var textFocus = context.RequestServices.GetRequiredService<ITextFocus>().Current;
        await SendAsync(socket, new TextFocusEvent(textFocus), cts.Token);
        var scrollHint = context.RequestServices.GetRequiredService<ScrollHint>();
        await SendAsync(socket, new ScrollableEvent(scrollHint.Scrollable), cts.Token);
        var audio = context.RequestServices.GetRequiredService<HomeTV.Core.Audio.AudioOutputService>();
        if (audio.Enabled)
            await SendAsync(socket, audio.Current, cts.Token);

        var sendLoop = Task.Run(async () =>
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(cts.Token))
            {
                if (forward(evt))
                    await SendAsync(socket, evt, cts.Token);
            }
        }, cts.Token);

        try
        {
            while (await ReceiveTextAsync(socket, cts.Token) is { } message)
            {
                var command = RemoteCommand.TryParse(message);
                // Looked up per move so a change made on the TV applies without the phone reconnecting.
                if (command is MouseMoveCommand move && deviceId is not null && pairing.Find(deviceId) is { } device)
                    command = scaler.Scale(move, device.PointerSpeed);

                if (command is not null)
                    await dispatcher.DispatchAsync(command, cts.Token);
                else
                    logger.LogWarning("Ignoring unparseable message");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Client went away.
        }
        finally
        {
            await cts.CancelAsync();
            try { await sendLoop; } catch (OperationCanceledException) { } catch (WebSocketException) { }
        }

        if (socket.State == WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>Returns the next text message, or null once the socket closes. Oversized messages close it.</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, ct);
                return null;
            }

            var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            length += result.Count;
            if (result.EndOfMessage)
                return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, length) : "";
        }
    }

    private static Task SendAsync(WebSocket socket, HubEvent evt, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt, HomeTvJson.Options), WebSocketMessageType.Text, true, ct);
}
