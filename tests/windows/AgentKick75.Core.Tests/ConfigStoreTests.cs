using System.Text.Json.Nodes;
using AgentKick75.Core.Configuration;
using AgentKick75.Core.Lighting;

namespace AgentKick75.Core.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsDefaultsWithoutWritingFile()
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        var store = new ConfigurationStore(path);

        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.MissingUsingDefaults, result.Status);
        Assert.Equal(AgentKick75Configuration.Default, result.Configuration);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("#12345", 50, 10)]
    [InlineData("#GG0000", 50, 10)]
    [InlineData("#123456", -1, 10)]
    [InlineData("#123456", 101, 10)]
    [InlineData("#123456", 50, 0)]
    [InlineData("#123456", 50, 3601)]
    public async Task LoadAsync_InvalidColorBrightnessOrTtl_FallsBackToDefaults(
        string color,
        int brightness,
        int completeTtlSeconds)
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
              {
                "schemaVersion": 1,
                "states": {
                  "thinking": { "color": "{{color}}", "brightness": {{brightness}} }
                },
                "completeTtlSeconds": {{completeTtlSeconds}}
              }
              """);
        var store = new ConfigurationStore(path);

        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.InvalidUsingDefaults, result.Status);
        Assert.Equal(AgentKick75Configuration.Default, result.Configuration);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_FallsBackWithoutOverwritingBadFile()
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        const string malformed = "{ this is not JSON";
        await File.WriteAllTextAsync(path, malformed);
        var store = new ConfigurationStore(path);

        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.InvalidUsingDefaults, result.Status);
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadAsync_UnsupportedVersion_ReturnsDistinctFallbackStatus()
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        await File.WriteAllTextAsync(path, """{"schemaVersion":99}""");
        var store = new ConfigurationStore(path);

        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.UnsupportedVersionUsingDefaults, result.Status);
        Assert.Equal(AgentKick75Configuration.Default, result.Configuration);
    }

    [Fact]
    public async Task LoadAsync_LegacyVersionAndStateNames_MigratesInMemory()
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": 1,
              "states": {
                "running": { "color": "#123abc", "brightness": 42 },
                "permission": { "color": "#fedcba", "brightness": 43 },
                "completed": { "color": "#010203", "brightness": 44 }
              }
            }
            """);
        var store = new ConfigurationStore(path);

        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal("#123ABC", result.Configuration.Lighting.Thinking.Color.ToString());
        Assert.Equal((byte)42, result.Configuration.Lighting.Thinking.Brightness);
        Assert.Equal("#FEDCBA", result.Configuration.Lighting.RequiresInput.Color.ToString());
        Assert.Equal("#010203", result.Configuration.Lighting.Complete.Color.ToString());
    }

    [Fact]
    public async Task SaveAsync_ValidConfiguration_RoundTripsAndLeavesNoTemporaryFile()
    {
        using var directory = new ConfigTemporaryDirectory();
        string path = directory.File("config.json");
        var store = new ConfigurationStore(path);
        var expected = new AgentKick75Configuration(
            new LightingSettings(
                new LightStyle(RgbColor.Parse("#123ABC"), 0),
                new LightStyle(RgbColor.Parse("#FEDCBA"), 50),
                new LightStyle(RgbColor.Parse("#010203"), 100),
                TimeSpan.FromSeconds(27)),
            TimeSpan.FromMinutes(91),
            startAtLogin: true);

        await store.SaveAsync(expected);
        ConfigurationLoadResult result = await store.LoadAsync();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal(expected, result.Configuration);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
        JsonObject saved = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(path)));
        Assert.Equal(1, saved["schemaVersion"]!.GetValue<int>());
        Assert.Equal(27d, saved["completeTtlSeconds"]!.GetValue<double>());
    }

    private sealed class ConfigTemporaryDirectory : IDisposable
    {
        public ConfigTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AgentKick75.ConfigTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
