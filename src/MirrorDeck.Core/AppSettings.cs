using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorDeck.Core;

public enum AccessMode
{
    /// <summary>Anyone on the local network can connect.</summary>
    Open,

    /// <summary>
    /// Apple-style pairing: a 4-digit code is shown on the PC the first time a device connects.
    /// Paired devices are remembered (engine options "pin" + "reg").
    /// </summary>
    PairingPin,

    /// <summary>A fresh 4-digit code is shown on the PC for every connection (engine option "pw" without a value).</summary>
    PinEveryTime,

    /// <summary>A fixed password must be typed on the Apple device (engine option "pw &lt;password&gt;").</summary>
    Password,
}

public enum VideoSink
{
    /// <summary>Direct3D 11. Works on every Windows 10/11 machine; the default.</summary>
    Direct3D11,

    /// <summary>Direct3D 12. Recommended upstream for recent GPUs and drivers.</summary>
    Direct3D12,

    /// <summary>Let GStreamer choose. The Alt+Enter fullscreen toggle is not available in this mode.</summary>
    Auto,
}

/// <summary>User settings, persisted as JSON in %LOCALAPPDATA%\MirrorDeck\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>The engine accepts 4; its documentation says 6. We enforce the stricter value.</summary>
    public const int MinPasswordLength = 6;

    /// <summary>
    /// The RAOP service instance is "&lt;12 hex digits&gt;@&lt;name&gt;" and has to fit in one 63-byte DNS label,
    /// which leaves 50 bytes of UTF-8 for the name itself.
    /// </summary>
    public const int MaxNameBytes = 50;

    public string ReceiverName { get; set; } = DefaultReceiverName();

    public AccessMode AccessMode { get; set; } = AccessMode.Open;

    /// <summary>
    /// The password as stored on disk: encrypted by the app layer (DPAPI, current user) and base64 encoded.
    /// The clear text only ever lives in <see cref="Password"/>.
    /// </summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public string? Password { get; set; }

    public bool StartFullscreen { get; set; }

    public bool AudioEnabled { get; set; } = true;

    public VideoSink VideoSink { get; set; } = VideoSink.Direct3D11;

    /// <summary>Upper limit for the mirroring frame rate that is requested from the Apple device.</summary>
    public int MaxFps { get; set; } = 60;

    /// <summary>A newly connecting device replaces the current one instead of being refused.</summary>
    public bool NewConnectionTakesOver { get; set; } = true;

    /// <summary>Accept 4K HEVC mirroring. Needs a GPU/driver with an H.265 decoder; off by default.</summary>
    public bool EnableH265 { get; set; }

    /// <summary>Video casting (HLS) from the YouTube app only. Experimental upstream; off by default.</summary>
    public bool EnableHls { get; set; }

    /// <summary>Start the receiver as soon as the app opens.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Keep the display awake while a device is mirroring.</summary>
    public bool KeepDisplayAwake { get; set; } = true;

    /// <summary>Optional override for the engine location (full path of uxplay.exe).</summary>
    public string? EnginePathOverride { get; set; }

    /// <summary>
    /// Extra UxPlay options, one per line, written without the leading dash (for example "s 1280x720").
    /// They are appended last, so they override everything the UI sets. See engine documentation.
    /// </summary>
    public string? ExtraEngineOptions { get; set; }

    public static string DefaultReceiverName()
    {
        var machine = Sanitize(Environment.MachineName);
        return ValidateName(machine) is null ? machine : "MirrorDeck";
    }

    /// <summary>Makes an arbitrary string acceptable as a receiver name (best effort).</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c) || c is '"' or '\\' or '.') continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString().Trim().TrimStart('-').Trim();
        while (Encoding.UTF8.GetByteCount(cleaned) > MaxNameBytes && cleaned.Length > 0)
        {
            // drop whole text elements from the end so that we never cut a surrogate pair in half
            var info = new System.Globalization.StringInfo(cleaned);
            cleaned = info.SubstringByTextElements(0, info.LengthInTextElements - 1).TrimEnd();
        }

        return cleaned;
    }

    /// <summary>Returns a list of human-readable problems; empty when the settings can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var nameError = ValidateName(ReceiverName);
        if (nameError is not null) errors.Add(nameError);

        if (AccessMode == AccessMode.Password)
        {
            var passwordError = ValidatePassword(Password);
            if (passwordError is not null) errors.Add(passwordError);
        }

        if (MaxFps is < 10 or > 120) errors.Add("The frame rate limit must be between 10 and 120.");
        return errors;
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The receiver name cannot be empty.";
        if (name != name.Trim()) return "The receiver name cannot start or end with a space.";
        if (name.StartsWith('-')) return "The receiver name cannot start with a dash.";
        if (name.Any(char.IsControl)) return "The receiver name contains characters that cannot be used.";
        // '"' and '\' cannot be written safely to the engine's configuration file,
        // '.' would be read as a DNS label separator by the engine's mDNS responder.
        if (name.IndexOfAny(['"', '\\', '.']) >= 0) return "The receiver name cannot contain \" \\ or a dot.";
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            return $"The receiver name is too long (at most {MaxNameBytes} bytes; about {MaxNameBytes} Latin letters).";
        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"The password must have at least {MinPasswordLength} characters.";
        if (password.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "The password cannot contain spaces.";
        if (password[0] is '-' or '"' or '\'' or '#')
            return "The password cannot start with - \" ' or #.";
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Never throws: a missing or corrupt settings file yields the defaults.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public AppSettings Clone()
    {
        var copy = FromJson(ToJson());
        copy.Password = Password;
        return copy;
    }
}
