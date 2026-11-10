using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MirrorDeck.Core;

/// <summary>A network interface a device could reach this PC on.</summary>
public sealed record LocalInterface(string Name, string Address, bool IsWireless, bool LooksVirtual);

/// <summary>Read-only look at the local network state. Nothing here opens a socket or needs elevation.</summary>
public static class NetworkProbe
{
    /// <summary>The fixed AirPlay ports that some program is already listening on, e.g. "TCP 7000".</summary>
    public static IReadOnlyList<string> FindBusyPorts()
    {
        var busy = new List<string>();
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            var tcp = ip.GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            var udp = ip.GetActiveUdpListeners().Select(e => e.Port).ToHashSet();
            busy.AddRange(EnginePorts.Tcp.Where(tcp.Contains).Select(p => $"TCP {p}"));
            busy.AddRange(EnginePorts.Udp.Where(udp.Contains).Select(p => $"UDP {p}"));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Not being able to look is not a reason to refuse to start; the engine reports a real conflict itself.
        }

        return busy;
    }

    public static bool IsTcpListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>IPv4 addresses of the interfaces that are up, physical adapters first.</summary>
    public static IReadOnlyList<LocalInterface> GetLocalInterfaces()
    {
        var result = new List<LocalInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var description = nic.Description ?? string.Empty;
                var virtualAdapter = LooksVirtual(nic.Name) || LooksVirtual(description);
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = address.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue; // no DHCP lease: not usable
                    result.Add(new LocalInterface(
                        nic.Name,
                        text,
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                        virtualAdapter));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // leave the list empty; the caller reports "no network"
        }

        return result.OrderBy(i => i.LooksVirtual).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksVirtual(string text)
    {
        string[] markers = ["virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "docker", "tap-", "tailscale", "wireguard", "zerotier", "vpn", "bluetooth"];
        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
