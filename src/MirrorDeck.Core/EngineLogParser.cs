using System.Text.RegularExpressions;

namespace MirrorDeck.Core;

public enum SessionState
{
    Idle,
    Connecting,
    PinRequired,
    Mirroring,
    AudioStreaming,
    VideoStreaming,
}

/// <summary>
/// Turns the engine's console output into receiver state.
/// </summary>
/// <remarks>
/// UxPlay has no IPC or status API; its log is the interface. Every pattern below was taken from the
/// format strings in the UxPlay 1.74 sources (uxplay.cpp, lib/httpd.c, lib/raop*.c, renderers/*.c) and the
/// file name is noted next to each one, so that they can be re-checked when the engine is updated.
/// The parser is deliberately forgiving: a line it does not know is simply ignored.
/// </remarks>
public sealed partial class EngineLogParser
{
    private readonly HashSet<int> _openSockets = [];

    /// <summary>True once the engine reports that its control port is listening.</summary>
    public bool IsListening { get; private set; }

    public SessionState Session { get; private set; }

    public string? ClientName { get; private set; }

    public string? ClientModel { get; private set; }

    public string? ClientDeviceId { get; private set; }

    /// <summary>The code the user has to type on the Apple device, while <see cref="Session"/> is PinRequired.</summary>
    public string? Pin { get; private set; }

    /// <summary>The most recent thing worth telling the user that is not fatal.</summary>
    public string? Notice { get; private set; }

    /// <summary>Set when the engine reported something it cannot recover from.</summary>
    public EngineFault Fault { get; private set; }

    public string? FaultDetail { get; private set; }

    /// <summary>Number of lines seen; zero after a few seconds means the engine's output is not reaching us.</summary>
    public int LinesSeen { get; private set; }

    public void Reset()
    {
        _openSockets.Clear();
        IsListening = false;
        Session = SessionState.Idle;
        ClientName = ClientModel = ClientDeviceId = Pin = Notice = FaultDetail = null;
        Fault = EngineFault.None;
        LinesSeen = 0;
    }

    /// <summary>Feeds one line of engine output.</summary>
    /// <returns>true when a property of this object changed.</returns>
    public bool Feed(string? rawLine)
    {
        if (rawLine is null) return false;
        var line = rawLine.TrimEnd('\r', '\n', ' ');
        if (line.Length == 0) return false;
        LinesSeen++;

        var before = Snapshot();
        Interpret(line);
        return before != Snapshot();
    }

    private (bool, SessionState, string?, string?, string?, string?, EngineFault, string?) Snapshot() =>
        (IsListening, Session, ClientName, ClientModel, Pin, Notice, Fault, FaultDetail);

