using Microsoft.Extensions.Logging;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Ways to get back to the launcher from the box itself, inside any app, without the phone:
/// Ctrl+Alt+H, the keyboard's Home (Browser Home) key, or holding Back+Start on a controller.
/// </summary>
public sealed class HomeShortcuts(ILogger<HomeShortcuts> logger) : IDisposable
{
    public const string Help = "Ctrl+Alt+H, the Home key, or hold Back+Start (View+Menu) on a controller";

    private static readonly TimeSpan ControllerHold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ControllerPoll = TimeSpan.FromMilliseconds(100);
    private const uint MaxControllers = 4;
    private const ushort HomeCombo = XINPUT_GAMEPAD_BACK | XINPUT_GAMEPAD_START;

    private readonly CancellationTokenSource _stop = new();
    private uint _hotkeyThreadId;

    public void Start(Action onHome)
    {
        var hotkeys = new Thread(() => RunHotkeys(onHome)) { Name = "Home hotkeys", IsBackground = true };
        hotkeys.Start();
        _ = Task.Run(() => PollControllersAsync(onHome, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_hotkeyThreadId != 0)
            PostThreadMessage(_hotkeyThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    // Hotkeys registered without a window post WM_HOTKEY to the registering thread's queue.
    private void RunHotkeys(Action onHome)
    {
        _hotkeyThreadId = GetCurrentThreadId();
        (int Id, uint Modifiers, uint Vk, string Name)[] keys =
        [
            (1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'H', "Ctrl+Alt+H"),
            (2, MOD_NOREPEAT, VK_BROWSER_HOME, "Home key"),
        ];
        foreach (var key in keys)
        {
            if (!RegisterHotKey(IntPtr.Zero, key.Id, key.Modifiers, key.Vk))
                logger.LogWarning("Couldn't register {Key} (another app may own it)", key.Name);
        }

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    logger.LogInformation("Home shortcut pressed on the box");
                    Fire(onHome);
                }
            }
        }
        finally
        {
            foreach (var key in keys)
                UnregisterHotKey(IntPtr.Zero, key.Id);
        }
    }

    private async Task PollControllersAsync(Action onHome, CancellationToken ct)
    {
        // When each controller started holding the combo; fires once per hold.
        var heldSince = new DateTime?[MaxControllers];
        var fired = new bool[MaxControllers];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                for (uint i = 0; i < MaxControllers; i++)
                {
                    var holding = XInputGetState(i, out var state) == ERROR_SUCCESS
                        && (state.Gamepad.wButtons & HomeCombo) == HomeCombo;
                    if (!holding)
                    {
                        heldSince[i] = null;
                        fired[i] = false;
                        continue;
                    }

                    heldSince[i] ??= DateTime.UtcNow;
                    if (!fired[i] && DateTime.UtcNow - heldSince[i] >= ControllerHold)
                    {
                        fired[i] = true;
                        logger.LogInformation("Controller {Index} held Back+Start: going home", i);
                        Fire(onHome);
                    }
                }
                await Task.Delay(ControllerPoll, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DllNotFoundException)
        {
            logger.LogWarning("XInput isn't available, so the controller Home combo is off");
        }
    }

    private void Fire(Action onHome)
    {
        try
        {
            onHome();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Going home failed");
        }
    }
}
