namespace MirrorDeck.Core;

public enum ReceiverState
{
    Stopped,

    /// <summary>The engine process is launching (first start scans the GStreamer plugins, which takes a while).</summary>
    Starting,

    /// <summary>Advertised on the network and waiting for a device.</summary>
    Ready,

    /// <summary>A device has asked to connect; the handshake is in progress.</summary>
    Connecting,

    /// <summary>The device is showing a code prompt; the code to type is in <see cref="ReceiverStatus.Pin"/>.</summary>
    PinRequired,

    /// <summary>Screen mirroring (with or without audio).</summary>
    Mirroring,

    /// <summary>Audio-only AirPlay (music, podcasts).</summary>
    AudioStreaming,

    /// <summary>Video casting through HLS (YouTube app).</summary>
    VideoStreaming,

    Stopping,

    /// <summary>The receiver could not start or stopped unexpectedly; see <see cref="ReceiverStatus.Fault"/>.</summary>
    Failed,
}

public enum EngineFault
{
    None,

    /// <summary>uxplay.exe was not found.</summary>
    EngineNotFound,

    /// <summary>The settings cannot be turned into a valid engine configuration.</summary>
    InvalidSettings,

    /// <summary>One of the fixed AirPlay ports is taken by another program.</summary>
    PortInUse,

    /// <summary>The mDNS/DNS-SD advertisement could not be set up, so no device can discover this PC.</summary>
    DiscoveryFailed,

    /// <summary>A GStreamer plugin is missing from the engine bundle.</summary>
    MissingPlugin,

    /// <summary>The video or audio output could not be created (unsupported renderer, no audio device, ...).</summary>
    RendererFailed,

    /// <summary>The engine did not come up in time.</summary>
    StartTimeout,

    /// <summary>The engine process ended on its own.</summary>
    Crashed,
}

/// <summary>Immutable snapshot of everything the UI shows about the receiver.</summary>
public sealed record ReceiverStatus(
    ReceiverState State,
    string? ClientName = null,
    string? ClientModel = null,
    string? Pin = null,
    string? Notice = null,
    EngineFault Fault = EngineFault.None,
    string? FaultDetail = null,
    bool LiveStatusAvailable = true)
{
    public static ReceiverStatus Stopped { get; } = new(ReceiverState.Stopped);

    public bool IsRunning => State is not (ReceiverState.Stopped or ReceiverState.Failed);

    public bool HasClient => State is ReceiverState.Connecting or ReceiverState.PinRequired
        or ReceiverState.Mirroring or ReceiverState.AudioStreaming or ReceiverState.VideoStreaming;
}