    private void Interpret(string line)
    {
        Match m;

        // ---- start-up -------------------------------------------------------------------------------

        // lib/httpd.c: "Initialized server socket(s)"
        if (line.StartsWith("Initialized server socket", StringComparison.Ordinal))
        {
            IsListening = true;
            return;
        }

        // lib/httpd.c: "Error initialising socket %d" (logged as an error), printed when the control port
        // cannot be bound. The IPv6 variant is only a warning and is not matched here.
        // The engine keeps running without a listener after this, so the host has to stop it.
        if ((m = SocketInitError().Match(line)).Success)
        {
            var code = m.Groups["code"].Value;
            // 10048 = WSAEADDRINUSE, 98 = EADDRINUSE (Linux), 48 = EADDRINUSE (macOS/BSD)
            var inUse = code is "10048" or "98" or "48";
            SetFault(
                EngineFault.PortInUse,
                inUse
                    ? $"TCP port {EnginePorts.Control} is already in use by another program."
                    : $"The control port (TCP {EnginePorts.Control}) could not be opened (socket error {code}).");
            return;
        }

        // uxplay.cpp: "Could not initialize dnssd library!: error %d",
        //             "dnssd_register_raop failed with error code %d", "dnssd_register_airplay failed ..."
        if (line.Contains("Could not initialize dnssd library", StringComparison.Ordinal) ||
            (line.Contains("dnssd_register_", StringComparison.Ordinal) &&
             line.Contains("failed with error code", StringComparison.Ordinal)))
        {
            SetFault(EngineFault.DiscoveryFailed, StripErrorPrefix(line));
            return;
        }

        // uxplay.cpp: "dnssd: stopped servicing the DNS-SD socket (fd %d) for service %d"
        if (line.Contains("dnssd: stopped servicing", StringComparison.Ordinal))
        {
            SetFault(EngineFault.DiscoveryFailed, "The network advertisement stopped working (network change?).");
            return;
        }

        // renderers/audio_renderer.c check_plugins(): "Required gstreamer plugin '%s' not found".
        // The engine exits right after printing this.
        if ((m = MissingPlugin().Match(line)).Success)
        {
            SetFault(EngineFault.MissingPlugin, $"GStreamer plugin '{m.Groups["name"].Value}' is missing from the engine.");
            return;
        }

        // renderers/audio_renderer.c check_plugin_feature(): "Required gstreamer libav plugin feature '%s' not found:".
        // Not fatal for the engine: it carries on without that audio decoder.
        if ((m = MissingDecoder().Match(line)).Success)
        {
            Notice = $"The audio decoder '{m.Groups["name"].Value}' is missing from the engine, so sound will not work.";
            return;
        }

        // renderers/video_renderer.c: "GStreamer gst_parse_launch failed to create video pipeline %d"
        // renderers/audio_renderer.c: "gst_parse_launch error (audio %d):"
        // renderers/video_renderer.c: "Failed to initialize GStreamer video renderer %d"
        if (line.Contains("gst_parse_launch failed to create video pipeline", StringComparison.Ordinal) ||
            line.Contains("gst_parse_launch error (audio", StringComparison.Ordinal) ||
            line.Contains("Failed to initialize GStreamer video renderer", StringComparison.Ordinal))
        {
            SetFault(EngineFault.RendererFailed, "The video or audio output could not be created. Try a different renderer in the advanced settings.");
            return;
        }

        // ---- connections ----------------------------------------------------------------------------

        // lib/httpd.c: "Accepted %s client on socket %d, port %u"
        // Devices also open short-lived connections just to list receivers, so this alone means nothing.
        if ((m = Accepted().Match(line)).Success)
        {
            _openSockets.Add(int.Parse(m.Groups["fd"].Value));
            return;
        }

        // lib/httpd.c: "Connection closed on socket %d"
        if ((m = Closed().Match(line)).Success)
        {
            _openSockets.Remove(int.Parse(m.Groups["fd"].Value));
            if (_openSockets.Count == 0 && Session != SessionState.Idle) EndSession();
            return;
        }

        // uxplay.cpp report_client_request(): "connection request from %s (%s) with deviceID = %s"
        if ((m = ConnectionRequest().Match(line)).Success)
        {
            ClientName = m.Groups["name"].Value.Trim();
            ClientModel = m.Groups["model"].Value.Trim();
            ClientDeviceId = m.Groups["id"].Value;
            Notice = null;
            if (Session is SessionState.Idle or SessionState.PinRequired) Session = SessionState.Connecting;
            if (Session == SessionState.Connecting) Pin = null;
            return;
        }

        // lib/raop.c: "*****"nohold" feature: switch to new connection request from %s"
        if (line.Contains("switch to new connection request", StringComparison.Ordinal))
        {
            ClientName = ClientModel = ClientDeviceId = Pin = null;
            Session = SessionState.Connecting;
            Notice = "Another device took over the connection.";
            return;
        }

        // lib/raop.c (warning): "rejecting new connection request from %s"
        if (line.Contains("rejecting new connection request", StringComparison.Ordinal))
        {
            Notice = "A second device tried to connect and was refused because one is already connected.";
            return;
        }

        // uxplay.cpp: "*** attempt to connect by blocked client (clientID %s): DENIED"
        // uxplay.cpp: "client connections have been restricted to those with listed deviceID,"
        if (line.Contains("DENIED", StringComparison.Ordinal) ||
            line.Contains("client connections have been restricted", StringComparison.Ordinal))
        {
            Notice = "A device was refused because it is not on the allowed list.";
            return;
        }

        // ---- access control -------------------------------------------------------------------------

        // lib/raop_handlers.h: "*** CLIENT MUST NOW ENTER PIN = "%s" AS AIRPLAY PASSWORD"
        // (both for one-time pairing and for the code-on-every-connection mode)
        if ((m = PinPrompt().Match(line)).Success)
        {
            Pin = m.Groups["pin"].Value;
            Session = SessionState.PinRequired;
            Notice = null;
            return;
        }

        // lib/raop_handlers.h: "Client authentication %s" (success|failure)
        if (line.StartsWith("Client authentication success", StringComparison.Ordinal))
        {
            Pin = null;
            if (Session == SessionState.PinRequired) Session = SessionState.Connecting;
            return;
        }

        // lib/raop_handlers.h: "*** authentication failure: count = %u", "Client authentication failure",
        //                      "pair-pin-setup (step 3): client authentication failed"
        if (line.Contains("authentication failure", StringComparison.Ordinal) ||
            line.Contains("client authentication failed", StringComparison.Ordinal))
        {
            Notice = "The code or password entered on the device was not correct.";
            return;
        }

        // uxplay.cpp: "returning client's pairing registration not found: PK=%s"
        if (line.Contains("pairing registration not found", StringComparison.Ordinal))
        {
            Notice = "A device that is no longer paired tried to connect. Remove this PC on the device's AirPlay list and pair again.";
            return;
        }

        // ---- streams --------------------------------------------------------------------------------

        // lib/raop_rtp_mirror.c: "raop_rtp_mirror starting mirroring"
        // renderers/video_renderer.c: "Begin streaming to GStreamer video pipeline"
        if (line.StartsWith("raop_rtp_mirror starting mirroring", StringComparison.Ordinal) ||
            line.StartsWith("Begin streaming to GStreamer video pipeline", StringComparison.Ordinal))
        {
            Session = SessionState.Mirroring;
            Pin = null;
            Notice = null;
            return;
        }

        // lib/raop_rtp_mirror.c: "raop_rtp_mirror->running is no longer true" (the mirror stream was torn down)
        if (line.StartsWith("raop_rtp_mirror->running is no longer true", StringComparison.Ordinal))
        {
            if (Session == SessionState.Mirroring) EndSession();
            return;
        }

        // lib/raop_rtp.c: "raop_rtp starting audio". Mirroring sessions carry audio too; only an audio
        // stream on its own is "audio streaming".
        if (line.StartsWith("raop_rtp starting audio", StringComparison.Ordinal))
        {
            if (Session is SessionState.Idle or SessionState.Connecting or SessionState.PinRequired)
            {
                Session = SessionState.AudioStreaming;
                Pin = null;
                Notice = null;
            }

            return;
        }

        // uxplay.cpp: "********************on_video_play: location = %s*** start position %f ****..."
        if (line.Contains("on_video_play: location", StringComparison.Ordinal))
        {
            Session = SessionState.VideoStreaming;
            Pin = null;
            Notice = null;
            return;
        }

        // uxplay.cpp: "**************************on_video_stop", "hls video has sent EOS", " video has finished, %f"
        if (line.Contains("on_video_stop", StringComparison.Ordinal) ||
            line.Contains("hls video has sent EOS", StringComparison.Ordinal) ||
            line.Contains("video has finished", StringComparison.Ordinal))
        {
            if (Session == SessionState.VideoStreaming) EndSession();
            return;
        }

        // lib/raop.c: "ignoring AirPlay video streaming request (use option -hls to activate HLS support)"
        if (line.Contains("ignoring AirPlay video streaming request", StringComparison.Ordinal))
        {
            Notice = "A device tried to cast a video directly, which is switched off. Use Screen Mirroring instead, or enable YouTube casting in the advanced settings.";
            return;
        }

        // uxplay.cpp conn_reset(): "*** ERROR Unsupported HLS streaming source: (exit attempt to stream)"
        if (line.Contains("Unsupported HLS streaming source", StringComparison.Ordinal))
        {
            Notice = "This app's video cast is not supported (only the YouTube app is). Use Screen Mirroring instead.";
            EndSession(keepNotice: true);
            return;
        }

        // uxplay.cpp: "***ERROR lost connection with client (network problem?)" (two spellings exist)
        if (line.Contains("lost connection with client", StringComparison.Ordinal))
        {
            Notice = "The connection to the device was lost (it left the network, went to sleep, or Wi-Fi dropped).";
            EndSession(keepNotice: true);
            return;
        }

        // renderers/video_renderer.c: "video is h265 but the -h265 option was not used"
        if (line.Contains("video is h265 but the -h265 option was not used", StringComparison.Ordinal))
        {
            Notice = "The device sent 4K HEVC video, which is switched off. Enable it in the advanced settings.";
            return;
        }

        // renderers/*.c: "GStreamer error (video): %s %s" / "GStreamer error (audio): %s %s"
        if ((m = GstError().Match(line)).Success)
        {
            Notice = $"Playback problem ({m.Groups["kind"].Value}): {m.Groups["msg"].Value.Trim()}";
        }
    }

