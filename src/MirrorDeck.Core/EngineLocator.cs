namespace MirrorDeck.Core;

public enum EngineKind
{
    /// <summary>Shipped next to the app (engine\bin\uxplay.exe), produced by engine/build-engine.sh.</summary>
    Bundled,

    /// <summary>The output of engine/build-engine.sh, found in the source tree (developer runs).</summary>
    SourceTree,

    /// <summary>A UxPlay installed into an MSYS2 environment.</summary>
    Msys2,

    /// <summary>The path the user typed into the settings.</summary>
    Custom,
}

/// <summary>Where the engine lives, and the GStreamer files that belong to it.</summary>
/// <param name="ExePath">Full path of uxplay.exe.</param>
/// <param name="BinDirectory">Directory of the exe; the DLLs it needs sit next to it.</param>
/// <param name="PluginDirectory">lib\gstreamer-1.0 next to bin, when the usual layout is present.</param>
/// <param name="PluginScannerPath">libexec\gstreamer-1.0\gst-plugin-scanner.exe, when present.</param>
public sealed record EngineLocation(
    string ExePath,
    string BinDirectory,
    string? PluginDirectory,
    string? PluginScannerPath,
    EngineKind Kind);

public static class EngineLocator
{
    public static string ExeName => OperatingSystem.IsWindows() ? "uxplay.exe" : "uxplay";

    /// <summary>
    /// Search order: explicit override, bundled copy, build output in the source tree, MSYS2 installations.
    /// </summary>
    /// <returns>null when no engine was found; <paramref name="searched"/> then lists where we looked.</returns>
    public static EngineLocation? Find(
        string? overridePath,
        string appBaseDirectory,
        out IReadOnlyList<string> searched,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        var tried = new List<string>();
        searched = tried;

        EngineLocation? Probe(string path, EngineKind kind)
        {
            tried.Add(path);
            return fileExists(path) ? Describe(path, kind, fileExists, directoryExists) : null;
        }

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // An override that does not exist is an error the user should see; do not silently fall back.
            return Probe(overridePath.Trim().Trim('"'), EngineKind.Custom);
        }

        var bundled = Probe(Path.Combine(appBaseDirectory, "engine", "bin", ExeName), EngineKind.Bundled);
        if (bundled is not null) return bundled;

        // Developer runs ("dotnet run"): walk up from bin\Debug\... to the repository root.
        var dir = new DirectoryInfo(appBaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engine", "dist", "bin", ExeName);
            if (!fileExists(candidate)) continue;
            tried.Add(candidate);
            return Describe(candidate, EngineKind.SourceTree, fileExists, directoryExists);
        }

        if (OperatingSystem.IsWindows())
        {
            var roots = new List<string>();
            var env = Environment.GetEnvironmentVariable("MSYS2_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
            roots.Add(@"C:\msys64");

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var flavour in new[] { "ucrt64", "clangarm64", "mingw64" })
                {
                    var found = Probe(Path.Combine(root, flavour, "bin", ExeName), EngineKind.Msys2);
                    if (found is not null) return found;
                }
            }
        }

        return null;
    }

    private static EngineLocation Describe(
        string exePath, EngineKind kind, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        var full = Path.GetFullPath(exePath);
        var bin = Path.GetDirectoryName(full)!;
        string? plugins = null;
        string? scanner = null;

        // GStreamer resolves its own files relative to "<prefix>\bin\libgstreamer-1.0-0.dll".
        // The bundle keeps that layout, so the same logic covers the bundle and an MSYS2 installation.
        var prefix = Path.GetDirectoryName(bin);
        if (prefix is not null && string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase))
        {
            var pluginDir = Path.Combine(prefix, "lib", "gstreamer-1.0");
            if (directoryExists(pluginDir)) plugins = pluginDir;

            var scannerName = OperatingSystem.IsWindows() ? "gst-plugin-scanner.exe" : "gst-plugin-scanner";
            var scannerPath = Path.Combine(prefix, "libexec", "gstreamer-1.0", scannerName);
            if (fileExists(scannerPath)) scanner = scannerPath;
        }

        return new EngineLocation(full, bin, plugins, scanner, kind);
    }

    /// <summary>
    /// Environment for the engine process. A null value means "remove this variable".
    /// </summary>
    /// <param name="uiLanguage">Culture name such as "en-US"; defaults to the current UI culture.</param>
    public static IReadOnlyDictionary<string, string?> BuildEnvironment(
        EngineLocation engine, string dataDirectory, string? uiLanguage = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Our own plugin cache: writable when the app is installed as an MSIX package, and never
            // shared with (or corrupted by) another GStreamer installation on the machine.
            ["GST_REGISTRY_1_0"] = Path.Combine(dataDirectory, "gstreamer-registry.bin"),
            ["GST_REGISTRY"] = null,

            // The startup file is passed explicitly; make sure a stray variable cannot redirect it.
            ["UXPLAYRC"] = null,
        };

        if (engine.PluginDirectory is not null && engine.Kind != EngineKind.Custom)
        {
            // Only our plugins: a second GStreamer on the machine (the official MSVC build is a common one)
            // must not be able to inject binary-incompatible plugins through these variables.
            env["GST_PLUGIN_SYSTEM_PATH_1_0"] = engine.PluginDirectory;
            env["GST_PLUGIN_SYSTEM_PATH"] = null;
            env["GST_PLUGIN_PATH_1_0"] = null;
            env["GST_PLUGIN_PATH"] = null;
            if (engine.PluginScannerPath is not null) env["GST_PLUGIN_SCANNER_1_0"] = engine.PluginScannerPath;
        }

        // With video casting (HLS) enabled the engine insists on a language preference from one of the
        // POSIX locale variables and aborts without one. Windows does not define any of them.
        string[] localeVariables = ["LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"];
        if (!localeVariables.Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))))
        {
            env["LANGUAGE"] = LanguagePreference(uiLanguage ?? System.Globalization.CultureInfo.CurrentUICulture.Name);
        }

        // Dependent DLLs are next to the exe; putting that directory first also keeps look-alike DLLs
        // from elsewhere on PATH (other GStreamer, Git, ...) out of the process.
        var path = Environment.GetEnvironmentVariable("PATH");
        env["PATH"] = string.IsNullOrEmpty(path) ? engine.BinDirectory : engine.BinDirectory + Path.PathSeparator + path;
        return env;
    }

    /// <summary>"pt-BR" becomes "pt-BR:pt:en": the engine's ranked list, most specific first.</summary>
    internal static string LanguagePreference(string? cultureName)
    {
        var parts = new List<string>();
        var name = (cultureName ?? string.Empty).Trim().Replace('_', '-');
        if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            parts.Add(name);
            var dash = name.IndexOf('-');
            if (dash > 0) parts.Add(name[..dash]);
        }

        if (!parts.Contains("en", StringComparer.OrdinalIgnoreCase)) parts.Add("en");
        return string.Join(':', parts);
    }
}
