using System.Diagnostics;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

/// <summary>
/// B1: the browser OmniWin starts can hand off to another process and exit with code 0. These tests prove a launch finds the
/// real browser, and that dispose, a failed launch and a cancelled launch leave NO browser behind. Every browser is counted by
/// its unique profile folder, so nothing outside OmniWin's own test browsers is ever counted or touched.
/// </summary>
public sealed class BrowserLaunchCleanupTests : IDisposable
{
    private readonly List<string> _profiles = [];
    private readonly List<string> _tempDirs = [];

    private (OmniWinConfig Config, OmniWinPaths Paths, string Profile) NewConfig()
    {
        var temp = Path.Combine(Path.GetTempPath(), "omniwin-test-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _tempDirs.Add(temp);
        var profile = Path.Combine(temp, "browser-profile");
        _profiles.Add(profile);
        var config = new OmniWinConfig { Browser = new BrowserConfig { Mode = "dedicated", ProfileDir = profile } };
        return (config, OmniWinPaths.FromOverride(temp), profile);
    }

    /// <summary>Waits (bounded) until no browser carries this profile folder; returns the final count.</summary>
    private static async Task<int> WaitForNoBrowserAsync(string profile, int seconds = 15)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var count = BrowserProcesses.CountForProfile(profile);
            if (count == 0 || Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(seconds)) return count;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        // Safety net: whatever a failed test left, stop it by profile folder (only this test's own browsers).
        foreach (var profile in _profiles)
        {
            BrowserProcesses.StopForProfile(profile);
        }

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp folder this test created; ignore */ }
        }
    }

    [Fact]
    public async Task Launch_finds_the_real_browser_and_dispose_leaves_none_for_that_profile()
    {
        var (config, paths, profile) = NewConfig();
        var launcher = new BrowserLauncher(config, paths);
        try
        {
            var ws = await launcher.LaunchAsync(headless: true, TestContext.Current.CancellationToken);

            Assert.StartsWith("ws://127.0.0.1:", ws.ToString());
            Assert.True(launcher.IsRunning, "the tracked process must be the real browser, not the exited starter");
            Assert.True(BrowserProcesses.CountForProfile(profile) > 0);
        }
        finally
        {
            await launcher.DisposeAsync();
        }

        Assert.Equal(0, await WaitForNoBrowserAsync(profile));
    }

    [Fact]
    public async Task A_cancelled_launch_leaves_no_browser()
    {
        var (config, paths, profile) = NewConfig();
        var launcher = new BrowserLauncher(config, paths);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));   // mid-launch: the browser is starting but not ready

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.LaunchAsync(headless: true, cts.Token));

        Assert.Equal(0, await WaitForNoBrowserAsync(profile));
    }

    [Fact]
    public async Task A_launch_that_fails_after_the_browser_is_up_leaves_no_browser()
    {
        var (config, paths, profile) = NewConfig();
        var engine = new BrowserEngine(config, paths, new SafetyGate(config, new TestKillSwitch()), new TestKillSwitch(), new TestUserActivity(), new TestMetrics())
        {
            // Called after the browser started and before the connection is made: simulates an unreachable CDP endpoint.
            ConnectionCreated = _ => throw new InvalidOperationException("Simulated: CDP endpoint unreachable"),
        };

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnsureConnectedAsync(headless: true, TestContext.Current.CancellationToken));
            Assert.Equal(0, await WaitForNoBrowserAsync(profile));
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task Engine_dispose_leaves_no_browser()
    {
        var (config, paths, profile) = NewConfig();
        var engine = new BrowserEngine(config, paths, new SafetyGate(config, new TestKillSwitch()), new TestKillSwitch(), new TestUserActivity(), new TestMetrics());
        try
        {
            await engine.EnsureConnectedAsync(headless: true, TestContext.Current.CancellationToken);
            Assert.True(BrowserProcesses.CountForProfile(profile) > 0);
        }
        finally
        {
            await engine.DisposeAsync();
        }

        Assert.Equal(0, await WaitForNoBrowserAsync(profile));
    }

    [Fact]
    public async Task Stopping_one_profile_never_touches_another_profiles_browser()
    {
        var (configA, pathsA, profileA) = NewConfig();
        var (configB, pathsB, profileB) = NewConfig();
        var a = new BrowserLauncher(configA, pathsA);
        var b = new BrowserLauncher(configB, pathsB);
        try
        {
            await a.LaunchAsync(headless: true, TestContext.Current.CancellationToken);
            await b.LaunchAsync(headless: true, TestContext.Current.CancellationToken);

            await a.DisposeAsync();

            Assert.Equal(0, await WaitForNoBrowserAsync(profileA));
            Assert.True(BrowserProcesses.CountForProfile(profileB) > 0, "B must still be running");
            Assert.True(b.IsRunning);
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }

        Assert.Equal(0, await WaitForNoBrowserAsync(profileB));
    }

    [Theory]
    [InlineData("msedge.exe --remote-debugging-port=0 --user-data-dir=\"C:\\t\\profile\" --no-first-run", true)]
    [InlineData("msedge.exe --type=renderer --user-data-dir=C:\\t\\profile --lang=en", true)]
    [InlineData("msedge.exe --user-data-dir=\"C:\\T\\PROFILE\\\" about:blank", true)]
    [InlineData("msedge.exe --user-data-dir=\"C:\\t\\profile2\" --no-first-run", false)]
    [InlineData("msedge.exe --user-data-dir=\"C:\\t\\profile\\sub\"", false)]
    [InlineData("msedge.exe --user-data-dir=\"C:\\t\\pro\"", false)]
    [InlineData("msedge.exe --no-first-run C:\\t\\profile", false)]
    [InlineData("", false)]
    public void NamesProfile_matches_only_the_exact_profile_folder(string commandLine, bool expected)
    {
        Assert.Equal(expected, BrowserProcesses.NamesProfile(commandLine, @"C:\t\profile"));
    }

    [Fact]
    public async Task A_browser_left_by_this_server_is_cleared_quickly_before_the_next_launch()
    {
        // Simulates a dropped connection or a launcher that was never disposed: A's browser is still running on the profile.
        var (config, paths, profile) = NewConfig();
        var a = new BrowserLauncher(config, paths);
        var b = new BrowserLauncher(config, paths);
        try
        {
            await a.LaunchAsync(headless: true, TestContext.Current.CancellationToken);
            var oldRoot = a.Process!.Id;

            var start = Stopwatch.GetTimestamp();
            var ws = await b.LaunchAsync(headless: true, TestContext.Current.CancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(start);

            Assert.StartsWith("ws://127.0.0.1:", ws.ToString());
            Assert.True(b.IsRunning);
            Assert.NotEqual(oldRoot, b.Process!.Id);
            Assert.True(elapsed < TimeSpan.FromSeconds(12), $"the old browser must not stall the new launch ({elapsed.TotalSeconds:F1} s)");
            Assert.False(ProcessIsRunning(oldRoot), "the leftover browser must have been stopped");
        }
        finally
        {
            await b.DisposeAsync();
            await a.DisposeAsync();
        }

        Assert.Equal(0, await WaitForNoBrowserAsync(profile));
    }

    [Fact]
    public async Task A_browser_owned_by_another_running_server_is_never_touched()
    {
        var (config, paths, profile) = NewConfig();
        var a = new BrowserLauncher(config, paths);
        var b = new BrowserLauncher(config, paths);

        // Stands in for another OmniWin server that is still running: a process this test starts and stops itself.
        using var otherServer = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await a.LaunchAsync(headless: true, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(profile, BrowserLauncher.OwnerFileName), BrowserLauncher.OwnerStamp(otherServer),
                TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => b.LaunchAsync(headless: true, TestContext.Current.CancellationToken));
            Assert.Contains("Another running OmniWin server", ex.Message);

            await b.DisposeAsync();   // b never owned the profile, so disposing it must not stop a's browser
            Assert.True(a.IsRunning, "the other server's browser must still be running");
            Assert.True(BrowserProcesses.CountForProfile(profile) > 0);
        }
        finally
        {
            try { otherServer.Kill(); } catch (InvalidOperationException) { /* already exited */ }
            await b.DisposeAsync();
            await a.DisposeAsync();
        }

        Assert.Equal(0, await WaitForNoBrowserAsync(profile));
    }

    private static bool ProcessIsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_starter_that_exits_with_code_zero_is_a_hand_off_not_a_failure()
    {
        var temp = Path.Combine(Path.GetTempPath(), "omniwin-test-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _tempDirs.Add(temp);
        var portFile = Path.Combine(temp, "DevToolsActivePort");

        using var starter = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        await starter.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, starter.ExitCode);

        // The real browser writes the port file a little after the starter has gone.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await File.WriteAllLinesAsync(portFile, ["9333", "/devtools/browser/abc"], TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var (port, ws) = await BrowserLauncher.WaitForActivePortAsync(portFile, starter, TestContext.Current.CancellationToken, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(9333, port);
        Assert.Equal(new Uri("ws://127.0.0.1:9333/devtools/browser/abc"), ws);
    }

    [Fact]
    public async Task A_starter_that_exits_with_an_error_before_any_port_file_is_a_failure()
    {
        var temp = Path.Combine(Path.GetTempPath(), "omniwin-test-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _tempDirs.Add(temp);
        var portFile = Path.Combine(temp, "DevToolsActivePort");

        using var starter = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 3") { UseShellExecute = false, CreateNoWindow = true })!;
        await starter.WaitForExitAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BrowserLauncher.WaitForActivePortAsync(portFile, starter, TestContext.Current.CancellationToken, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Contains("code 3", ex.Message);
    }
}
