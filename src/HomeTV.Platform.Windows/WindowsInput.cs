using System.Runtime.InteropServices;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>Injects input with Win32 SendInput into whatever window has focus.</summary>
public sealed class WindowsInput(ILogger<WindowsInput> logger) : IInput
{
    private const int WheelDelta = 120;

    public void PressKey(RemoteKey key)
    {
        var (vk, extended) = Map(key);
        var flags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        Send(Key(vk, 0, flags), Key(vk, 0, flags | KEYEVENTF_KEYUP));
    }

    public void MoveMouse(int dx, int dy) => Send(Mouse(MOUSEEVENTF_MOVE, dx, dy));

    public void Click(MouseButton button)
    {
        var (down, up) = button == MouseButton.Right
            ? (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP)
            : (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
        Send(Mouse(down), Mouse(up));
    }

    /// <summary>Positive <paramref name="dy"/> scrolls down, matching a finger dragging up on the touchpad.</summary>
    public void Scroll(int dy) => Send(Mouse(MOUSEEVENTF_WHEEL, data: -dy * WheelDelta / 10));

    public void TypeText(string text)
    {
        // KEYEVENTF_UNICODE sends UTF-16 code units, so surrogate pairs go through as two units.
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(Key(0, unit, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send([.. inputs]);
    }

    private static (ushort Vk, bool Extended) Map(RemoteKey key) => key switch
    {
        RemoteKey.Up => (VK_UP, true),
        RemoteKey.Down => (VK_DOWN, true),
        RemoteKey.Left => (VK_LEFT, true),
        RemoteKey.Right => (VK_RIGHT, true),
        RemoteKey.Ok or RemoteKey.Enter => (VK_RETURN, false),
        // Escape is "back" in youtube.com/tv and exits full-screen players on Prime/Fubo.
        RemoteKey.Back => (VK_ESCAPE, false),
        RemoteKey.PlayPause => (VK_MEDIA_PLAY_PAUSE, false),
        RemoteKey.Next => (VK_MEDIA_NEXT_TRACK, false),
        RemoteKey.Previous => (VK_MEDIA_PREV_TRACK, false),
        RemoteKey.VolumeUp => (VK_VOLUME_UP, false),
        RemoteKey.VolumeDown => (VK_VOLUME_DOWN, false),
        RemoteKey.Mute => (VK_VOLUME_MUTE, false),
        RemoteKey.Backspace => (VK_BACK, false),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    private void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            logger.LogWarning("SendInput injected {Sent}/{Total} events (error {Error})", sent, inputs.Length, Marshal.GetLastWin32Error());
    }
}
