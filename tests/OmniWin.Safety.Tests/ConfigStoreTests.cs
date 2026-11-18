using OmniWin.Safety;

namespace OmniWin.Safety.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omniwin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Override_keeps_every_file_under_the_data_dir()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        Assert.True(paths.IsOverride);
        foreach (var p in new[] { paths.ConfigFile, paths.LogsDir, paths.WorkflowsDir, paths.DefaultBrowserProfileDir })
        {
            Assert.StartsWith(Path.GetFullPath(_dir), p, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(paths.DefaultBrowserProfileDir, paths.ResolveBrowserProfileDir(new OmniWinConfig()));
    }

    [Fact]
    public void First_run_writes_defaults_and_reads_them_back()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        var created = ConfigStore.LoadOrCreate(paths);

        Assert.True(File.Exists(paths.ConfigFile));
        var json = File.ReadAllText(paths.ConfigFile);
        Assert.Contains("\"kill_hotkey\": \"ctrl+alt+shift+k\"", json, StringComparison.Ordinal);
        Assert.Contains("\"uia_transaction_ms\": 1000", json, StringComparison.Ordinal);
        Assert.Contains("\"confirm_mode\": \"off\"", json, StringComparison.Ordinal);

        var again = ConfigStore.LoadOrCreate(paths);
        Assert.Equal(created.KillHotkey, again.KillHotkey);
        Assert.Equal(created.ConfirmTypingProcesses, again.ConfirmTypingProcesses);
        Assert.False(again.AllowElevated);
        Assert.Empty(again.Preapproved);
    }

    [Fact]
    public void Existing_file_is_not_overwritten()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(paths.ConfigFile, """{ "user_activity_cooldown_ms": 5000, "rate_limit": { "max_actions": 5, "per_seconds": 1 } }""");

        var config = ConfigStore.LoadOrCreate(paths);
        Assert.Equal(5000, config.UserActivityCooldownMs);
        Assert.Equal(5, config.RateLimit.MaxActions);
        Assert.Equal("ctrl+alt+shift+k", config.KillHotkey);
    }

    [Theory]
    [InlineData("""{ "process_blocklist_extr": ["x.exe"] }""")]       // misspelled key
    [InlineData("""{ "confirm_mode": "never" }""")]                   // invalid value
    [InlineData("""{ "rate_limit": { "max_actions": 0 } }""")]       // out of range
    [InlineData("""{ "kill_hotkey": """)]                              // malformed
    public void Bad_config_stops_startup(string json)
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(paths.ConfigFile, json);

        Assert.Throws<ConfigException>(() => ConfigStore.LoadOrCreate(paths));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_in_hand_edited_config()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(paths.ConfigFile, """
            {
              // added by hand
              "log_typed_text": true,
            }
            """);

        Assert.True(ConfigStore.LoadOrCreate(paths).LogTypedText);
    }
}
