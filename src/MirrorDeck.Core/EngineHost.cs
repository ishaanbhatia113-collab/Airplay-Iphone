using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MirrorDeck.Core;

public sealed class EngineHostOptions
{
    /// <summary>Writable directory for the startup file, the receiver key, the pairing register and the plugin cache.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// Generous on purpose: the very first start scans every GStreamer plugin (and antivirus scans each DLL).
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long to wait for the engine's own "listening" message before believing the port table instead
    /// (see <see cref="ReceiverStatus.LiveStatusAvailable"/>).
    /// </summary>
    public TimeSpan OutputGracePeriod { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How often a crashed engine is restarted before giving up (the counter resets after a stable minute).</summary>
    public int MaxAutoRestarts { get; init; } = 3;

    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the engine to shut down by itself (Windows: a console Ctrl+C event) so that it can withdraw its
    /// network advertisement. Return false if the request could not be delivered; the process is then killed.
    /// </summary>
    public Func<Process, bool>? RequestGracefulStop { get; init; }

    /// <summary>Called right after the process was created (Windows: put it into a kill-on-close job object).</summary>
    public Action<Process>? OnProcessStarted { get; init; }

    public Func<IReadOnlyList<string>> BusyPortsProbe { get; init; } = NetworkProbe.FindBusyPorts;

    public Func<bool> ListeningProbe { get; init; } = () => NetworkProbe.IsTcpListening(EnginePorts.Control);
}

/// <summary>
/// Owns the engine process: starts it, watches it, translates its output into <see cref="ReceiverStatus"/>,
/// restarts it after a crash and stops it cleanly.
/// </summary>
/// <remarks>
/// Events are raised on thread-pool threads while an internal lock is held, which keeps log lines and status
/// changes in order. Handlers must return quickly and must not block (post to the UI thread instead).
/// </remarks>
public sealed class EngineHost : IDisposable
{
    private const int RecentLineCount = 12;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly EngineHostOptions _options;
    private readonly EngineLogParser _parser = new();
    private readonly Queue<string> _recentLines = new();

    private Process? _process;
    private TaskCompletionSource? _exited;
    private int _generation;
    private bool _stopRequested;
    private bool _assumedListening;
    private bool _disposed;
    private string? _hostNotice;
    private int _restartCount;
    private DateTime _readySinceUtc;
    private AppSettings? _settings;
    private EngineLocation? _engine;
    private IReadOnlyList<string> _searched = [];
    private ReceiverStatus _status = ReceiverStatus.Stopped;

    public EngineHost(EngineHostOptions options) => _options = options;

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action<ReceiverStatus>? StatusChanged;

    /// <summary>Every line the engine prints, plus a few lines of our own (prefixed with "[MirrorDeck]").</summary>
    public event Action<string>? LogLine;

    public ReceiverStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    /// <summary>Process id of the running engine (its video window belongs to this process), or null.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process?.Id;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Starts the receiver, or restarts it with new settings when it is already running.</summary>
    /// <param name="settings">Copied; later changes to the object do not affect the running engine.</param>
    /// <param name="engine">Result of <see cref="EngineLocator.Find"/>; null when no engine was found.</param>
    /// <param name="searchedPaths">Where the locator looked, for the error message.</param>
    public async Task StartAsync(AppSettings settings, EngineLocation? engine, IReadOnlyList<string>? searchedPaths = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _settings = settings.Clone();
                _engine = engine;
                _searched = searchedPaths ?? [];
                _stopRequested = false;
                _restartCount = 0;
            }

            Launch(restartNotice: null);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Process? process;
        lock (_gate)
        {
            _stopRequested = true;
            _generation++;
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            Kill(process);
            process.Dispose();
        }

        DeleteRcFile();
    }

    // ------------------------------------------------------------------------------------------------

    private void Launch(string? restartNotice)
    {
        AppSettings settings;
        EngineLocation? engine;
        int generation;
        lock (_gate)
        {
            if (_stopRequested || _disposed || _settings is null) return;
            settings = _settings;
            engine = _engine;
            generation = ++_generation;
            _parser.Reset();
            _recentLines.Clear();
            _assumedListening = false;
            _hostNotice = restartNotice;
            SetStatus(new ReceiverStatus(ReceiverState.Starting, Notice: restartNotice));
        }

        // ---- checks that give a far better message than a failed process start would ----

        if (engine is null || !File.Exists(engine.ExePath))
        {
            var where = _searched.Count > 0 ? " Looked in: " + string.Join("; ", _searched) : string.Empty;
            Fail(generation, EngineFault.EngineNotFound,
                "The AirPlay engine (uxplay.exe) was not found. Build it with engine/build-engine.sh, or set its path in the advanced settings." + where);
            return;
        }

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            Fail(generation, EngineFault.InvalidSettings, string.Join(" ", problems));
            return;
        }

