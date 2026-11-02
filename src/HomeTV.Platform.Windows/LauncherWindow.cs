using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HomeTV.Platform.Windows;

public enum LauncherMode
{
    /// <summary>Borderless full-screen window that can't be closed from the keyboard: the TV experience.</summary>
    Fullscreen,

    /// <summary>A normal resizable window with dev tools; closing it stops HomeTV. For development.</summary>
    Windowed,

    /// <summary>No window; open the launcher URL in a browser yourself.</summary>
    None,
}

/// <summary>
/// The launcher page in a WebView2 window. It sits under the apps it launches, and comes back to the front when
/// they close. Runs on its own STA thread so the web server keeps the main thread.
/// </summary>
public sealed class LauncherWindow(ILogger<LauncherWindow> logger)
{
    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV", "launcher-webview");

    /// <summary>The UI thread counts as hung once its heartbeat is this stale.</summary>
    private const long HungAfterMs = 10_000;

    private LauncherForm? _form;

    /// <summary>
    /// False when the launcher's UI thread has stopped processing messages (frozen window). Feeds /healthz, so
    /// the watchdog restarts HomeTV. True when there's no window.
    /// </summary>
    public bool IsResponsive => _form is not { } form || Environment.TickCount64 - form.LastHeartbeat < HungAfterMs;

    /// <summary>Shows the launcher; completes when the window closes (or <paramref name="stopping"/> fires).</summary>
    public Task RunAsync(Uri url, LauncherMode mode, CancellationToken stopping)
    {
        var closed = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var form = new LauncherForm(url, mode, logger);
                _form = form;
                using var _ = stopping.Register(() => Post(f => f.CloseForShutdown()));
                Application.Run(form);
                closed.TrySetResult();
            }
            catch (Exception ex)
            {
                closed.TrySetException(ex);
            }
            finally
            {
                _form = null;
            }
        })
        {
            Name = "Launcher UI",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return closed.Task;
    }

    /// <summary>Brings the launcher in front of everything, with keyboard focus. Safe from any thread.</summary>
    public void BringToFront() => Post(f => TopLevelWindows.BringToFront(f.Handle));

    private void Post(Action<LauncherForm> action)
    {
        var form = _form;
        if (form is null || !form.IsHandleCreated)
            return;
        try
        {
            form.BeginInvoke(() => action(form));
        }
        catch (InvalidOperationException)
        {
            // Window is closing.
        }
    }

    private sealed class LauncherForm : Form
    {
        private readonly Uri _url;
        private readonly LauncherMode _mode;
        private readonly ILogger _logger;
        private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.Black };
        private readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 1000 };
        private long _lastHeartbeat = Environment.TickCount64;
        private bool _shuttingDown;

        public long LastHeartbeat => Interlocked.Read(ref _lastHeartbeat);

        public LauncherForm(Uri url, LauncherMode mode, ILogger logger)
        {
            _url = url;
            _mode = mode;
            _logger = logger;

            Text = "HomeTV";
            BackColor = System.Drawing.Color.Black;
            Controls.Add(_web);

            // Ticks only while the UI thread pumps messages, so a stale value means the window is frozen.
            _heartbeat.Tick += (_, _) => Interlocked.Exchange(ref _lastHeartbeat, Environment.TickCount64);
            _heartbeat.Start();

            if (mode == LauncherMode.Fullscreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = Screen.PrimaryScreen!.Bounds;
                ShowInTaskbar = false;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                Size = new System.Drawing.Size(1280, 760);
            }
        }

        public void CloseForShutdown()
        {
            _shuttingDown = true;
            Close();
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
                await _web.EnsureCoreWebView2Async(environment);

                var dev = _mode == LauncherMode.Windowed;
                var settings = _web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = dev;
                settings.AreDevToolsEnabled = dev;
                settings.AreBrowserAcceleratorKeysEnabled = dev;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;

                // The launcher never opens pages of its own.
                _web.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
                _web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _web.CoreWebView2.ProcessFailed += OnProcessFailed;
                _web.CoreWebView2.Navigate(_url.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Couldn't start WebView2. Is the WebView2 runtime installed?");
            }
        }

        // The page reconnects its own WebSocket, but a failed first load (server not up yet) needs a retry.
        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess || _shuttingDown)
                return;
            _logger.LogWarning("Launcher page failed to load ({Status}); retrying", args.WebErrorStatus);
            await Task.Delay(2000);
            if (!_shuttingDown)
                _web.CoreWebView2.Navigate(_url.ToString());
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (_shuttingDown)
                return;
            switch (args.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    _logger.LogWarning("Launcher page process failed ({Kind}); reloading", args.ProcessFailedKind);
                    _web.CoreWebView2.Reload();
                    break;
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    // The WebView2 control is dead for good. Exit and let the watchdog start a fresh HomeTV.
                    _logger.LogCritical("WebView2 browser process exited; restarting HomeTV");
                    Environment.Exit(3);
                    break;
                default:
                    _logger.LogWarning("WebView2 helper process failed ({Kind})", args.ProcessFailedKind);
                    break;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Started by the watchdog (a windowless background process), Windows may not give the launcher focus.
            if (_mode == LauncherMode.Fullscreen)
                TopLevelWindows.BringToFront(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // As the TV shell, Alt+F4 mustn't leave a black screen. Windows shutdown and HomeTV stopping still close it.
            if (_mode == LauncherMode.Fullscreen && !_shuttingDown && e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _heartbeat.Dispose();
                _web.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
