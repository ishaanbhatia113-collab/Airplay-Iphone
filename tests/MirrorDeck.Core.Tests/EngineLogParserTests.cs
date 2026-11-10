using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

/// <summary>
/// The transcripts are assembled from the format strings in the UxPlay 1.74 sources; the start-up block
/// is the literal output of the engine built from engine/build-engine.sh.
/// </summary>
public class EngineLogParserTests
{
    internal const string Startup = """
        UxPlay: reading configuration from  engine.rc
        UxPlay 1.74: An Open-Source AirPlay mirroring and audio-streaming server.
        public key storage (for persistence) is in receiver-key.pem
        using network ports UDP 7011 6001 6000 TCP 7100 7000 7001
        using system MAC address ee:26:c2:e1:a7:8c
        *** WARNING: Error initialising IPv6 socket 97
        *** WARNING: Continuing without IPv6 support
        Initialized server socket(s)
        """;

    internal const string MirrorSession = """
        Accepted IPv4 client on socket 31, port 7000
        Local : 192.168.1.20
        Remote: 192.168.1.42
        Client identified as User-Agent: AirPlay/775.3.1
        connection request from Ishaan’s iPhone (iPhone15,3) with deviceID = A4:83:E7:11:22:33

        raop_rtp_mirror starting mirroring
        raop_rtp starting audio
        ct=8 spf=480 usingScreen=1 isMedia=0  audioFormat=0x1000000
        raop_rtp_mirror: SPS+PPS header size = 44
        Begin streaming to GStreamer video pipeline
        """;

    private static EngineLogParser Parse(params string[] blocks)
    {
        var parser = new EngineLogParser();
        Feed(parser, blocks);
        return parser;
    }

