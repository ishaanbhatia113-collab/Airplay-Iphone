using MirrorDeck.Core;

namespace MirrorDeck.Core.Tests;

public sealed class EngineLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mirrordeck-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Finds_the_bundled_engine_with_its_gstreamer_layout()
    {
        var exe = Touch("app", "engine", "bin", EngineLocator.ExeName);
        Touch("app", "engine", "lib", "gstreamer-1.0", "libgstcoreelements.dll");
        var scanner = Touch("app", "engine", "libexec", "gstreamer-1.0",
            OperatingSystem.IsWindows() ? "gst-plugin-scanner.exe" : "gst-plugin-scanner");

        var found = EngineLocator.Find(null, Path.Combine(_root, "app"), out var searched);

        Assert.NotNull(found);
        Assert.Equal(EngineKind.Bundled, found.Kind);
        Assert.Equal(exe, found.ExePath);
        Assert.Equal(Path.Combine(_root, "app", "engine", "bin"), found.BinDirectory);
        Assert.Equal(Path.Combine(_root, "app", "engine", "lib", "gstreamer-1.0"), found.PluginDirectory);
        Assert.Equal(scanner, found.PluginScannerPath);
        Assert.NotEmpty(searched);
    }

    [Fact]
    public void Finds_the_build_output_when_running_from_the_source_tree()
    {
        var exe = Touch("repo", "engine", "dist", "bin", EngineLocator.ExeName);
        var appDir = Path.Combine(_root, "repo", "src", "MirrorDeck", "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(appDir);

        var found = EngineLocator.Find(null, appDir, out _);

        Assert.NotNull(found);
        Assert.Equal(EngineKind.SourceTree, found.Kind);
        Assert.Equal(exe, found.ExePath);
    }

    [Fact]
    public void An_override_is_used_as_is_and_never_falls_back()
    {
        Touch("app", "engine", "bin", EngineLocator.ExeName);
        var custom = Touch("elsewhere", EngineLocator.ExeName);

        var found = EngineLocator.Find($"\"{custom}\" ", Path.Combine(_root, "app"), out _);
        Assert.NotNull(found);
        Assert.Equal(EngineKind.Custom, found.Kind);
        Assert.Equal(custom, found.ExePath);
        Assert.Null(found.PluginDirectory);

        var missing = EngineLocator.Find(Path.Combine(_root, "nope", "uxplay.exe"), Path.Combine(_root, "app"), out var searched);
        Assert.Null(missing);
        Assert.Single(searched);
    }

    [Fact]
    public void Reports_where_it_looked_when_nothing_is_found()
    {
        var found = EngineLocator.Find(null, Path.Combine(_root, "empty"), out var searched, fileExists: _ => false);
        Assert.Null(found);
        Assert.Contains(searched, p => p.EndsWith(Path.Combine("engine", "bin", EngineLocator.ExeName), StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_isolates_the_bundled_gstreamer()
    {
        Touch("app", "engine", "bin", EngineLocator.ExeName);
        Touch("app", "engine", "lib", "gstreamer-1.0", "x.dll");
        var engine = EngineLocator.Find(null, Path.Combine(_root, "app"), out _)!;
        var data = Path.Combine(_root, "data");

        var env = EngineLocator.BuildEnvironment(engine, data);

        Assert.Equal(engine.PluginDirectory, env["GST_PLUGIN_SYSTEM_PATH_1_0"]);
        Assert.Null(env["GST_PLUGIN_PATH_1_0"]);
        Assert.Null(env["GST_PLUGIN_PATH"]);
        Assert.Null(env["UXPLAYRC"]);
        Assert.Equal(Path.Combine(data, "gstreamer-registry.bin"), env["GST_REGISTRY_1_0"]);
        Assert.StartsWith(engine.BinDirectory + Path.PathSeparator, env["PATH"] + Path.PathSeparator);
        Assert.False(env.ContainsKey("GST_PLUGIN_SCANNER_1_0")); // no scanner in this layout
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR:pt:en")]
    [InlineData("en-US", "en-US:en")]
    [InlineData("de", "de:en")]
    [InlineData("en", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    [InlineData("zh_CN", "zh-CN:zh:en")]
    [InlineData("C.UTF-8", "en")]
    public void Language_preference_for_the_engine(string? culture, string expected) =>
        Assert.Equal(expected, EngineLocator.LanguagePreference(culture));

    [Fact]
    public void A_language_is_always_available_to_the_engine()
    {
        // The engine aborts on start when video casting is enabled and no locale variable is set,
        // which is the normal situation on Windows.
        var engine = EngineLocator.Find(Touch("x", EngineLocator.ExeName), _root, out _)!;
        var env = EngineLocator.BuildEnvironment(engine, _root, "fr-CA");

        string[] variables = ["LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"];
        var inherited = variables.Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));
        if (inherited) Assert.False(env.ContainsKey("LANGUAGE"));
        else Assert.Equal("fr-CA:fr:en", env["LANGUAGE"]);
    }

    [Fact]
    public void A_custom_engine_keeps_its_own_plugin_search_path()
    {
        var custom = Touch("custom", "bin", EngineLocator.ExeName);
        Touch("custom", "lib", "gstreamer-1.0", "x.dll");
        var engine = EngineLocator.Find(custom, _root, out _)!;

        var env = EngineLocator.BuildEnvironment(engine, Path.Combine(_root, "data"));

        Assert.False(env.ContainsKey("GST_PLUGIN_SYSTEM_PATH_1_0"));
        Assert.True(env.ContainsKey("GST_REGISTRY_1_0"));
    }
}
