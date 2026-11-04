namespace HomeTV.Core.Platform;

/// <summary>A text field that has keyboard focus on the TV.</summary>
/// <param name="Name">What the field calls itself (its label or placeholder, e.g. "Search"), if anything.</param>
/// <param name="Secret">A password field: the phone masks what's typed.</param>
public sealed record TextField(string? Name, bool Secret);

/// <summary>Watches whether keyboard focus on the TV is in a text field, so the phone can bring up its keyboard.</summary>
public interface ITextFocus
{
    void Start();

    /// <summary>The focused text field, or null when focus is anywhere else.</summary>
    TextField? Current { get; }

    /// <summary>Raised from any thread when <see cref="Current"/> changes.</summary>
    event Action<TextField?>? Changed;

    /// <summary>
    /// Runs <paramref name="action"/> (a click) and returns the text field focus lands in, or null if it lands
    /// anywhere else. If focus doesn't move within <paramref name="timeout"/> (the click hit the field that already
    /// had focus, or something that takes no focus), returns what's focused now.
    /// </summary>
    Task<TextField?> FocusAfterAsync(Func<Task> action, TimeSpan timeout, CancellationToken ct = default);
}
