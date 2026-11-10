using System.Diagnostics;
using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

/// <summary>Runs only where PowerShell 7 ("pwsh") is installed, or MIRRORDECK_TEST_PWSH points at it.</summary>
public sealed class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "the stand-in netsh is a shell script";
        else if (FirewallRuleScriptTests.FindPowerShell() is null) Skip = "pwsh is not installed (or set MIRRORDECK_TEST_PWSH)";
    }
}

public sealed class FirewallRuleScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mirrordeck-fw-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string? FindPowerShell()
    {
        var configured = Environment.GetEnvironmentVariable("MIRRORDECK_TEST_PWSH");
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "pwsh"))
            .FirstOrDefault(File.Exists);
    }

    [Fact]
    public void Script_replaces_existing_rules_with_one_allow_rule_for_private_and_domain_networks()
    {
        var script = FirewallRuleScript.Build(@"C:\Program Files\MirrorDeck\engine\bin\uxplay.exe");
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("$ErrorActionPreference = 'Stop'", lines[0]);
        Assert.Equal(@"$program = 'C:\Program Files\MirrorDeck\engine\bin\uxplay.exe'", lines[1]);
        Assert.StartsWith("& netsh advfirewall firewall delete rule 'name=all' 'dir=in' \"program=$program\"", lines[2]);
        Assert.StartsWith("& netsh advfirewall firewall add rule 'name=MirrorDeck AirPlay receiver' 'dir=in' 'action=allow' \"program=$program\" 'profile=private,domain' 'enable=yes'", lines[3]);
        Assert.Equal("exit $LASTEXITCODE", lines[4]);
        Assert.DoesNotContain("public", script, StringComparison.OrdinalIgnoreCase);   // never opened on Public networks
    }

    [Fact]
    public void Quotes_in_the_path_cannot_end_the_string()
    {
        var script = FirewallRuleScript.Build(@"C:\Users\O'Brien\apps\uxplay.exe");
        Assert.Contains(@"$program = 'C:\Users\O''Brien\apps\uxplay.exe'", script);

        var typographic = FirewallRuleScript.Build("C:\\a\u2019b\\uxplay.exe");
        Assert.Contains("'C:\\a\u2019\u2019b\\uxplay.exe'", typographic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("C:\\a\nRemove-Item C:\\ -Recurse\\uxplay.exe")]
    public void Unusable_paths_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => FirewallRuleScript.Build(path));

    [Fact]
    public void Encoded_form_is_utf16_base64_as_powershell_expects()
    {
        var encoded = FirewallRuleScript.Encode("exit 3\n");
        Assert.Equal("exit 3\n", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    /// <summary>
    /// Executes the script with real PowerShell against a stand-in "netsh" that records its arguments.
    /// This is the part that cannot be judged by reading: how PowerShell splits and quotes arguments.
    /// </summary>
    [PowerShellFact]
    public void PowerShell_passes_netsh_exactly_the_intended_arguments()
    {
        // Spaces, an apostrophe, a dollar sign, a backtick and a comma: everything PowerShell could trip over.
        const string engine = @"C:\Users\O'Brien\My $Apps, `new`\MirrorDeck\engine\bin\uxplay.exe";
        var (exitCode, calls) = Run(engine, deleteExit: 1, addExit: 0);

        Assert.Equal(0, exitCode);   // "no rules to delete" is not a failure
        Assert.Equal(2, calls.Count);
        Assert.Equal(
            ["advfirewall", "firewall", "delete", "rule", "name=all", "dir=in", "program=" + engine],
            calls[0]);
        Assert.Equal(
            [
                "advfirewall", "firewall", "add", "rule",
                "name=MirrorDeck AirPlay receiver", "dir=in", "action=allow", "program=" + engine,
                "profile=private,domain", "enable=yes",
                "description=Lets Apple devices on your home or work network find this PC and mirror to it.",
            ],
            calls[1]);
    }

    [PowerShellFact]
    public void A_failing_add_is_reported_through_the_exit_code()
    {
        var (exitCode, calls) = Run(@"C:\MirrorDeck\engine\bin\uxplay.exe", deleteExit: 0, addExit: 1);
        Assert.Equal(1, exitCode);
        Assert.Equal(2, calls.Count);
    }

    private (int ExitCode, List<string[]> Calls) Run(string enginePath, int deleteExit, int addExit)
    {
        var log = Path.Combine(_root, "calls.txt");
        var netsh = Path.Combine(_root, "netsh");
        // one line per argument, a blank line after each invocation
        File.WriteAllText(netsh, $"""
            #!/bin/sh
            for a in "$@"; do printf '%s\n' "$a" >> '{log}'; done
            printf '\n' >> '{log}'
            case "$3" in delete) exit {deleteExit} ;; *) exit {addExit} ;; esac

            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(netsh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var startInfo = new ProcessStartInfo(FindPowerShell()!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(FirewallRuleScript.Encode(FirewallRuleScript.Build(enginePath)));
        startInfo.Environment["PATH"] = _root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(File.Exists(log), "netsh was never called. PowerShell said: " + output);
        var calls = File.ReadAllText(log)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(call => call.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        return (process.ExitCode, calls);
    }
}