    private void EndSession(bool keepNotice = false)
    {
        Session = SessionState.Idle;
        ClientName = ClientModel = ClientDeviceId = Pin = null;
        if (!keepNotice) Notice = null;
    }

    private void SetFault(EngineFault fault, string detail)
    {
        // keep the first fault: later errors are usually consequences of it
        if (Fault != EngineFault.None) return;
        Fault = fault;
        FaultDetail = detail;
    }

    private static string StripErrorPrefix(string line)
    {
        const string prefix = "*** ERROR: ";
        return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
    }

    [GeneratedRegex(@"^\*\*\* ERROR: Error initialising socket (?<code>-?\d+)")]
    private static partial Regex SocketInitError();

    [GeneratedRegex(@"^Required gstreamer plugin '(?<name>[^']+)' not found")]
    private static partial Regex MissingPlugin();

    [GeneratedRegex(@"^Required gstreamer libav plugin feature '(?<name>[^']+)' not found")]
    private static partial Regex MissingDecoder();

    [GeneratedRegex(@"^Accepted IPv[46] client on socket (?<fd>\d+)")]
    private static partial Regex Accepted();

    [GeneratedRegex(@"^Connection closed on socket (?<fd>\d+)")]
    private static partial Regex Closed();

    // The device name is free text and may itself contain parentheses; the model ("iPhone15,3") cannot.
    [GeneratedRegex(@"^connection request from (?<name>.*) \((?<model>[^()]*)\) with deviceID = (?<id>\S+)")]
    private static partial Regex ConnectionRequest();

    [GeneratedRegex(@"CLIENT MUST NOW ENTER PIN = ""(?<pin>\d{4})""")]
    private static partial Regex PinPrompt();

    [GeneratedRegex(@"^GStreamer error \((?<kind>video|audio)\): (?<msg>.*)$")]
    private static partial Regex GstError();
}
