using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MirrorDeck.Core;

namespace MirrorDeck.Services;

internal enum FirewallVerdict
{
    /// <summary>The check itself failed; say so rather than guess.</summary>
    Unknown,

    /// <summary>Windows Firewall is switched off for the active network.</summary>
    FirewallOff,

    /// <summary>An enabled inbound allow rule for the engine covers the active network.</summary>
    Allowed,

    /// <summary>Nothing allows the engine in. Windows will ask on first start; this is the state before that.</summary>
    Missing,

    /// <summary>
    /// An enabled inbound block rule exists for the engine. Windows creates one when "Cancel" is chosen
    /// on its "allow this app" prompt, and a block rule wins over any allow rule.
    /// </summary>
    Blocked,
}

/// <param name="OnPublicNetwork">At least one connected network is classified as Public.</param>
/// <param name="OnPrivateOrDomainNetwork">At least one connected network is Private or Domain.</param>
internal sealed record FirewallReport(
    FirewallVerdict Verdict,
    bool OnPublicNetwork,
    bool OnPrivateOrDomainNetwork,
    string? Error = null);

/// <summary>
/// Reads the Windows Firewall state through its scripting API (no elevation needed) and, when the user
/// asks for it, adds the inbound rule through one elevated PowerShell call.
/// </summary>
internal static class FirewallService
{
    public const string RuleName = "MirrorDeck AirPlay receiver";

    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;
    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolAny = 256;

    /// <summary>May take a few hundred milliseconds (it walks every firewall rule); call off the UI thread.</summary>
    public static FirewallReport Inspect(string enginePath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return new FirewallReport(FirewallVerdict.Unknown, false, false, "Windows Firewall API is not available.");
            dynamic policy = Activator.CreateInstance(type)!;

            int active = policy.CurrentProfileTypes;
            var onPublic = (active & ProfilePublic) != 0;
            var onPrivateOrDomain = (active & (ProfilePrivate | ProfileDomain)) != 0;

            var enabledProfiles = 0;
            foreach (var profile in new[] { ProfileDomain, ProfilePrivate, ProfilePublic })
            {
                if ((active & profile) == 0) continue;
                bool enabled = policy.FirewallEnabled[profile];
                if (enabled) enabledProfiles |= profile;
            }

            if (enabledProfiles == 0) return new FirewallReport(FirewallVerdict.FirewallOff, onPublic, onPrivateOrDomain);

            // Discovery on a Public network is something we never open ourselves, so judge the rules
            // against the private/domain profiles when one of them is active.
            var relevant = (enabledProfiles & (ProfilePrivate | ProfileDomain)) != 0
                ? enabledProfiles & (ProfilePrivate | ProfileDomain)
                : enabledProfiles;

            var blocked = false;
            var tcpAllowed = 0;
            var udpAllowed = 0;
            foreach (dynamic rule in policy.Rules)
            {
                string? application = rule.ApplicationName;
                if (string.IsNullOrEmpty(application)) continue;
                if (!SamePath(application, enginePath)) continue;
                if (!(bool)rule.Enabled || (int)rule.Direction != DirectionIn) continue;

                int profiles = rule.Profiles;
                if ((profiles & relevant) == 0) continue;

                if ((int)rule.Action == ActionBlock)
                {
                    blocked = true;
                    continue;
                }

                int protocol = rule.Protocol;
                if (protocol is ProtocolTcp or ProtocolAny) tcpAllowed |= profiles;
                if (protocol is ProtocolUdp or ProtocolAny) udpAllowed |= profiles;
            }

            if (blocked) return new FirewallReport(FirewallVerdict.Blocked, onPublic, onPrivateOrDomain);

            // Both are needed: TCP carries control and video, UDP carries discovery (mDNS), audio and timing.
            var allowed = (tcpAllowed & relevant) == relevant && (udpAllowed & relevant) == relevant;
            return new FirewallReport(allowed ? FirewallVerdict.Allowed : FirewallVerdict.Missing, onPublic, onPrivateOrDomain);
        }
        catch (Exception ex)
        {
            // COM errors, a third-party firewall that replaced the Windows one, group policy, ...
            return new FirewallReport(FirewallVerdict.Unknown, false, false, ex.Message);
        }
    }

    /// <summary>
    /// Replaces whatever inbound rules exist for the engine with a single allow rule for Private and
    /// Domain networks. Shows the UAC prompt.
    /// </summary>
    /// <returns>null on success, otherwise a message for the user.</returns>
    public static async Task<string?> AllowAsync(string enginePath)
    {
        // The rule is bound to the program instead of to port numbers: it then covers exactly what the
        // engine does (see EnginePorts for the list) and nothing else on the machine.
        // Every netsh argument is passed as one quoted PowerShell string. That matters twice: paths may
        // contain spaces, and an unquoted comma ("profile=private,domain") would be split by PowerShell.
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine($"$program = '{enginePath.Replace("'", "''")}'")
            // removes stale allow rules and, importantly, the block rules Windows adds after "Cancel";
            // netsh reports "no rules match" through its exit code, which is fine here
            .AppendLine("& netsh advfirewall firewall delete rule \"name=all\" \"dir=in\" \"program=$program\" | Out-Null")
            .AppendLine($"& netsh advfirewall firewall add rule \"name={RuleName}\" \"dir=in\" \"action=allow\" \"program=$program\" \"profile=private,domain\" \"enable=yes\" \"description=Lets Apple devices on your home or work network find this PC and mirror to it.\" | Out-Null")
            .AppendLine("exit $LASTEXITCODE")
            .ToString();

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // -EncodedCommand sidesteps every quoting problem a path could cause
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return "The elevated command could not be started.";
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? null : $"Windows Firewall refused the rule (exit code {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt was cancelled, so nothing was changed.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return "The firewall rule could not be added: " + ex.Message;
        }
    }

    private static bool SamePath(string a, string b)
    {
        static string Normalize(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            try
            {
                return System.IO.Path.GetFullPath(expanded);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return expanded;
            }
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
