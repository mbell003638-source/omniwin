using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

public sealed class AppLaunchTests
{
    // ---- pure rules ----

    [Theory]
    [InlineData("C:\\Windows\\System32\\notepad.exe")]
    [InlineData("notepad.exe")]
    [InlineData("cmd /c something")]
    [InlineData("https://example.com")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("..\\evil")]
    [InlineData("calc && other")]
    [InlineData("script.ps1")]
    [InlineData("a|b")]
    [InlineData("")]
    public void Path_url_command_and_shell_syntax_look_like_paths(string text)
    {
        Assert.True(AppLaunchRules.LooksLikePathOrCommand(text));
    }

    [Theory]
    [InlineData("Calculator")]
    [InlineData("Windows Terminal")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void Start_menu_names_and_aumids_do_not_look_like_paths(string text)
    {
        Assert.False(AppLaunchRules.LooksLikePathOrCommand(text));
    }

    [Theory]
    [InlineData("cmd.exe", true)]
    [InlineData("PowerShell.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("wt.exe", true)]
    [InlineData("WindowsTerminal.exe", true)]
    [InlineData("wscript.exe", true)]
    [InlineData("cscript.exe", true)]
    [InlineData("mshta.exe", true)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData(null, false)]
    public void Command_processors_and_script_hosts_are_recognised(string? exe, bool expected)
    {
        Assert.Equal(expected, AppLaunchRules.IsCommandProcessor(exe));
    }

    [Fact]
    public void Allow_list_needs_the_exact_path_and_ignores_case()
    {
        string[] allow = ["C:\\Tools\\mytool.exe"];
        Assert.True(AppLaunchRules.IsAllowListed("c:\\tools\\MYTOOL.exe", allow));
        Assert.False(AppLaunchRules.IsAllowListed("C:\\Tools\\other.exe", allow));
        Assert.False(AppLaunchRules.IsAllowListed("C:\\Tools\\..\\Evil\\mytool.exe", allow));
    }

    // ---- the launch through the service ----

    [Theory]
    [InlineData("Calculator")]
    [InlineData("calculator")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public async Task Start_menu_names_and_aumids_launch_after_the_gate(string app)
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync(app, null, new ExpectSpec(new UrlContains("x")), Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.True(result.InputSent);
        Assert.Equal("app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Assert.Single(h.Launcher.Started));
        Assert.Equal(["gate", "input:launch"], h.Log);
        var descriptor = Assert.Single(h.Gate.Seen);
        Assert.Equal("system", descriptor.Engine);
        Assert.Equal("app_launch", descriptor.Tool);
        // F2: the launch is described as a click on the app entry, named like its Start-menu entry, so the delete words apply.
        Assert.Equal("click", descriptor.Action);
        Assert.Equal("Calculator", descriptor.Target?.Name);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\notepad.exe")]
    [InlineData("notepad.exe")]
    [InlineData("cmd /c something")]
    [InlineData("https://example.com")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("calc && other")]
    [InlineData("script.ps1")]
    public async Task Paths_urls_and_command_lines_are_denied_and_nothing_starts(string app)
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync(app, null, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
        Assert.Empty(h.Gate.Seen);
    }

    [Fact]
    public async Task An_allow_listed_path_starts_directly_with_its_args()
    {
        var h = new Harness(new SystemToolsOptions { AppLaunchAllowPaths = ["C:\\Tools\\mytool.exe"] });

        var result = await h.Service.LaunchAppAsync("C:\\Tools\\MYTOOL.exe", "--flag value", new ExpectSpec(new UrlContains("x")), Harness.Ct);

        Assert.True(result.InputSent);
        // The launcher gets the path as the owner wrote it in the allow list, not the caller's spelling.
        Assert.Equal("exe:C:\\Tools\\mytool.exe|--flag value", Assert.Single(h.Launcher.Started));
        Assert.Equal("mytool.exe", Assert.Single(h.Gate.Seen).Process);
    }

    [Fact]
    public async Task A_path_that_is_not_the_exact_allow_listed_one_is_denied()
    {
        var h = new Harness(new SystemToolsOptions { AppLaunchAllowPaths = ["C:\\Tools\\mytool.exe"] });

        var result = await h.Service.LaunchAppAsync("C:\\Tools\\other.exe", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Empty(h.Launcher.Started);
    }

    [Theory]
    [InlineData("Windows Terminal")]
    [InlineData("Windows PowerShell")]
    public async Task Args_never_reach_a_terminal_or_shell_from_the_start_menu(string app)
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync(app, "-c something", null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
        Assert.Empty(h.Gate.Seen);
    }

    [Fact]
    public async Task Args_never_reach_an_allow_listed_command_processor()
    {
        var h = new Harness(new SystemToolsOptions { AppLaunchAllowPaths = ["C:\\Windows\\System32\\cmd.exe"] });

        var result = await h.Service.LaunchAppAsync("C:\\Windows\\System32\\cmd.exe", "/c something", null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.PolicyForbidden, result.Reason);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task A_terminal_without_args_may_be_launched_and_the_gate_sees_its_process()
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync("Windows Terminal", null, new ExpectSpec(new UrlContains("x")), Harness.Ct);

        Assert.True(result.InputSent);
        Assert.Equal("WindowsTerminal.exe", Assert.Single(h.Gate.Seen).Process);
    }

    [Fact]
    public async Task Start_menu_apps_take_no_args()
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync("Notepad", "file.txt", null, Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UnsupportedAction, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task An_unknown_name_is_not_found_and_an_ambiguous_one_lists_candidates()
    {
        var h = new Harness();

        var unknown = await h.Service.LaunchAppAsync("Doesnotexist", null, null, Harness.Ct);
        var ambiguous = await h.Service.LaunchAppAsync("Pai", null, null, Harness.Ct);

        Assert.Equal(ReasonCodes.NotFound, unknown.Reason);
        Assert.False(unknown.InputSent);
        Assert.Equal(ReasonCodes.NotFound, ambiguous.Reason);
        Assert.Contains("matches 2 apps", ambiguous.Detail);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task A_gate_denial_starts_nothing()
    {
        var h = new Harness();
        h.Gate.Decide = _ => new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, "no");

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task The_stop_key_denies_before_anything_starts()
    {
        var h = new Harness();
        h.Kill.IsTripped = true;

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Denied, result.Status);
        Assert.Equal(ReasonCodes.KillSwitch, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task The_user_activity_pause_applies_to_launching()
    {
        var h = new Harness();
        h.Activity.UserActiveRecently = true;

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.UserActive, result.Reason);
        Assert.False(result.InputSent);
        Assert.Empty(h.Launcher.Started);
    }

    [Fact]
    public async Task An_expectation_that_is_not_met_times_out_with_input_sent()
    {
        var h = new Harness();
        h.Desktop.OnWait = _ => new WaitResult(false, ReasonCodes.EngineTimeout, null, Timing.Total(0));

        var result = await h.Service.LaunchAppAsync("Calculator", null, new ExpectSpec(new UrlContains("x")), Harness.Ct);

        Assert.Equal(ActStatus.Timeout, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Without_an_expectation_a_new_window_confirms_the_launch()
    {
        var h = new Harness();
        h.Desktop.OnList = count =>
        {
            if (count == 2) h.AddWindow("w9", 900, "calculator.exe", "Calculator");
        };

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Confirmed, result.Status);
        Assert.Contains("w9", result.Detail);
    }

    [Fact]
    public async Task Without_an_expectation_and_no_new_window_the_launch_is_unverified()
    {
        var h = new Harness();

        var result = await h.Service.LaunchAppAsync("Calculator", null, null, Harness.Ct);

        Assert.Equal(ActStatus.Unverified, result.Status);
        Assert.True(result.InputSent);
    }

    [Fact]
    public async Task Apps_list_filters_and_caches_the_catalog()
    {
        var h = new Harness();

        var all = await h.Service.ListAppsAsync(null, Harness.Ct);
        var terminals = await h.Service.ListAppsAsync("terminal", Harness.Ct);

        Assert.Equal(h.Apps.Apps.Count, all.Count);
        Assert.Equal("Windows Terminal", Assert.Single(terminals).Name);
        Assert.Equal(1, h.Apps.Calls);
    }
}
