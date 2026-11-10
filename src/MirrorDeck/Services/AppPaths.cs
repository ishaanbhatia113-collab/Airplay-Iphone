using System.IO;

namespace MirrorDeck.Services;

/// <summary>
/// Everything the app writes lives under %LOCALAPPDATA%\MirrorDeck. When the app is installed as an MSIX
/// package, Windows transparently redirects this to the package's private storage, so the same code
/// works packaged and unpackaged, and the install directory is never written to.
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDeck");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Working directory of the engine: receiver key, pairing register, GStreamer plugin cache.</summary>
    public static string EngineDataDirectory => Path.Combine(DataDirectory, "engine");

    public static string CrashLog => Path.Combine(DataDirectory, "crash.log");

    /// <summary>What the engine printed since the app was started; replaced on every start of the app.</summary>
    public static string EngineLog => Path.Combine(DataDirectory, "engine.log");
}
