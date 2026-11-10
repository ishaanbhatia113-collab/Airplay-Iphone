using System.Text;

namespace MirrorDeck.Core;

/// <summary>
/// The PowerShell script that adds the Windows Firewall rule for the engine. It is run elevated (UAC) by
/// the app; scripts/firewall-rule.ps1 does the same thing for manual or unattended set-up.
/// </summary>
/// <remarks>
/// Kept in the core library, although only the Windows app uses it, so that its quoting can be tested:
/// it embeds a path chosen by the user's install location into a script that runs as administrator.
/// </remarks>
public static class FirewallRuleScript
{
    public const string RuleName = "MirrorDeck AirPlay receiver";

    private const string Description = "Lets Apple devices on your home or work network find this PC and mirror to it.";

    /// <summary>
    /// Replaces whatever inbound rules exist for the engine with a single allow rule for Private and
    /// Domain networks.
    /// </summary>
    /// <remarks>
    /// The rule is bound to the program instead of to port numbers: it then covers exactly what the engine
    /// does (see <see cref="EnginePorts"/>) and nothing else on the machine.
    /// </remarks>
    public static string Build(string enginePath)
    {
        if (string.IsNullOrWhiteSpace(enginePath)) throw new ArgumentException("An engine path is required.", nameof(enginePath));
        if (enginePath.Any(char.IsControl)) throw new ArgumentException("The engine path contains control characters.", nameof(enginePath));

        // Inside a single-quoted PowerShell string only the quote itself is special. Windows file names
        // cannot contain the typographic quotes that PowerShell also accepts as string delimiters, but
        // doubling them costs nothing.
        var literal = enginePath
            .Replace("'", "''")
            .Replace("‘", "‘‘")
            .Replace("’", "’’");

        // Every netsh argument is one quoted PowerShell string, so that nothing in it (the spaces in a
        // path, the comma in "profile=private,domain") is left to PowerShell's argument parsing. The
        // tests run this script through real PowerShell and check what netsh receives.
        return new StringBuilder()
            .Append("$ErrorActionPreference = 'Stop'\n")
            .Append($"$program = '{literal}'\n")
            // Removes stale allow rules and, more importantly, the block rules Windows creates when
            // "Cancel" is chosen on its "allow this app" prompt: a block rule beats every allow rule.
            // netsh reports "no rules match" through its exit code, which is fine here.
            .Append("& netsh advfirewall firewall delete rule 'name=all' 'dir=in' \"program=$program\" | Out-Null\n")
            .Append($"& netsh advfirewall firewall add rule 'name={RuleName}' 'dir=in' 'action=allow' \"program=$program\" 'profile=private,domain' 'enable=yes' 'description={Description}' | Out-Null\n")
            .Append("exit $LASTEXITCODE\n")
            .ToString();
    }

    /// <summary>The value for <c>powershell.exe -EncodedCommand</c>, which sidesteps command-line quoting entirely.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
