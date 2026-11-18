using OmniWin.Safety;
using OmniWin.Server.Power;

namespace OmniWin.Router.Tests;

/// <summary>
/// The power tools (off by default). Files and the registry-free paths are checked in a temp folder that each test creates and removes
/// itself; the one real process is a harmless PowerShell echo.
/// </summary>
public sealed class PowerToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "omniwin-test-power-" + Guid.NewGuid().ToString("N")[..8]);

    public PowerToolsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // The folder was created by this test class; it is the only thing removed.
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PowerTools Tools(bool allowDelete = false, AllowGate? gate = null) => new(
        new OmniWinConfig { PowerTools = new PowerToolsConfig { Enabled = true, AllowDelete = allowDelete } },
        gate ?? new AllowGate(), new FreeKill(), new NullMetrics());

    [Fact]
    public async Task Power_tools_are_off_in_a_default_config()
    {
        var config = new OmniWinConfig();
        Assert.False(config.PowerTools.Enabled);
        Assert.False(config.PowerTools.AllowDelete);
        Assert.Empty(config.Validate());
        await Task.CompletedTask;
    }

    [Fact]
    public void Allowing_delete_without_enabling_power_tools_is_a_config_error()
    {
        var config = new OmniWinConfig { PowerTools = new PowerToolsConfig { Enabled = false, AllowDelete = true } };

        Assert.Contains(config.Validate(), e => e.Contains("allow_delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_is_written_read_back_and_listed()
    {
        var file = Path.Combine(_folder, "a.txt");

        var written = await Tools().Files("write", file, Ct, text: "hello");
        var read = await Tools().Files("read", file, Ct);
        var list = await Tools().Files("list", _folder, Ct);

        Assert.StartsWith("status=confirmed input_sent=true", written, StringComparison.Ordinal);
        Assert.Contains("hello", read, StringComparison.Ordinal);
        Assert.Contains("a.txt", list, StringComparison.Ordinal);
        Assert.Equal("hello", await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task Writing_over_an_existing_file_is_blocked_and_overwrite_needs_allow_delete()
    {
        var file = Path.Combine(_folder, "keep.txt");
        await File.WriteAllTextAsync(file, "original", Ct);

        var blocked = await Tools().Files("write", file, Ct, text: "new");
        var denied = await Tools().Files("write", file, Ct, text: "new", overwrite: true);
        var allowed = await Tools(allowDelete: true).Files("write", file, Ct, text: "new", overwrite: true);

        Assert.StartsWith("status=blocked input_sent=false", blocked, StringComparison.Ordinal);
        Assert.StartsWith("status=denied input_sent=false", denied, StringComparison.Ordinal);
        Assert.StartsWith("status=confirmed", allowed, StringComparison.Ordinal);
        Assert.Equal("new", await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task Delete_is_denied_unless_allow_delete_is_on_and_then_it_is_read_back()
    {
        var file = Path.Combine(_folder, "gone.txt");
        await File.WriteAllTextAsync(file, "x", Ct);

        var denied = await Tools().Files("delete", file, Ct);
        Assert.StartsWith("status=denied input_sent=false", denied, StringComparison.Ordinal);
        Assert.True(File.Exists(file));

        var done = await Tools(allowDelete: true).Files("delete", file, Ct);
        Assert.StartsWith("status=confirmed input_sent=true", done, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_folder_copy_and_move_are_checked_on_disk()
    {
        var source = Path.Combine(_folder, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), "1", Ct);

        var copy = await Tools().Files("copy", source, Ct, to: Path.Combine(_folder, "copy"));
        var move = await Tools().Files("move", source, Ct, to: Path.Combine(_folder, "moved"));

        Assert.StartsWith("status=confirmed", copy, StringComparison.Ordinal);
        Assert.StartsWith("status=confirmed", move, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_folder, "copy", "f.txt")));
        Assert.True(File.Exists(Path.Combine(_folder, "moved", "f.txt")));
        Assert.False(Directory.Exists(source));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    public void Drive_roots_are_protected(string path) => Assert.True(PowerTools.IsProtectedPath(path));

    [Fact]
    public void The_user_profile_and_the_windows_folder_are_protected_but_a_folder_inside_is_not()
    {
        Assert.True(PowerTools.IsProtectedPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.True(PowerTools.IsProtectedPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(PowerTools.IsProtectedPath(_folder));
    }

    [Fact]
    public async Task Deleting_a_protected_path_is_refused_even_with_allow_delete()
    {
        var reply = await Tools(allowDelete: true).Files("delete", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Ct);

        Assert.StartsWith("status=denied input_sent=false", reply, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public async Task A_delete_command_in_the_shell_is_refused_when_delete_is_off_and_nothing_runs()
    {
        var marker = Path.Combine(_folder, "marker.txt");
        await File.WriteAllTextAsync(marker, "x", Ct);

        var reply = await Tools().Shell($"Remove-Item -LiteralPath '{marker}'", Ct);

        Assert.StartsWith("status=denied input_sent=false", reply, StringComparison.Ordinal);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task The_shell_runs_a_harmless_command_and_the_result_is_unverified_with_check_state()
    {
        var reply = await Tools().Shell("Write-Output 'omniwin-power-test'", Ct);

        Assert.StartsWith("status=unverified input_sent=true", reply, StringComparison.Ordinal);
        Assert.Contains("retry=check_state", reply, StringComparison.Ordinal);
        Assert.Contains("exit_code=0", reply, StringComparison.Ordinal);
        Assert.Contains("omniwin-power-test", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_command_that_runs_too_long_is_ended()
    {
        var reply = await Tools().Shell("Start-Sleep -Seconds 30", Ct, timeout_ms: 1500);

        Assert.StartsWith("status=error input_sent=true", reply, StringComparison.Ordinal);
        Assert.Contains("reason=engine_timeout", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_refuses_delete_when_off_and_a_bad_key_when_on()
    {
        var denied = await Tools().Registry("delete_value", @"HKCU\Software\OmniWinTest", Ct, name: "x");
        var bad = await Tools(allowDelete: true).Registry("read", @"HKXX\Software", Ct);

        Assert.StartsWith("status=denied input_sent=false", denied, StringComparison.Ordinal);
        Assert.Contains("reason=unsupported_action", bad, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_round_trips_a_value_in_a_key_the_test_creates_and_removes_with_its_own_permission()
    {
        var key = @"HKCU\Software\OmniWinTest-" + Guid.NewGuid().ToString("N")[..8];
        var tools = Tools(allowDelete: true);
        try
        {
            var set = await tools.Registry("set", key, Ct, name: "answer", value: "42", kind: "dword");
            var read = await tools.Registry("read", key, Ct, name: "answer");

            Assert.StartsWith("status=confirmed input_sent=true", set, StringComparison.Ordinal);
            Assert.Contains("42", read, StringComparison.Ordinal);
        }
        finally
        {
            // The key was created by this test (random name under HKCU\Software); only that key is removed.
            await tools.Registry("delete_key", key, Ct);
        }
    }

    [Fact]
    public async Task The_process_tool_never_stops_itself_explorer_or_a_system_process()
    {
        var self = await Tools().ProcessTool("stop", Ct, pid: Environment.ProcessId);
        var explorer = await Tools().ProcessTool("stop", Ct, name: "explorer");

        Assert.StartsWith("status=denied input_sent=false", self, StringComparison.Ordinal);
        Assert.StartsWith("status=denied input_sent=false", explorer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stop_key_or_policy_denies_every_power_tool_before_it_runs()
    {
        var gate = new AllowGate { Deny = true };

        var shell = await Tools(gate: gate).Shell("Write-Output hi", Ct);
        var files = await Tools(gate: gate).Files("list", _folder, Ct);

        Assert.StartsWith("status=denied input_sent=false", shell, StringComparison.Ordinal);
        Assert.StartsWith("status=denied input_sent=false", files, StringComparison.Ordinal);
    }
}
