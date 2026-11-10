using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

/// <summary>
/// End-to-end checks against a real UxPlay binary: the generated configuration is accepted, the receiver
/// really shows up in an mDNS browse (what the AirPlay menu on an iPhone does), and it goes away again.
/// Skipped unless MIRRORDECK_TEST_ENGINE is set; see the README ("Tests").
/// </summary>
[Collection("real AirPlay ports")]
public sealed class EngineHostRealEngineTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("mirrordeck-real-").FullName;
    private readonly List<EngineHost> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts) host.Dispose();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The engine under test. UxPlay only switches off output buffering on Windows, so elsewhere the
    /// binary is run through coreutils' stdbuf to get the same line-by-line output the Windows app sees.
    /// </summary>
    private EngineLocation Engine(bool buffered = false)
    {
        var binary = Environment.GetEnvironmentVariable(EngineFactAttribute.Variable)!;
        if (!buffered && !OperatingSystem.IsWindows())
        {
            var wrapper = Path.Combine(_data, "uxplay-live.sh");
            File.WriteAllText(wrapper, $"#!/bin/sh\nexec stdbuf -o0 -e0 '{binary}' \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            binary = wrapper;
        }

        var found = EngineLocator.Find(binary, AppContext.BaseDirectory, out _);
        return found ?? throw new InvalidOperationException($"{EngineFactAttribute.Variable} does not point at a file");
    }

    private (EngineHost Host, HostRecorder Recorder, List<Process> Started) NewHost(
        Func<IReadOnlyList<string>>? busyPorts = null, TimeSpan? grace = null)
    {
        var started = new List<Process>();
        var host = new EngineHost(new EngineHostOptions
        {
            DataDirectory = _data,
            StartupTimeout = TimeSpan.FromSeconds(60),
            OutputGracePeriod = grace ?? TimeSpan.FromSeconds(30),
            RestartDelay = TimeSpan.FromMilliseconds(200),
            RequestGracefulStop = OperatingSystem.IsWindows() ? null : Unix.Interrupt,
            OnProcessStarted = started.Add,
            BusyPortsProbe = busyPorts ?? NetworkProbe.FindBusyPorts,
        });
        _hosts.Add(host);
        return (host, new HostRecorder(host), started);
    }

    /// <summary>No display or sound card on a build machine: send the decoded streams nowhere.</summary>
    private static AppSettings Settings(string name, AccessMode mode = AccessMode.Open, string? password = null) => new()
    {
        ReceiverName = name,
        AccessMode = mode,
        Password = password,
        VideoSink = VideoSink.Auto,
        ExtraEngineOptions = "vs fakesink\nas fakesink",
    };

    private static Task<IReadOnlyList<MdnsRecord>> Browse() =>
        MdnsBrowser.QueryAsync(TimeSpan.FromSeconds(3), "_airplay._tcp.local", "_raop._tcp.local");

    [EngineFact]
    public async Task Receiver_is_discoverable_under_its_name_and_disappears_when_stopped()
    {
        const string name = "Zoë's MirrorDeck Test PC";
        var (host, recorder, _) = NewHost();

        await host.StartAsync(Settings(name), Engine());
        var status = await recorder.WaitForStateAsync(ReceiverState.Ready, 60);
        Assert.True(status.LiveStatusAvailable);
        Assert.Equal([ReceiverState.Starting, ReceiverState.Ready], recorder.States);

        // the engine accepted every line of the generated startup file
        Assert.Contains("UxPlay: reading configuration from  engine.rc", recorder.Log);
        Assert.Contains("using network ports UDP 7011 6001 6000 TCP 7100 7000 7001", recorder.Log);
        Assert.Contains("public key storage (for persistence) is in receiver-key.pem", recorder.Log);
        Assert.True(File.Exists(Path.Combine(_data, EngineConfig.KeyFileName)));
        Assert.False(File.Exists(Path.Combine(_data, EngineConfig.RcFileName)));
        Assert.True(NetworkProbe.IsTcpListening(EnginePorts.Control));

        // what an iPhone sees when the AirPlay list is opened
        var records = await Browse();
        Assert.Contains(records, r => r.Type == "PTR" && r.Name == "_airplay._tcp.local" && r.Value == $"{name}._airplay._tcp.local");
        Assert.Contains(records, r => r.Type == "PTR" && r.Name == "_raop._tcp.local" && r.Value.EndsWith($"@{name}._raop._tcp.local", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Type == "SRV" && r.Name == $"{name}._airplay._tcp.local" && r.Value.EndsWith(":7000", StringComparison.Ordinal));
        var txt = Assert.Single(records, r => r.Type == "TXT" && r.Name == $"{name}._airplay._tcp.local");
        Assert.Contains("pw=false", txt.Value);   // open access

        // the control port answers
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, EnginePorts.Control);
        }

        await recorder.WaitForAsync(_ => recorder.Log.Contains("Accepted IPv4 client on socket"), 5);
        Assert.Equal(ReceiverState.Ready, host.Status.State);   // a bare TCP connection is not a session

        await host.StopAsync();
        Assert.Equal(ReceiverState.Stopped, host.Status.State);
        Assert.False(NetworkProbe.IsTcpListening(EnginePorts.Control));
        Assert.Empty(NetworkProbe.FindBusyPorts());
        if (!OperatingSystem.IsWindows()) Assert.Contains("Stopping RAOP Server", recorder.Log);

        var after = await Browse();
        Assert.DoesNotContain(after, r => r.Value.Contains(name, StringComparison.Ordinal));
    }

    [EngineFact]
    public async Task Renaming_restarts_under_the_new_name_and_access_control_is_advertised()
    {
        var (host, recorder, _) = NewHost();

        await host.StartAsync(Settings("MirrorDeck One"), Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        await host.StartAsync(Settings("MirrorDeck Two", AccessMode.Password, "correct-horse"), Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        var records = await Browse();
        Assert.DoesNotContain(records, r => r.Value.Contains("MirrorDeck One", StringComparison.Ordinal));
        var txt = Assert.Single(records, r => r.Type == "TXT" && r.Name == "MirrorDeck Two._airplay._tcp.local");
        Assert.Contains("pw=true", txt.Value);
        Assert.DoesNotContain("correct-horse", recorder.Log);

        await host.StopAsync();
    }

    [EngineFact]
    public async Task Pairing_pin_mode_starts_and_creates_no_register_until_a_device_pairs()
    {
        var (host, recorder, _) = NewHost();

        await host.StartAsync(Settings("MirrorDeck Pin", AccessMode.PairingPin), Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        var records = await Browse();
        var txt = Assert.Single(records, r => r.Type == "TXT" && r.Name == "MirrorDeck Pin._airplay._tcp.local");
        Assert.Contains("pw=true", txt.Value);

        await host.StopAsync();
    }

    [EngineFact]
    public async Task Pin_every_time_mode_starts()
    {
        var (host, recorder, _) = NewHost();
        await host.StartAsync(Settings("MirrorDeck Code", AccessMode.PinEveryTime), Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);
        await host.StopAsync();
    }

    [EngineFact]
    public async Task All_optional_switches_are_accepted_together()
    {
        var (host, recorder, _) = NewHost();
        var settings = Settings("MirrorDeck Everything");
        settings.StartFullscreen = true;
        settings.AudioEnabled = false;
        settings.NewConnectionTakesOver = false;
        settings.EnableH265 = true;
        settings.EnableHls = true;
        settings.MaxFps = 30;

        await host.StartAsync(settings, Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);
        await host.StopAsync();
    }

    [EngineFact]
    public async Task A_port_taken_by_another_program_is_caught_before_the_engine_starts()
    {
        using var squatter = new TcpListener(IPAddress.Any, EnginePorts.Control);
        squatter.Start();
        var (host, recorder, started) = NewHost();

        await host.StartAsync(Settings("MirrorDeck Busy"), Engine());

        Assert.Equal(ReceiverState.Failed, host.Status.State);
        Assert.Equal(EngineFault.PortInUse, host.Status.Fault);
        Assert.Contains("TCP 7000", host.Status.FaultDetail);
        Assert.Empty(started);
        Assert.Equal([ReceiverState.Starting, ReceiverState.Failed], recorder.States);
    }

    [EngineFact]
    public async Task A_port_conflict_reported_by_the_engine_itself_is_understood()
    {
        // Same situation with our own pre-check switched off, to prove the engine's message is recognised
        // and that the engine (which does not exit after this error) is not left running.
        using var squatter = new TcpListener(IPAddress.Any, EnginePorts.Control);
        squatter.Start();
        var (host, recorder, started) = NewHost(busyPorts: () => []);

        await host.StartAsync(Settings("MirrorDeck Busy"), Engine());
        var status = await recorder.WaitForStateAsync(ReceiverState.Failed, 60);

        Assert.Equal(EngineFault.PortInUse, status.Fault);
        Assert.Contains("already in use", status.FaultDetail);
        Assert.Contains("Error initialising socket", recorder.Log);
        var process = Assert.Single(started);
        await recorder.WaitForAsync(_ => recorder.Log.Contains("engine exited"), 10);
        Assert.Equal(ReceiverState.Failed, host.Status.State);
        _ = process;
    }

    [EngineFact]
    public async Task A_killed_engine_comes_back_by_itself()
    {
        var (host, recorder, started) = NewHost();

        await host.StartAsync(Settings("MirrorDeck Phoenix"), Engine());
        await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        started[0].Kill();
        await recorder.WaitForAsync(_ => started.Count == 2, 20);
        var status = await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        Assert.Contains("restarted", status.Notice);
        var records = await Browse();
        Assert.Contains(records, r => r.Value == "MirrorDeck Phoenix._airplay._tcp.local");

        await host.StopAsync();
        Assert.Equal(ReceiverState.Stopped, host.Status.State);
    }

    [EngineFact(bufferedOutputOnly: true)]
    public async Task An_engine_with_buffered_output_still_works_but_has_no_live_status()
    {
        var (host, recorder, _) = NewHost(grace: TimeSpan.FromSeconds(2));

        await host.StartAsync(Settings("MirrorDeck Buffered"), Engine(buffered: true));
        var status = await recorder.WaitForStateAsync(ReceiverState.Ready, 60);

        Assert.False(status.LiveStatusAvailable);
        var records = await Browse();
        Assert.Contains(records, r => r.Value == "MirrorDeck Buffered._airplay._tcp.local");

        await host.StopAsync();
        Assert.False(NetworkProbe.IsTcpListening(EnginePorts.Control));
    }
}
