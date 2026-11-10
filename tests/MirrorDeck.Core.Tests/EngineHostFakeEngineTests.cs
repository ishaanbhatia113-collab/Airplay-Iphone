using System.Diagnostics;
using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

/// <summary>
/// Drives <see cref="EngineHost"/> with a shell script that prints what UxPlay prints. This exercises the
/// whole pipeline (process start, working directory, startup file, asynchronous output, state mapping,
/// restart and stop) without needing an Apple device.
/// </summary>
public sealed class EngineHostFakeEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mirrordeck-host-").FullName;
    private readonly List<EngineHost> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts) host.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DataDir => Path.Combine(_root, "data dir with spaces");

    private EngineLocation Script(string body)
    {
        var path = Path.Combine(_root, $"fake-engine-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/bash\n" + body.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new EngineLocation(path, _root, null, null, EngineKind.Custom);
    }

    private (EngineHost Host, HostRecorder Recorder) NewHost(Action<EngineHostOptionsBuilder>? configure = null)
    {
        var builder = new EngineHostOptionsBuilder(DataDir);
        configure?.Invoke(builder);
        var host = new EngineHost(builder.Build());
        _hosts.Add(host);
        return (host, new HostRecorder(host));
    }

    private sealed class EngineHostOptionsBuilder(string dataDir)
    {
        public TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        public TimeSpan OutputGracePeriod = TimeSpan.FromSeconds(20);
        public int MaxAutoRestarts = 3;
        public Func<Process, bool>? GracefulStop = Unix.Interrupt;
        public Func<bool> Listening = () => false;
        public Func<IReadOnlyList<string>> BusyPorts = () => [];
        public Action<Process>? OnStarted;

        public EngineHostOptions Build() => new()
        {
            DataDirectory = dataDir,
            StartupTimeout = StartupTimeout,
            OutputGracePeriod = OutputGracePeriod,
            StopTimeout = TimeSpan.FromSeconds(3),
            MaxAutoRestarts = MaxAutoRestarts,
            RestartDelay = TimeSpan.FromMilliseconds(50),
            RequestGracefulStop = GracefulStop,
            OnProcessStarted = OnStarted,
            ListeningProbe = Listening,
            BusyPortsProbe = BusyPorts,
        };
    }

    private const string Idle = """
        trap 'echo "Stopping RAOP Server..."; exit 0' INT TERM
        while true; do sleep 0.05; done
        """;

    [UnixFact]
    public async Task Start_passes_the_configuration_and_reaches_ready()
    {
        var engine = Script($"""
            echo "args=$*" > invocation.txt
            pwd >> invocation.txt
            cp engine.rc seen.rc
            echo "UxPlay: reading configuration from  engine.rc"
            echo "Initialized server socket(s)"
            {Idle}
            """);
        var (host, recorder) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "Zoë's PC", AccessMode = AccessMode.Password, Password = "hunter22" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Ready);

        Assert.True(status.LiveStatusAvailable);
        Assert.Equal([ReceiverState.Starting, ReceiverState.Ready], recorder.States);

        var invocation = File.ReadAllLines(Path.Combine(DataDir, "invocation.txt"));
        Assert.Equal("args=-rc engine.rc", invocation[0]);
        Assert.Equal(DataDir, invocation[1]);   // working directory = data directory, so relative paths resolve there

        var seen = File.ReadAllText(Path.Combine(DataDir, "seen.rc"));
        Assert.Contains("n \"Zoë's PC\"\n", seen);
        Assert.Contains("pw hunter22\n", seen);
        Assert.False(seen.StartsWith('﻿'));   // a byte order mark would corrupt the first option

        // the password must not stay on disk or show up in the log
        await WaitUntil(() => !File.Exists(Path.Combine(DataDir, EngineConfig.RcFileName)));
        Assert.DoesNotContain("hunter22", recorder.Log);
        Assert.Contains("pw ********", recorder.Log);

        await host.StopAsync();
        Assert.Equal(ReceiverState.Stopped, host.Status.State);
        Assert.Contains("Stopping RAOP Server", recorder.Log);   // it was asked nicely, not killed
        Assert.Equal(ReceiverState.Stopping, recorder.States[^2]);
    }

    [UnixFact]
    public async Task A_full_session_is_reported_step_by_step()
    {
        var engine = Script($$"""
            echo "Initialized server socket(s)"
            sleep 0.3
            echo "Accepted IPv4 client on socket 31, port 7000"
            echo "client sent PAIR-PIN-START request"
            echo '*** CLIENT MUST NOW ENTER PIN = "0397" AS AIRPLAY PASSWORD'
            sleep 0.3
            echo "connection request from Ishaan's iPhone (iPhone15,3) with deviceID = A4:83:E7:11:22:33"
            echo
            sleep 0.3
            echo "raop_rtp_mirror starting mirroring"
            echo "raop_rtp starting audio"
            echo "Begin streaming to GStreamer video pipeline"
            sleep 0.3
            echo "raop_rtp_mirror->running is no longer true"
            echo "Connection closed on socket 31"
            {{Idle}}
            """);
        var (host, recorder) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "Den", AccessMode = AccessMode.PairingPin }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Mirroring);
        await recorder.WaitForStateAsync(ReceiverState.Ready);

        Assert.Equal(
            [ReceiverState.Starting, ReceiverState.Ready, ReceiverState.PinRequired, ReceiverState.Connecting, ReceiverState.Mirroring, ReceiverState.Ready],
            recorder.States);

        var statuses = recorder.Statuses;
        Assert.Equal("0397", statuses[2].Pin);
        Assert.Null(statuses[3].Pin);
        Assert.Equal("Ishaan's iPhone", statuses[3].ClientName);
        Assert.Equal("iPhone15,3", statuses[4].ClientModel);
        Assert.Null(statuses[5].ClientName);
        Assert.True(statuses[4].HasClient);
        Assert.False(statuses[5].HasClient);
    }

    [UnixFact]
    public async Task Output_on_stderr_is_read_too()
    {
        var engine = Script($"""
            echo "Initialized server socket(s)" >&2
            {Idle}
            """);
        var (host, recorder) = NewHost();
        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Ready);
    }

    [UnixFact]
    public async Task An_engine_that_reports_a_busy_port_and_hangs_is_stopped()
    {
        // This is what UxPlay really does: it logs the error and then idles without a listener.
        var engine = Script($"""
            echo "*** ERROR: Error initialising socket 10048"
            {Idle}
            """);
        Process? started = null;
        var (host, recorder) = NewHost(o => o.OnStarted = p => started = p);

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed);

        Assert.Equal(EngineFault.PortInUse, status.Fault);
        Assert.Contains("7000", status.FaultDetail);
        Assert.NotNull(started);
        await WaitUntil(() => HasExited(started));
        await Task.Delay(300);
        Assert.Equal(ReceiverState.Failed, host.Status.State);   // the exit that follows must not hide the reason
        Assert.Equal(EngineFault.PortInUse, host.Status.Fault);
    }

    [UnixFact]
    public async Task Preflight_refuses_to_start_when_ports_are_taken()
    {
        var engine = Script("echo should-not-run > ran.txt\n" + Idle);
        var (host, recorder) = NewHost(o => o.BusyPorts = () => ["TCP 7000", "UDP 6000"]);

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);

        Assert.Equal(ReceiverState.Failed, host.Status.State);
        Assert.Equal(EngineFault.PortInUse, host.Status.Fault);
        Assert.Contains("TCP 7000, UDP 6000", host.Status.FaultDetail);
        Assert.False(File.Exists(Path.Combine(DataDir, "ran.txt")));
        Assert.Equal([ReceiverState.Starting, ReceiverState.Failed], recorder.States);
    }

    [UnixFact]
    public async Task Missing_engine_is_explained()
    {
        var (host, _) = NewHost();
        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine: null, searchedPaths: ["/opt/app/engine/bin/uxplay"]);

        Assert.Equal(EngineFault.EngineNotFound, host.Status.Fault);
        Assert.Contains("build-engine.sh", host.Status.FaultDetail);
        Assert.Contains("/opt/app/engine/bin/uxplay", host.Status.FaultDetail);

        var gone = new EngineLocation(Path.Combine(_root, "deleted"), _root, null, null, EngineKind.Custom);
        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, gone);
        Assert.Equal(EngineFault.EngineNotFound, host.Status.Fault);
    }

    [UnixFact]
    public async Task Invalid_settings_never_reach_the_engine()
    {
        var engine = Script("echo should-not-run > ran.txt\n" + Idle);
        var (host, _) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "bad.name" }, engine);

        Assert.Equal(EngineFault.InvalidSettings, host.Status.Fault);
        Assert.False(File.Exists(Path.Combine(DataDir, "ran.txt")));
    }

    [UnixFact]
    public async Task An_engine_that_dies_while_starting_fails_with_its_last_words()
    {
        var engine = Script("""
            echo "Required gstreamer plugin 'libav' not found"
            echo "*** ERROR: stopping"
            exit 1
            """);
        var (host, recorder) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed);

        Assert.Equal(EngineFault.MissingPlugin, status.Fault);
        Assert.Contains("libav", status.FaultDetail);
    }

    [UnixFact]
    public async Task An_unexplained_early_exit_quotes_the_engine_output()
    {
        var engine = Script("""
            echo 'invalid "-fps 999"; -fps n : max n=255, default n=30' >&2
            exit 1
            """);
        var (host, recorder) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed);

        Assert.Equal(EngineFault.Crashed, status.Fault);
        Assert.Contains("exited while starting", status.FaultDetail);
        Assert.Contains("-fps 999", status.FaultDetail);
        Assert.DoesNotContain(ReceiverState.Ready, recorder.States);   // no restart loop for start-up failures
    }

    [UnixFact]
    public async Task A_crash_after_start_is_restarted_and_then_given_up_on()
    {
        var engine = Script("""
            echo run >> runs.txt
            echo "Initialized server socket(s)"
            sleep 0.2
            exit 139
            """);
        var (host, recorder) = NewHost(o => o.MaxAutoRestarts = 2);

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed);

        Assert.Equal(EngineFault.Crashed, status.Fault);
        Assert.Contains("keeps stopping", status.FaultDetail);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(DataDir, "runs.txt")).Length);   // first run + 2 restarts
        Assert.Equal(3, recorder.States.Count(s => s == ReceiverState.Ready));
        Assert.Contains(recorder.Statuses, s => s.State == ReceiverState.Starting && s.Notice is not null && s.Notice.Contains("attempt 2 of 2"));
    }

    [UnixFact]
    public async Task Stop_during_a_restart_delay_wins()
    {
        var engine = Script("""
            echo run >> runs.txt
            echo "Initialized server socket(s)"
            exit 1
            """);
        var builderDelay = TimeSpan.FromSeconds(1);
        var host = new EngineHost(new EngineHostOptions
        {
            DataDirectory = DataDir,
            RestartDelay = builderDelay,
            BusyPortsProbe = () => [],
            ListeningProbe = () => false,
        });
        _hosts.Add(host);
        var recorder = new HostRecorder(host);

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        await WaitUntil(() => recorder.Log.Contains("engine exited"));
        await host.StopAsync();
        await Task.Delay(builderDelay + TimeSpan.FromMilliseconds(500));

        Assert.Equal(ReceiverState.Stopped, host.Status.State);
        Assert.Single(File.ReadAllLines(Path.Combine(DataDir, "runs.txt")));
    }

    [UnixFact]
    public async Task An_engine_that_ignores_the_polite_request_is_killed()
    {
        var engine = Script("""
            trap '' INT TERM
            echo "Initialized server socket(s)"
            while true; do sleep 0.05; done
            """);
        Process? started = null;
        var (host, recorder) = NewHost(o => o.OnStarted = p => started = p);

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Ready);
        var watch = Stopwatch.StartNew();
        await host.StopAsync();

        Assert.Equal(ReceiverState.Stopped, host.Status.State);
        Assert.True(HasExited(started!));
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.5, 9);   // waited for StopTimeout, then killed
    }

    [UnixFact]
    public async Task Start_while_running_restarts_with_the_new_settings()
    {
        var engine = Script($"""
            grep '^n ' engine.rc >> names.txt
            echo "Initialized server socket(s)"
            {Idle}
            """);
        var (host, recorder) = NewHost();

        await host.StartAsync(new AppSettings { ReceiverName = "First" }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Ready);
        await host.StartAsync(new AppSettings { ReceiverName = "Second" }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Ready);

        Assert.Equal(["n \"First\"", "n \"Second\""], File.ReadAllLines(Path.Combine(DataDir, "names.txt")));
        Assert.Equal(
            [ReceiverState.Starting, ReceiverState.Ready, ReceiverState.Stopping, ReceiverState.Stopped, ReceiverState.Starting, ReceiverState.Ready],
            recorder.States);
    }

    [UnixFact]
    public async Task Silent_engine_with_an_open_port_counts_as_ready_without_live_status()
    {
        // an engine whose console output is block-buffered: it works, but its log arrives late or never
        var engine = Script(Idle);
        var (host, recorder) = NewHost(o =>
        {
            o.OutputGracePeriod = TimeSpan.FromMilliseconds(600);
            o.Listening = () => true;
        });

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Ready);

        Assert.False(status.LiveStatusAvailable);
        Assert.False(File.Exists(Path.Combine(DataDir, EngineConfig.RcFileName)));
    }

    [UnixFact]
    public async Task Start_timeout()
    {
        var engine = Script("echo 'scanning plugins forever'\n" + Idle);
        Process? started = null;
        var (host, recorder) = NewHost(o =>
        {
            o.StartupTimeout = TimeSpan.FromMilliseconds(700);
            o.OnStarted = p => started = p;
        });

        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed);

        Assert.Equal(EngineFault.StartTimeout, status.Fault);
        Assert.Contains("scanning plugins forever", status.FaultDetail);
        await WaitUntil(() => HasExited(started!));
    }

    [UnixFact]
    public async Task Dispose_kills_the_engine()
    {
        var engine = Script("echo \"Initialized server socket(s)\"\n" + Idle);
        Process? started = null;
        var (host, recorder) = NewHost(o => o.OnStarted = p => started = p);
        await host.StartAsync(new AppSettings { ReceiverName = "Den" }, engine);
        await recorder.WaitForStateAsync(ReceiverState.Ready);
        var pid = started!.Id;

        host.Dispose();

        await WaitUntil(() => !ProcessExists(pid));
    }

    [UnixFact]
    public async Task Stop_when_not_running_is_harmless()
    {
        var (host, recorder) = NewHost();
        await host.StopAsync();
        await host.StopAsync();
        Assert.Equal(ReceiverState.Stopped, host.Status.State);
        Assert.Empty(recorder.States);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // disposed by the host after it exited
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, double seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("condition not met");
            await Task.Delay(25);
        }
    }
}
