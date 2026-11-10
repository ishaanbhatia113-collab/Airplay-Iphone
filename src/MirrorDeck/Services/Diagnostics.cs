using MirrorDeck.Core;

namespace MirrorDeck.Services;

internal enum Severity
{
    Ok,
    Info,
    Warning,
    Error,
}

internal enum FixKind
{
    None,
    AllowThroughFirewall,
    OpenNetworkSettings,
    ChooseEngine,
}

internal sealed record DiagnosticResult(Severity Severity, string Title, string Detail, FixKind Fix = FixKind.None);

/// <summary>
/// The checks behind the "Is everything set up?" list. They cover the reasons a PC does not show up on an
/// iPhone, roughly in the order in which they occur in practice.
/// </summary>
internal static class Diagnostics
{
    public static IReadOnlyList<DiagnosticResult> Run(EngineLocation? engine, IReadOnlyList<string> searched, bool receiverRunning)
    {
        var results = new List<DiagnosticResult>();

        // 1. the engine itself
        if (engine is null)
        {
            results.Add(new DiagnosticResult(
                Severity.Error,
                "AirPlay engine not found",
                "uxplay.exe is not installed next to the app. Build it with engine/build-engine.sh (see README), or point the app at an existing copy."
                + (searched.Count > 0 ? "\nLooked in: " + string.Join("; ", searched) : string.Empty),
                FixKind.ChooseEngine));
        }
        else
        {
            var origin = engine.Kind switch
            {
                EngineKind.Bundled => "bundled with the app",
                EngineKind.SourceTree => "build output in the source tree",
                EngineKind.Msys2 => "MSYS2 installation",
                _ => "custom path",
            };
            results.Add(new DiagnosticResult(Severity.Ok, "AirPlay engine found", $"{engine.ExePath} ({origin})"));
        }

        // 2. is there a network at all, and which address will devices see?
        var interfaces = NetworkProbe.GetLocalInterfaces();
        var real = interfaces.Where(i => !i.LooksVirtual).ToList();
        if (interfaces.Count == 0)
        {
            results.Add(new DiagnosticResult(
                Severity.Error,
                "No network connection",
                "This PC has no active network adapter with an address. Connect it to the same Wi-Fi or wired network as your Apple device."));
        }
        else
        {
            var list = string.Join(", ", (real.Count > 0 ? real : interfaces).Select(i => $"{i.Address} ({i.Name})"));
            results.Add(real.Count > 0
                ? new DiagnosticResult(
                    Severity.Ok,
                    "Connected to a network",
                    $"{list}. Your Apple device has to be on this same network (not on mobile data, a guest Wi-Fi or a VPN).")
                : new DiagnosticResult(
                    Severity.Warning,
                    "Only virtual or VPN adapters are connected",
                    $"{list}. Apple devices can only find this PC over a real Wi-Fi or Ethernet connection."));
        }

        // 3 + 4. network profile and firewall
        if (engine is not null)
        {
            var firewall = FirewallService.Inspect(engine.ExePath);

            if (firewall.OnPublicNetwork && !firewall.OnPrivateOrDomainNetwork && firewall.Verdict != FirewallVerdict.FirewallOff)
            {
                results.Add(new DiagnosticResult(
                    Severity.Warning,
                    "Windows treats this network as Public",
                    "On Public networks Windows hides the PC from other devices, so it will not appear in the AirPlay list. If this is your home or office network, change it to Private: Settings > Network & internet > (your connection) > Network profile type.",
                    FixKind.OpenNetworkSettings));
            }

            results.Add(firewall.Verdict switch
            {
                FirewallVerdict.Allowed => new DiagnosticResult(
                    Severity.Ok, "Windows Firewall allows the receiver", "Inbound connections to the engine are allowed on this network."),

                FirewallVerdict.FirewallOff => new DiagnosticResult(
                    Severity.Ok, "Windows Firewall is off for this network", "No rule is needed. If you use another firewall or security suite, allow uxplay.exe there."),

                FirewallVerdict.Blocked => new DiagnosticResult(
                    Severity.Error,
                    "Windows Firewall is blocking the receiver",
                    "There is a blocking rule for the engine. Windows adds one when \"Cancel\" is chosen on its \"allow this app\" prompt. Apple devices cannot see or reach this PC until it is replaced.",
                    FixKind.AllowThroughFirewall),

                FirewallVerdict.Missing when PackageInfo.IsPackaged => new DiagnosticResult(
                    Severity.Info,
                    "Firewall rules are managed by the app package",
                    "The rules were installed with the app. If devices still cannot find this PC, a security suite other than Windows Firewall may be filtering the connection."),

                FirewallVerdict.Missing => new DiagnosticResult(
                    Severity.Warning,
                    "Windows Firewall has no rule for the receiver yet",
                    $"Windows will ask when the receiver first starts; choose \"Allow\" for private networks. Or add the rule now (TCP {string.Join(", ", EnginePorts.Tcp)}; UDP {string.Join(", ", EnginePorts.Udp)} and {EnginePorts.Mdns}, limited to the engine).",
                    FixKind.AllowThroughFirewall),

                _ => new DiagnosticResult(
                    Severity.Info,
                    "Windows Firewall could not be checked",
                    (firewall.Error ?? "Unknown error.") + " If devices cannot find this PC, make sure inbound connections to uxplay.exe are allowed."),
            });
        }

        // 5. ports (only meaningful while our own engine is not the one holding them)
        if (!receiverRunning)
        {
            var busy = NetworkProbe.FindBusyPorts();
            if (busy.Count > 0)
            {
                results.Add(new DiagnosticResult(
                    Severity.Error,
                    "Another program is using the AirPlay ports",
                    $"{string.Join(", ", busy)} in use. Close other AirPlay receivers (AirServer, LonelyScreen, 5KPlayer, another copy of UxPlay) before starting."));
            }
        }

        return results;
    }
}