    private static void Feed(EngineLogParser parser, params string[] blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var line in block.Split('\n')) parser.Feed(line);
        }
    }

    [Fact]
    public void Starts_idle_and_not_listening()
    {
        var parser = new EngineLogParser();
        Assert.False(parser.IsListening);
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Equal(EngineFault.None, parser.Fault);
    }

    [Fact]
    public void Startup_ends_in_listening_without_a_fault()
    {
        var parser = Parse(Startup);
        Assert.True(parser.IsListening);
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Equal(EngineFault.None, parser.Fault);   // the IPv6 warning is not a fault
        Assert.Null(parser.Notice);
        Assert.Equal(8, parser.LinesSeen);
    }

    [Fact]
    public void Feed_reports_whether_anything_changed()
    {
        var parser = new EngineLogParser();
        Assert.False(parser.Feed("using system MAC address ee:26:c2:e1:a7:8c"));
        Assert.True(parser.Feed("Initialized server socket(s)"));
        Assert.False(parser.Feed("Initialized server socket(s)"));
        Assert.False(parser.Feed(null));
        Assert.False(parser.Feed("   "));
    }

    [Fact]
    public void Mirroring_session_is_tracked_from_request_to_teardown()
    {
        var parser = Parse(Startup);

        Feed(parser, """
            Accepted IPv4 client on socket 31, port 7000
            connection request from Ishaan’s iPhone (iPhone15,3) with deviceID = A4:83:E7:11:22:33
            """);
        Assert.Equal(SessionState.Connecting, parser.Session);
        Assert.Equal("Ishaan’s iPhone", parser.ClientName);
        Assert.Equal("iPhone15,3", parser.ClientModel);
        Assert.Equal("A4:83:E7:11:22:33", parser.ClientDeviceId);

        Feed(parser, "raop_rtp_mirror starting mirroring", "raop_rtp starting audio");
        Assert.Equal(SessionState.Mirroring, parser.Session);   // audio inside a mirror session stays "mirroring"

        Feed(parser, "raop_rtp_mirror->running is no longer true");
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Null(parser.ClientName);

        Feed(parser, "Connection closed on socket 31");
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.True(parser.IsListening);
    }

    [Fact]
    public void Carriage_returns_from_windows_pipes_are_ignored()
    {
        var parser = Parse("Initialized server socket(s)\r", "raop_rtp_mirror starting mirroring\r");
        Assert.True(parser.IsListening);
        Assert.Equal(SessionState.Mirroring, parser.Session);
    }

    [Fact]
    public void Device_names_with_parentheses_are_parsed()
    {
        var parser = Parse(Startup, "connection request from Dad (work) iPad (iPad13,4) with deviceID = 11:22:33:44:55:66");
        Assert.Equal("Dad (work) iPad", parser.ClientName);
        Assert.Equal("iPad13,4", parser.ClientModel);
    }

    [Fact]
    public void Devices_that_only_look_at_the_airplay_list_do_not_count_as_connected()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 30, port 7000
            Connection closed on socket 30
            Accepted IPv6 client on socket 31, port 7000
            Connection closed on socket 31
            """);
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Null(parser.ClientName);
    }

    [Fact]
    public void Closing_the_last_socket_ends_the_session()
    {
        var parser = Parse(Startup, MirrorSession);
        Assert.Equal(SessionState.Mirroring, parser.Session);

        Feed(parser, "Accepted IPv4 client on socket 32, port 7000", "Connection closed on socket 32");
        Assert.Equal(SessionState.Mirroring, parser.Session);   // socket 31 is still open

        Feed(parser, "Connection closed on socket 31");
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Null(parser.ClientName);
    }

    [Fact]
    public void Audio_only_stream()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 31, port 7000
            connection request from MacBook Pro (Mac14,9) with deviceID = 11:22:33:44:55:66
            raop_rtp starting audio
            ct=2 spf=352 usingScreen=0 isMedia=1  audioFormat=0x40000
            """);
        Assert.Equal(SessionState.AudioStreaming, parser.Session);
        Assert.Equal("MacBook Pro", parser.ClientName);

        Feed(parser, "Connection closed on socket 31");
        Assert.Equal(SessionState.Idle, parser.Session);
    }

    [Fact]
    public void Pairing_pin_is_surfaced_and_cleared()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 31, port 7000
            client sent PAIR-PIN-START request
               ####    ####    ####    ####
            *** CLIENT MUST NOW ENTER PIN = "0397" AS AIRPLAY PASSWORD
            """);
        Assert.Equal(SessionState.PinRequired, parser.Session);
        Assert.Equal("0397", parser.Pin);   // leading zero preserved

        Feed(parser, """
            client requested pair-setup-pin, datalen = 85
            pair-setup-pin:  device_id = A4:83:E7:11:22:33
            connection request from Ishaan’s iPhone (iPhone15,3) with deviceID = A4:83:E7:11:22:33
            """);
        Assert.Equal(SessionState.Connecting, parser.Session);
        Assert.Null(parser.Pin);

        Feed(parser, "raop_rtp_mirror starting mirroring");
        Assert.Equal(SessionState.Mirroring, parser.Session);
    }

    [Fact]
    public void Cancelling_the_code_prompt_returns_to_idle()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 31, port 7000
            *** CLIENT MUST NOW ENTER PIN = "4821" AS AIRPLAY PASSWORD
            Connection closed on socket 31
            """);
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Null(parser.Pin);
    }

    [Fact]
    public void Wrong_code_then_right_code()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 31, port 7000
            *** CLIENT MUST NOW ENTER PIN = "4821" AS AIRPLAY PASSWORD
            *** authentication failure: count = 2
            Client authentication failure
            """);
        Assert.Equal(SessionState.PinRequired, parser.Session);
        Assert.Equal("4821", parser.Pin);
        Assert.Contains("not correct", parser.Notice);

        Feed(parser, "Client authentication success");
        Assert.Equal(SessionState.Connecting, parser.Session);
        Assert.Null(parser.Pin);
    }

    [Fact]
    public void Port_in_use_is_a_fault()
    {
        // literal output of a second engine instance started while the first one holds the ports
        var parser = Parse("""
            UxPlay 1.74: An Open-Source AirPlay mirroring and audio-streaming server.
            using network ports UDP 7011 6001 6000 TCP 7100 7000 7001
            using system MAC address fa:63:60:f5:2e:c9
            *** ERROR: Error initialising socket 98
            """);
        Assert.Equal(EngineFault.PortInUse, parser.Fault);
        Assert.Contains("7000", parser.FaultDetail);
        Assert.Contains("already in use", parser.FaultDetail);
        Assert.False(parser.IsListening);
    }

    [Theory]
    [InlineData("10048", "already in use")]   // WSAEADDRINUSE
    [InlineData("10013", "socket error 10013")] // WSAEACCES: reserved/excluded port range
    public void Windows_socket_errors(string code, string expected)
    {
        var parser = Parse($"*** ERROR: Error initialising socket {code}");
        Assert.Equal(EngineFault.PortInUse, parser.Fault);
        Assert.Contains(expected, parser.FaultDetail);
    }

    [Fact]
    public void Discovery_failures_are_faults()
    {
        Assert.Equal(EngineFault.DiscoveryFailed, Parse("*** ERROR: Could not initialize dnssd library!: error -65537").Fault);
        Assert.Equal(EngineFault.DiscoveryFailed, Parse("*** ERROR: dnssd_register_raop failed with error code -65548").Fault);
        Assert.Equal(EngineFault.DiscoveryFailed, Parse("*** ERROR: dnssd: stopped servicing the DNS-SD socket (fd 12) for service 1").Fault);
    }

    [Fact]
    public void The_first_fault_wins()
    {
        var parser = Parse(
            "*** ERROR: Error initialising socket 98",
            "*** ERROR: dnssd_register_raop failed with error code -1");
        Assert.Equal(EngineFault.PortInUse, parser.Fault);
    }

    [Fact]
    public void Missing_plugin_is_a_fault_but_a_missing_decoder_is_a_notice()
    {
        var plugin = Parse("Required gstreamer plugin 'videoparsersbad' not found", "Missing plugin is contained in  '[GStreamer 1.x]-plugins-bad'");
        Assert.Equal(EngineFault.MissingPlugin, plugin.Fault);
        Assert.Contains("videoparsersbad", plugin.FaultDetail);

        var decoder = Parse("Required gstreamer libav plugin feature 'avdec_aac' not found:");
        Assert.Equal(EngineFault.None, decoder.Fault);
        Assert.Contains("avdec_aac", decoder.Notice);
    }

    [Fact]
    public void Renderer_failure_is_a_fault()
    {
        var parser = Parse("""
            *** ERROR: GStreamer gst_parse_launch failed to create video pipeline 1
            *** error message from gst_parse_launch was:
            no element "d3d12videosink"
            """);
        Assert.Equal(EngineFault.RendererFailed, parser.Fault);
    }

    [Fact]
    public void Takeover_by_a_second_device()
    {
        var parser = Parse(Startup, MirrorSession, """
            Accepted IPv4 client on socket 35, port 7000
            *****"nohold" feature: switch to new connection request from 192.168.1.77
            Connection closed on socket 31
            connection request from Living Room iPad (iPad13,4) with deviceID = 11:22:33:44:55:66
            raop_rtp_mirror starting mirroring
            """);
        Assert.Equal(SessionState.Mirroring, parser.Session);
        Assert.Equal("Living Room iPad", parser.ClientName);
    }

    [Fact]
    public void Second_device_refused_while_busy()
    {
        var parser = Parse(Startup, MirrorSession, "*** WARNING: rejecting new connection request from 192.168.1.77");
        Assert.Equal(SessionState.Mirroring, parser.Session);
        Assert.Equal("Ishaan’s iPhone", parser.ClientName);
        Assert.Contains("refused", parser.Notice);
    }

    [Fact]
    public void Lost_connection_returns_to_idle_and_says_why()
    {
        var parser = Parse(Startup, MirrorSession, "***ERROR lost connection with client (network problem?)");
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Contains("lost", parser.Notice);
        Assert.Equal(EngineFault.None, parser.Fault);
        Assert.True(parser.IsListening);
    }

    [Fact]
    public void A_new_session_clears_the_old_notice()
    {
        var parser = Parse(Startup, MirrorSession, "*** ERROR lost connection with client (network problem?)", MirrorSession);
        Assert.Equal(SessionState.Mirroring, parser.Session);
        Assert.Null(parser.Notice);
    }

    [Fact]
    public void Video_cast_while_hls_is_off_explains_itself()
    {
        var parser = Parse(Startup, "ignoring AirPlay video streaming request (use option -hls to activate HLS support)");
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Contains("Screen Mirroring", parser.Notice);
    }

    [Fact]
    public void Hls_video_session()
    {
        var parser = Parse(Startup, """
            Accepted IPv4 client on socket 31, port 7000
            ********************on_video_play: location = http://localhost:7000/master.m3u8*** start position 0.000000 ********************
            """);
        Assert.Equal(SessionState.VideoStreaming, parser.Session);

        Feed(parser, "**************************on_video_stop");
        Assert.Equal(SessionState.Idle, parser.Session);
    }

    [Fact]
    public void Unsupported_hls_source()
    {
        var parser = Parse(Startup, "*** ERROR Unsupported HLS streaming source: (exit attempt to stream)");
        Assert.Contains("YouTube", parser.Notice);
        Assert.Equal(EngineFault.None, parser.Fault);
    }

    [Fact]
    public void Gstreamer_runtime_errors_become_notices()
    {
        var parser = Parse(Startup, MirrorSession, "GStreamer error (video): d3d11videosink_h264 Failed to open device");
        Assert.Equal(SessionState.Mirroring, parser.Session);
        Assert.StartsWith("Playback problem (video):", parser.Notice);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var parser = Parse(Startup, MirrorSession);
        parser.Reset();
        Assert.False(parser.IsListening);
        Assert.Equal(SessionState.Idle, parser.Session);
        Assert.Null(parser.ClientName);
        Assert.Equal(0, parser.LinesSeen);

        // sockets from before the reset must not keep a later session alive
        Feed(parser, Startup, MirrorSession, "Connection closed on socket 31");
        Assert.Equal(SessionState.Idle, parser.Session);
    }

    [Fact]
    public void Unknown_lines_are_ignored()
    {
        var parser = Parse(Startup);
        Assert.False(parser.Feed("some future log line that we have never seen"));
        Assert.False(parser.Feed("*** ERROR: something obscure"));
        Assert.Equal(EngineFault.None, parser.Fault);
    }
}