        var busy = _options.BusyPortsProbe();
        if (busy.Count > 0)
        {
            Fail(generation, EngineFault.PortInUse,
                $"Another program is already using {string.Join(", ", busy)}. Close other AirPlay receivers (AirServer, LonelyScreen, 5KPlayer, another MirrorDeck or UxPlay) and start again.");
            return;
        }

        string rcFile;
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            rcFile = EngineConfig.BuildRcFile(settings);
            File.WriteAllText(RcPath, rcFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(generation, EngineFault.InvalidSettings, $"Cannot write to the data folder {_options.DataDirectory}: {ex.Message}");
            return;
        }

        var startInfo = new ProcessStartInfo(engine.ExePath)
        {
            WorkingDirectory = _options.DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in EngineConfig.CommandLine) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in EngineLocator.BuildEnvironment(engine, _options.DataDirectory))
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(generation, e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(generation, e.Data);
        process.Exited += (_, _) => _ = OnExitedAsync(generation, process);

        Emit($"[MirrorDeck] starting {engine.ExePath} ({engine.Kind})");
        foreach (var line in EngineConfig.Redact(rcFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Emit("[MirrorDeck]   " + line);
        }

        try
        {
            lock (_gate)
            {
                if (generation != _generation || _stopRequested)
                {
                    process.Dispose();
                    return;
                }

                process.Start();
                _process = process;
                _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            DeleteRcFile();
            Fail(generation, EngineFault.EngineNotFound,
                $"The engine could not be started ({ex.Message}). If antivirus software quarantined uxplay.exe, add an exception for it.");
            return;
        }

        try
        {
            _options.OnProcessStarted?.Invoke(process);
        }
        catch (Exception ex)
        {
            Emit("[MirrorDeck] warning: " + ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = WatchStartupAsync(generation);
    }

    private void OnLine(int generation, string? line)
    {
        if (line is null) return;
        Process? toKill = null;
        var nowListening = false;
        lock (_gate)
        {
            if (generation != _generation) return;

            _recentLines.Enqueue(line);
            while (_recentLines.Count > RecentLineCount) _recentLines.Dequeue();
            RaiseLogLine(line);

            var wasListening = _parser.IsListening;
            if (!_parser.Feed(line)) return;
            if (_status.State is ReceiverState.Stopping or ReceiverState.Stopped or ReceiverState.Failed) return;

            if (_parser.Fault != EngineFault.None)
            {
                // The engine does not exit after some fatal errors (a busy port is one), so end it here.
                SetStatus(new ReceiverStatus(ReceiverState.Failed, Fault: _parser.Fault, FaultDetail: _parser.FaultDetail));
                toKill = _process;
            }
            else
            {
                nowListening = !wasListening && _parser.IsListening;
                if (nowListening) _readySinceUtc = DateTime.UtcNow;
                PublishParserState();
            }
        }

        if (toKill is not null)
        {
            Kill(toKill);
        }
        else if (nowListening)
        {
            DeleteRcFile(); // the engine has read it; do not leave a password lying around
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void PublishParserState()
    {
        if (!_parser.IsListening && !_assumedListening)
        {
            // still starting: keep the state, but surface notices (a missing decoder, for example)
            if (_status.State == ReceiverState.Starting && _parser.Notice is not null && _status.Notice != _parser.Notice)
            {
                _hostNotice = _parser.Notice;
                SetStatus(_status with { Notice = _parser.Notice });
            }

            return;
        }

        var state = _parser.Session switch
        {
            SessionState.Connecting => ReceiverState.Connecting,
            SessionState.PinRequired => ReceiverState.PinRequired,
            SessionState.Mirroring => ReceiverState.Mirroring,
            SessionState.AudioStreaming => ReceiverState.AudioStreaming,
            SessionState.VideoStreaming => ReceiverState.VideoStreaming,
            _ => ReceiverState.Ready,
        };

        // "restarted after a crash" stays visible until a device connects again
        if (state != ReceiverState.Ready) _hostNotice = null;

        SetStatus(new ReceiverStatus(
            state,
            _parser.ClientName,
            _parser.ClientModel,
            _parser.Pin,
            _parser.Notice ?? _hostNotice,
            LiveStatusAvailable: _parser.IsListening));
    }

    private async Task WatchStartupAsync(int generation)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(250).ConfigureAwait(false);
            Process? toKill = null;
            var assumed = false;
            lock (_gate)
            {
                if (generation != _generation || _status.State != ReceiverState.Starting) return;

                if (started.Elapsed > _options.OutputGracePeriod && _options.ListeningProbe())
                {
                    // The port is open but the engine never said so: its console output is block-buffered.
                    // UxPlay switches buffering off itself on Windows, except when its debug option "d" is
                    // used (and never on other systems). Mirroring works either way; only the live
                    // connection status is missing.
                    _assumedListening = true;
                    _readySinceUtc = DateTime.UtcNow;
                    assumed = true;
                    RaiseLogLine("[MirrorDeck] the engine is listening but its output is delayed; live connection status is unavailable (remove the \"d\" debug option if you added it)");
                    PublishParserState();
                }
                else if (started.Elapsed > _options.StartupTimeout)
                {
                    SetStatus(new ReceiverStatus(
                        ReceiverState.Failed,
                        Fault: EngineFault.StartTimeout,
                        FaultDetail: $"The engine did not start listening within {_options.StartupTimeout.TotalSeconds:0} seconds." + RecentOutput()));
                    toKill = _process;
                }
            }

            if (toKill is not null)
            {
                Kill(toKill);
                return;
            }

            if (assumed)
            {
                DeleteRcFile();
                return;
            }
        }
    }

    private async Task OnExitedAsync(int generation, Process process)
    {
        // Exited can arrive before the last lines of output; give the readers a moment to drain.
        try
        {
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        DeleteRcFile();

        var restart = false;
        string? notice = null;
        TaskCompletionSource? exited;
        lock (_gate)
        {
            if (generation != _generation)
            {
                process.Dispose();
                return;
            }

            exited = _exited;
            _process = null;
            _exited = null;
            RaiseLogLine($"[MirrorDeck] engine exited{(exitCode is null ? string.Empty : $" with code {exitCode}")}");

            if (_stopRequested)
            {
                SetStatus(ReceiverStatus.Stopped);
            }
            else if (_status.State != ReceiverState.Failed)
            {
                var wasUp = _parser.IsListening || _assumedListening;
                if (wasUp && DateTime.UtcNow - _readySinceUtc > TimeSpan.FromMinutes(1)) _restartCount = 0;

                if (wasUp && _restartCount < _options.MaxAutoRestarts)
                {
                    _restartCount++;
                    restart = true;
                    notice = $"The receiver stopped unexpectedly and was restarted (attempt {_restartCount} of {_options.MaxAutoRestarts}).";
                }
                else
                {
                    var fault = _parser.Fault != EngineFault.None ? _parser.Fault : EngineFault.Crashed;
                    var what = wasUp
                        ? "The receiver keeps stopping unexpectedly."
                        : "The engine exited while starting.";
                    SetStatus(new ReceiverStatus(
                        ReceiverState.Failed,
                        Fault: fault,
                        FaultDetail: _parser.FaultDetail ?? what + RecentOutput()));
                }
            }
        }

        process.Dispose();
        exited?.TrySetResult();

        if (!restart) return;
        await Task.Delay(_options.RestartDelay).ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            bool stillWanted;
            lock (_gate) stillWanted = generation == _generation && !_stopRequested && !_disposed;
            if (stillWanted) Launch(notice);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Caller holds the lifecycle semaphore.</summary>
    private async Task StopCoreAsync()
    {
        Process? process;
        Task? exited;
        lock (_gate)
        {
            _stopRequested = true;
            process = _process;
            exited = _exited?.Task;
            if (process is null)
            {
                _generation++; // cancels a pending start-up watchdog or delayed restart
                if (_status.State != ReceiverState.Stopped) SetStatus(ReceiverStatus.Stopped);
                return;
            }

            SetStatus(new ReceiverStatus(ReceiverState.Stopping));
        }

        var asked = false;
        try
        {
            asked = _options.RequestGracefulStop?.Invoke(process) ?? false;
        }
        catch (Exception ex)
        {
            Emit("[MirrorDeck] graceful stop failed: " + ex.Message);
        }

        if (exited is not null)
        {
            if (!asked || await Task.WhenAny(exited, Task.Delay(_options.StopTimeout)).ConfigureAwait(false) != exited)
            {
                Kill(process);
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
        }

        lock (_gate)
        {
            // Normally OnExitedAsync has done this already; make sure of it even if the event got lost.
            if (_process == process)
            {
                _generation++;
                _process = null;
                _exited = null;
            }

            if (_status.State != ReceiverState.Stopped) SetStatus(ReceiverStatus.Stopped);
        }

        DeleteRcFile();
    }

    private void Fail(int generation, EngineFault fault, string detail)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            RaiseLogLine($"[MirrorDeck] {fault}: {detail}");
            SetStatus(new ReceiverStatus(ReceiverState.Failed, Fault: fault, FaultDetail: detail));
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private string RecentOutput()
    {
        var lines = _recentLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return lines.Count == 0 ? string.Empty : " Last engine output: " + string.Join(" | ", lines.TakeLast(4));
    }

    /// <summary>Caller holds the lock.</summary>
    private void SetStatus(ReceiverStatus status)
    {
        if (status == _status) return;
        _status = status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // a misbehaving listener must not take the receiver down
        }
    }

    private void Emit(string line)
    {
        lock (_gate) RaiseLogLine(line);
    }

    /// <summary>Caller holds the lock.</summary>
    private void RaiseLogLine(string line)
    {
        try
        {
            LogLine?.Invoke(line);
        }
        catch
        {
        }
    }

    private string RcPath => Path.Combine(_options.DataDirectory, EngineConfig.RcFileName);

    private void DeleteRcFile()
    {
        try
        {
            File.Delete(RcPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still open in the engine; it is removed on the next attempt (ready, exit or stop)
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // already gone
        }
    }
}
