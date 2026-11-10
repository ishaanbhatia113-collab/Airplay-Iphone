using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.Validate());
        Assert.Equal(AccessMode.Open, settings.AccessMode);
        Assert.False(string.IsNullOrWhiteSpace(settings.ReceiverName));
    }

    [Theory]
    [InlineData("Living Room PC")]
    [InlineData("Zoë's PC")]
    [InlineData("书房电脑")]
    [InlineData("Den (upstairs) @ home")]
    [InlineData("a")]
    public void Accepts_ordinary_names(string name) => Assert.Null(AppSettings.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("-starts-with-dash")]
    [InlineData("has \"quotes\"")]
    [InlineData("back\\slash")]
    [InlineData("dotted.name")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    public void Rejects_names_the_engine_cannot_take(string name) => Assert.NotNull(AppSettings.ValidateName(name));

    [Fact]
    public void Name_length_is_limited_in_utf8_bytes_not_characters()
    {
        Assert.Null(AppSettings.ValidateName(new string('a', 50)));
        Assert.NotNull(AppSettings.ValidateName(new string('a', 51)));
        // 17 CJK characters are 51 bytes of UTF-8
        Assert.Null(AppSettings.ValidateName(new string('书', 16)));
        Assert.NotNull(AppSettings.ValidateName(new string('书', 17)));
    }

    [Theory]
    [InlineData("DESKTOP-4F2K.corp", "DESKTOP-4F2Kcorp")]
    [InlineData("  -odd \"name\"\\  ", "odd name")]
    [InlineData(null, "")]
    public void Sanitize_produces_valid_names(string? input, string expected)
    {
        var cleaned = AppSettings.Sanitize(input);
        Assert.Equal(expected, cleaned);
        if (cleaned.Length > 0) Assert.Null(AppSettings.ValidateName(cleaned));
    }

    [Fact]
    public void Sanitize_truncates_without_splitting_characters()
    {
        var cleaned = AppSettings.Sanitize(string.Concat(Enumerable.Repeat("😀", 40))); // 4 bytes each
        Assert.Null(AppSettings.ValidateName(cleaned));
        Assert.Equal(12, new System.Globalization.StringInfo(cleaned).LengthInTextElements);
    }

    [Theory]
    [InlineData("secret1", true)]
    [InlineData("pässword", true)]
    [InlineData("a\"b'c#d-e", true)]
    [InlineData("short", false)]
    [InlineData("has space", false)]
    [InlineData("-leading", false)]
    [InlineData("\"quoted", false)]
    [InlineData("'quoted", false)]
    [InlineData("#comment", false)]
    [InlineData(null, false)]
    public void Password_rules(string? password, bool ok) =>
        Assert.Equal(ok, AppSettings.ValidatePassword(password) is null);

    [Fact]
    public void Password_is_only_required_in_password_mode()
    {
        var settings = new AppSettings { AccessMode = AccessMode.PairingPin };
        Assert.Empty(settings.Validate());
        settings.AccessMode = AccessMode.Password;
        Assert.Single(settings.Validate());
        settings.Password = "longenough";
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Json_round_trip_keeps_settings_but_never_the_clear_text_password()
    {
        var settings = new AppSettings
        {
            ReceiverName = "Zoë's PC",
            AccessMode = AccessMode.Password,
            Password = "topsecret",
            ProtectedPassword = "AQAAANCM",
            VideoSink = VideoSink.Direct3D12,
            MaxFps = 30,
            EnableHls = true,
        };

        var json = settings.ToJson();
        Assert.DoesNotContain("topsecret", json);
        Assert.DoesNotContain("\"Password\":", json.Replace("\"ProtectedPassword\":", string.Empty));
        Assert.Contains("\"AccessMode\": \"Password\"", json);

        var loaded = AppSettings.FromJson(json);
        Assert.Equal("Zoë's PC", loaded.ReceiverName);
        Assert.Equal(AccessMode.Password, loaded.AccessMode);
        Assert.Equal(VideoSink.Direct3D12, loaded.VideoSink);
        Assert.Equal(30, loaded.MaxFps);
        Assert.True(loaded.EnableHls);
        Assert.Equal("AQAAANCM", loaded.ProtectedPassword);
        Assert.Null(loaded.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void Unreadable_settings_fall_back_to_defaults(string? json)
    {
        var settings = AppSettings.FromJson(json);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Unknown_properties_from_a_newer_version_are_ignored()
    {
        var settings = AppSettings.FromJson("{\"ReceiverName\":\"Den\",\"SomethingNew\":42}");
        Assert.Equal("Den", settings.ReceiverName);
    }

    [Fact]
    public void Clone_is_independent_and_keeps_the_password()
    {
        var original = new AppSettings { ReceiverName = "A", Password = "secret12", AccessMode = AccessMode.Password };
        var copy = original.Clone();
        original.ReceiverName = "B";
        Assert.Equal("A", copy.ReceiverName);
        Assert.Equal("secret12", copy.Password);
    }
}
