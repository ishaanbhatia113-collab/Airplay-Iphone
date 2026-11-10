using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

public class EngineConfigTests
{
    private static string[] Lines(AppSettings settings) =>
        EngineConfig.BuildRcFile(settings).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Default_settings_produce_the_expected_file()
    {
        var lines = Lines(new AppSettings { ReceiverName = "Living Room PC" });

        Assert.StartsWith("#", lines[0]);
        Assert.Equal(
            ["n \"Living Room PC\"", "nh", "p", "key receiver-key.pem", "fps 60", "vs d3d11videosink", "nohold"],
            lines.Skip(1));
    }

    [Fact]
    public void The_command_line_only_names_the_startup_file()
    {
        // Everything user-controlled goes through the file: see the remarks on EngineConfig.
        Assert.Equal(["-rc", "engine.rc"], EngineConfig.CommandLine);
    }

    [Fact]
    public void The_file_uses_unix_line_endings_and_plain_ascii_paths()
    {
        var file = EngineConfig.BuildRcFile(new AppSettings { ReceiverName = "Zoë's PC", AccessMode = AccessMode.PairingPin });
        Assert.DoesNotContain('\r', file);
        Assert.EndsWith("\n", file);
        Assert.Contains("n \"Zoë's PC\"\n", file);
        // no drive letters or directory separators: every path is relative to the working directory
        Assert.DoesNotContain(":\\", file);
        Assert.DoesNotContain('/', file);
    }

    [Fact]
    public void Options_follow_the_settings()
    {
        var lines = Lines(new AppSettings
        {
            ReceiverName = "Den",
            StartFullscreen = true,
            AudioEnabled = false,
            NewConnectionTakesOver = false,
            EnableH265 = true,
            EnableHls = true,
            VideoSink = VideoSink.Direct3D12,
            MaxFps = 30,
        });

        Assert.Contains("fs", lines);
        Assert.Contains("as 0", lines);
        Assert.Contains("h265", lines);
        Assert.Contains("hls", lines);
        Assert.Contains("vs d3d12videosink", lines);
        Assert.Contains("fps 30", lines);
        Assert.DoesNotContain("nohold", lines);
    }

    [Fact]
    public void Automatic_renderer_writes_no_videosink_line()
    {
        var lines = Lines(new AppSettings { ReceiverName = "Den", VideoSink = VideoSink.Auto });
        Assert.DoesNotContain(lines, l => l.StartsWith("vs ", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_mode_has_no_access_control_lines()
    {
        var lines = Lines(new AppSettings { ReceiverName = "Den", AccessMode = AccessMode.Open });
        Assert.DoesNotContain(lines, l => l == "pin" || l == "pw" || l.StartsWith("pw ") || l.StartsWith("reg "));
    }

    [Fact]
    public void Pairing_pin_always_comes_with_a_register()
    {
        var lines = Lines(new AppSettings { ReceiverName = "Den", AccessMode = AccessMode.PairingPin });
        Assert.Contains("pin", lines);
        Assert.Contains("reg paired-devices.txt", lines);
    }

    [Fact]
    public void Pin_every_time_is_the_password_option_without_a_value()
    {
        var lines = Lines(new AppSettings { ReceiverName = "Den", AccessMode = AccessMode.PinEveryTime });
        Assert.Contains("pw", lines);
        Assert.DoesNotContain("pin", lines);
    }

    [Fact]
    public void Password_mode_writes_the_password_and_redact_hides_it()
    {
        var file = EngineConfig.BuildRcFile(new AppSettings
        {
            ReceiverName = "Den",
            AccessMode = AccessMode.Password,
            Password = "s3cret-pw",
        });

        Assert.Contains("\npw s3cret-pw\n", file);
        var redacted = EngineConfig.Redact(file);
        Assert.DoesNotContain("s3cret-pw", redacted);
        Assert.Contains("pw ********", redacted);
        Assert.Contains("n \"Den\"", redacted);
    }

    [Fact]
    public void Redact_leaves_the_valueless_pw_option_alone()
    {
        var file = EngineConfig.BuildRcFile(new AppSettings { ReceiverName = "Den", AccessMode = AccessMode.PinEveryTime });
        Assert.Equal(file, EngineConfig.Redact(file));
    }

    [Fact]
    public void Invalid_settings_are_refused()
    {
        Assert.Throws<ArgumentException>(() => EngineConfig.BuildRcFile(new AppSettings { ReceiverName = "bad\"name" }));
        Assert.Throws<ArgumentException>(() => EngineConfig.BuildRcFile(new AppSettings
        {
            ReceiverName = "Den",
            AccessMode = AccessMode.Password,
            Password = "x",
        }));
    }

    [Fact]
    public void Extra_options_are_appended_last_and_cleaned_up()
    {
        var lines = Lines(new AppSettings
        {
            ReceiverName = "Den",
            ExtraEngineOptions = "  -s 1280x720\r\n\r\n# a comment\nrc other.rc\nrc\n--vs fakesink\nmp4 session",
        });

        Assert.Equal(["s 1280x720", "vs fakesink", "mp4 session"], lines[^3..]);
        Assert.DoesNotContain(lines, l => l.StartsWith("rc", StringComparison.Ordinal));
    }
}
