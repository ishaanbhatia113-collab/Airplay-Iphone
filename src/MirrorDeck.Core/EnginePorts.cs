namespace MirrorDeck.Core;

/// <summary>
/// The engine is always started with the "p" option, which pins it to AirPlay's classic port set.
/// Fixed ports are what make precise firewall rules (and the rules in the MSIX manifest) possible;
/// without it the engine asks the OS for random ports on every start.
/// </summary>
public static class EnginePorts
{
    /// <summary>7000: RTSP/HTTP control channel. 7100: encrypted mirror video. 7001: reserved by the engine (currently unused).</summary>
    public static readonly IReadOnlyList<int> Tcp = [7000, 7001, 7100];

    /// <summary>6000: audio data. 6001: audio control. 7011: NTP timing.</summary>
    public static readonly IReadOnlyList<int> Udp = [6000, 6001, 7011];

    /// <summary>mDNS / DNS-SD (UDP, multicast 224.0.0.251 and ff02::fb). Shared with other responders.</summary>
    public const int Mdns = 5353;

    /// <summary>The port whose listener tells us that the engine is up.</summary>
    public const int Control = 7000;
}
