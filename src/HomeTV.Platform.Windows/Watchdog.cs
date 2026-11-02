using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeTV.Core.Watchdog;
using static HomeTV.Platform.Windows.NativeMethods;

namespace HomeTV.Platform.Windows;

/// <summary>
/// Keeps HomeTV running. As the TV's shell, a crashed or hung HomeTV means a black screen, so the installed
/// HomeTV.exe starts as this small supervisor and runs the real app as a child process (<see cref="WorkerFlag"/>).
/// It restarts the app when it exits (with backoff), kills and restarts it when <c>/healthz</c> stops answering,
/// and writes the app's log output to <see cref="LogPath"/>.
/// </summary>
public static class Watchdog
{
    /// <summary>Marks the child process that actually runs HomeTV.</summary>
    public const string WorkerFlag = "--worker";

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeTV", "logs", "hometv.log");

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(IEnumerable<string> args, int port, CancellationToken ct = default)
    {
        // One watchdog per desktop session; a second copy (double-click, a deploy racing the shell) just leaves.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\HomeTV.Watchdog", out var first);
        if (!first)
            return 0;

        using var log = new RollingLog(LogPath);
        var job = CreateKillOnCloseJob(log);
        var time = TimeProvider.System;
        var restarts = new RestartPolicy(time);
        var hang = new HangDetector(time);
        using var http = new HttpClient { Timeout = CheckTimeout };
        var healthUrl = $"http://127.0.0.1:{port}/healthz";

        log.Write($"watchdog: started (pid {Environment.ProcessId}), checking {healthUrl}");
        while (!ct.IsCancellationRequested)
        {
            using var worker = StartWorker(args, log);
            if (job != IntPtr.Zero && !AssignProcessToJobObject(job, worker.Handle))
                log.Write($"watchdog: couldn't add HomeTV to the job object (error {Marshal.GetLastWin32Error()})");
            restarts.OnStarted();
            hang.OnStarted();
            log.Write($"watchdog: HomeTV started (pid {worker.Id})");

            try
            {
                await SuperviseAsync(worker, http, healthUrl, hang, log, ct);
            }
            catch (OperationCanceledException)
            {
                worker.Kill();
                break;
            }

            var delay = restarts.OnExited();
            log.Write($"watchdog: HomeTV exited with code {worker.ExitCode}; restart #{restarts.Restarts} in {delay.TotalSeconds:0}s");
            await Task.Delay(delay, ct).ContinueWith(_ => { });
        }
        return 0;
    }

    private static async Task SuperviseAsync(Process worker, HttpClient http, string healthUrl, HangDetector hang, RollingLog log, CancellationToken ct)
    {
        var exited = worker.WaitForExitAsync(ct);
        while (true)
        {
            if (await Task.WhenAny(exited, Task.Delay(CheckInterval, ct)) == exited)
            {
                await exited; // Surfaces cancellation.
                return;
            }

            bool healthy;
            try
            {
                using var response = await http.GetAsync(healthUrl, ct);
                healthy = response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                healthy = false;
            }

            if (hang.Report(healthy))
            {
                log.Write($"watchdog: HomeTV failed {HangDetector.FailuresBeforeHang} health checks in a row; killing it");
                worker.Kill();
                await worker.WaitForExitAsync(ct);
                return;
            }
        }
    }

    private static Process StartWorker(IEnumerable<string> args, RollingLog log)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.ArgumentList.Add(WorkerFlag);

        var worker = new Process { StartInfo = startInfo };
        worker.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteRaw(e.Data); };
        worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteRaw(e.Data); };
        worker.Start();
        worker.BeginOutputReadLine();
        worker.BeginErrorReadLine();
        return worker;
    }

    /// <summary>
    /// A job that kills HomeTV when the watchdog dies (deploys and restart-hometv.ps1 stop it), so no orphaned
    /// app keeps port 8888. Silent breakaway keeps apps HomeTV launches (Edge, Steam) out of the job.
    /// </summary>
    private static IntPtr CreateKillOnCloseJob(RollingLog log)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK },
        };
        if (job == IntPtr.Zero || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(info)))
        {
            log.Write($"watchdog: couldn't create the job object (error {Marshal.GetLastWin32Error()})");
            return IntPtr.Zero;
        }
        // Deliberately never closed: the handle closing at process exit is what kills HomeTV.
        return job;
    }

    /// <summary>Append-only log file that rolls over to <c>.1</c> at 5 MB.</summary>
    private sealed class RollingLog : IDisposable
    {
        private const long MaxBytes = 5 * 1024 * 1024;
        private readonly string _path;
        private readonly Lock _lock = new();
        private StreamWriter _writer;

        public RollingLog(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = Open();
        }

        public void Write(string message) => WriteRaw($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

        public void WriteRaw(string line)
        {
            lock (_lock)
            {
                _writer.WriteLine(line);
                if (_writer.BaseStream.Length < MaxBytes)
                    return;
                _writer.Dispose();
                File.Move(_path, _path + ".1", overwrite: true);
                _writer = Open();
            }
        }

        public void Dispose()
        {
            lock (_lock)
                _writer.Dispose();
        }

        private StreamWriter Open() =>
            new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }
}
