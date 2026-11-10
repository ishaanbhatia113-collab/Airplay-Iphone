using System.Globalization;
using System.Text;

namespace MirrorDeck.Core;

/// <summary>
/// Translates <see cref="AppSettings"/> into a UxPlay startup file.
/// </summary>
/// <remarks>
/// Why a file instead of command-line switches: on Windows the engine's <c>main()</c> receives its arguments
/// converted to the ANSI code page, and it rejects anything that is not valid UTF-8. A receiver name such as
/// "Zoë's PC" would therefore stop the engine from starting. The startup file is read as raw bytes, so UTF-8
/// passes through untouched. For the same reason every path in the file is relative: the engine is started
/// with the data directory as its working directory, which keeps non-ASCII user profile paths out of it.
/// </remarks>
public static class EngineConfig
{
    public const string RcFileName = "engine.rc";
    public const string KeyFileName = "receiver-key.pem";
    public const string PairingRegisterFileName = "paired-devices.txt";

    /// <summary>The complete command line: everything else comes from the startup file.</summary>
    public static IReadOnlyList<string> CommandLine { get; } = ["-rc", RcFileName];

    /// <summary>Format: one option per line, without the leading dash; '#' starts a comment line.</summary>
    public static string BuildRcFile(AppSettings s)
    {
        var errors = s.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(s));

        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');

        Line("# Written by MirrorDeck on every start. Changes made here are overwritten.");
        Line($"n \"{s.ReceiverName}\"");   // quotes keep spaces; ValidateName guarantees there is no '"' or '\' inside
        Line("nh");                        // advertise exactly this name, without "@hostname"
        Line("p");                         // fixed ports, see EnginePorts
        Line($"key {KeyFileName}");        // persistent identity, so Apple devices recognise this PC after a restart
        Line("fps " + s.MaxFps.ToString(CultureInfo.InvariantCulture));

        switch (s.VideoSink)
        {
            // Naming the sink is what makes the engine add its Alt+Enter fullscreen toggle.
            case VideoSink.Direct3D11: Line("vs d3d11videosink"); break;
            case VideoSink.Direct3D12: Line("vs d3d12videosink"); break;
            case VideoSink.Auto: break;
        }

        if (s.StartFullscreen) Line("fs");
        if (!s.AudioEnabled) Line("as 0");
        if (s.NewConnectionTakesOver) Line("nohold");
        if (s.EnableH265) Line("h265");
        if (s.EnableHls) Line("hls");

        switch (s.AccessMode)
        {
            case AccessMode.Open:
                break;
            case AccessMode.PairingPin:
                Line("pin");
                // Without a register the engine trusts every client that claims to be paired already.
                Line($"reg {PairingRegisterFileName}");
                break;
            case AccessMode.PinEveryTime:
                Line("pw");
                break;
            case AccessMode.Password:
                Line("pw " + s.Password);   // ValidatePassword guarantees: no whitespace, no leading quote/dash
                break;
        }

        // EXTENSION POINT: every other engine feature is one more line here. Examples:
        //   "mp4 <name>"   record mirroring sessions to MP4
        //   "ca"           show cover art while music is streamed
        //   "vrtp <pipeline>"  hand the decrypted H.264 stream to another program (OBS) instead of rendering it
        // Until a feature has UI of its own, it can be tried through AppSettings.ExtraEngineOptions.
        foreach (var extra in ExtraLines(s.ExtraEngineOptions)) Line(extra);

        return sb.ToString();
    }

    /// <summary>The same file with the password masked, for logs and bug reports.</summary>
    public static string Redact(string rcFile)
    {
        var lines = rcFile.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("pw ", StringComparison.Ordinal)) lines[i] = "pw ********";
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> ExtraLines(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) yield break;
        foreach (var raw in extra.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Any(char.IsControl)) continue;
            // "rc" would make the engine read a different file, which is never what is wanted here
            if (line == "rc" || line.StartsWith("rc ", StringComparison.Ordinal)) continue;
            yield return line;
        }
    }
}
