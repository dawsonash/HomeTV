namespace HomeTV.Core.Platform;

public enum RemoteKey
{
    Up,
    Down,
    Left,
    Right,
    Ok,
    Back,
    PlayPause,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    Mute,
    Backspace,
    Enter,
}

public enum MouseButton
{
    Left,
    Right,
}

/// <summary>Injects keyboard and mouse input into whatever window has focus.</summary>
public interface IInput
{
    void PressKey(RemoteKey key);
    void MoveMouse(int dx, int dy);
    void Click(MouseButton button);
    void Scroll(int dy);
    void TypeText(string text);
}
