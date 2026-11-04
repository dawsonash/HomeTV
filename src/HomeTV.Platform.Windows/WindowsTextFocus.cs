using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Automation;
using HomeTV.Core.Platform;
using Microsoft.Extensions.Logging;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Follows keyboard focus across every app with UI Automation, the API screen readers use. Edge, WebView2 (the
/// launcher) and most native apps report their text fields this way; Chromium switches its accessibility tree on
/// when it sees a UIA client listening.
/// </summary>
public sealed class WindowsTextFocus(ILogger<WindowsTextFocus> logger) : ITextFocus, IDisposable
{
    /// <summary>
    /// How long focus must stay out of a text field before the phone hides its keyboard. Rides out a search box
    /// handing focus to its suggestion list and back, or a page re-rendering the field as you type.
    /// </summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private const int MaxNameLength = 80;

    private readonly Lock _lock = new();
    private readonly List<Channel<TextField?>> _waiters = [];
    private AutomationFocusChangedEventHandler? _handler;
    private TextField? _current;
    private long _sequence;

    public TextField? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public event Action<TextField?>? Changed;

    public void Start()
    {
        // UIA client calls belong on an MTA thread; the thread pool's are. Events arrive on UIA's own threads.
        _ = Task.Run(() =>
        {
            try
            {
                _handler = OnFocusChanged;
                Automation.AddAutomationFocusChangedEventHandler(_handler);
                logger.LogInformation("Watching for focused text fields");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Can't watch focus; the phone keyboard won't open by itself");
            }
        });
    }

    public void Dispose()
    {
        if (_handler is null)
            return;
        try
        {
            Automation.RemoveAutomationFocusChangedEventHandler(_handler);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Shutting down anyway.
        }
    }

    public async Task<TextField?> FocusAfterAsync(Func<Task> action, TimeSpan timeout, CancellationToken ct = default)
    {
        var events = Channel.CreateUnbounded<TextField?>();
        lock (_lock)
            _waiters.Add(events);
        try
        {
            await action();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var moved = false;
            try
            {
                while (true)
                {
                    var field = await events.Reader.ReadAsync(cts.Token);
                    if (field is not null)
                        return field;
                    // Focus went somewhere else first (Edge reports its page, then the field): listen a little longer.
                    moved = true;
                    cts.CancelAfter(SettleAfterOtherFocus);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return moved ? null : Current;
            }
        }
        finally
        {
            lock (_lock)
                _waiters.Remove(events);
        }
    }

    /// <summary>After focus lands outside a text field, how long a click waits for it to move on into one.</summary>
    private static readonly TimeSpan SettleAfterOtherFocus = TimeSpan.FromMilliseconds(80);

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var field = sender is AutomationElement element ? Describe(element) : null;

        // Anyone waiting on a click hears straight away, without the leave delay below.
        lock (_lock)
        {
            foreach (var waiter in _waiters)
                waiter.Writer.TryWrite(field);
        }

        if (field is not null)
        {
            Set(field, sequence);
            return;
        }

        // Out of a text field: wait a moment in case focus comes straight back.
        _ = Task.Delay(LeaveDelay).ContinueWith(_ => Set(null, sequence), TaskScheduler.Default);
    }

    /// <summary>Applies the reading taken at <paramref name="sequence"/> unless a newer focus change has come in since.</summary>
    private void Set(TextField? field, long sequence)
    {
        lock (_lock)
        {
            if (Interlocked.Read(ref _sequence) != sequence || _current == field)
                return;
            _current = field;
        }
        logger.LogDebug("Text focus: {Field}", field);
        try
        {
            Changed?.Invoke(field);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Text focus listener failed");
        }
    }

    /// <summary>The element as a <see cref="TextField"/> if it's somewhere you can type, otherwise null.</summary>
    private static TextField? Describe(AutomationElement element)
    {
        try
        {
            var info = element.Current;
            if (!info.IsEnabled)
                return null;

            var writable = element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                ? !((ValuePattern)pattern).Current.IsReadOnly
                : (bool?)null;

            // Edit: inputs and textareas (multi-line ones may only have TextPattern, so no ValuePattern is fine).
            // ComboBox: search boxes with suggestions (role=combobox, as on YouTube). Document: contenteditable.
            var editable = info.ControlType == ControlType.Edit
                ? writable != false
                : (info.ControlType == ControlType.ComboBox || info.ControlType == ControlType.Document) && writable == true;
            if (!editable)
                return null;

            var name = info.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                name = null;
            else if (name.Length > MaxNameLength)
                name = name[..MaxNameLength];
            return new TextField(name, info.IsPassword);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null; // Gone already.
        }
    }
}
