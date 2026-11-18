using System.Diagnostics;

namespace OmniWin.Desktop.Tests;

/// <summary>
/// Contract 2.6: one input lane for every OmniWin process in the session. The lane is an exclusively opened file, so these tests use
/// two lane objects (as two processes would each have one) on a file in a temp folder the test removes, and one real second process.
/// </summary>
public sealed class MachineLaneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omniwin-test-lane-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public MachineLaneTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "lane.lock");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);   // the temp folder this test created
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Only_one_holder_at_a_time_and_the_lane_is_free_again_after_exit()
    {
        var mine = new MachineLane(_file);
        var other = new MachineLane(_file);

        Assert.True(mine.TryEnter());
        Assert.False(other.TryEnter());
        Assert.False(other.TryEnterBriefly());
        mine.Exit();
        Assert.True(other.TryEnter());
        other.Exit();
        mine.Exit();   // a second exit is harmless
    }

    [Fact]
    public void An_act_is_told_at_once_when_another_process_holds_the_lane_and_runs_when_it_is_free()
    {
        using var lanes = new ActLanes { Machine = new MachineLane(_file) };
        var other = new MachineLane(_file);
        Assert.True(other.TryEnter());

        Assert.Null(lanes.TryEnterPhysical());
        Assert.Equal(ActLanes.OtherProcessLabel, lanes.PhysicalHolder);
        Assert.Contains("Another OmniWin process", ActLanes.BusyDetail(lanes.PhysicalHolder), StringComparison.Ordinal);
        Assert.Equal(1, lanes.PhysicalFree);          // the in-process lane was handed back

        other.Exit();
        using (var lease = lanes.TryEnterPhysical())
        {
            Assert.NotNull(lease);
            Assert.False(other.TryEnter());            // and now this process holds the shared lane
        }

        Assert.True(other.TryEnter());                 // released with the lease
        other.Exit();
    }

    [Fact]
    public async Task A_waiting_act_gets_the_lane_as_soon_as_the_other_process_lets_go_and_stops_waiting_when_cancelled()
    {
        using var lanes = new ActLanes { Machine = new MachineLane(_file) };
        var other = new MachineLane(_file);
        Assert.True(other.TryEnter());

        var waiting = lanes.EnterPhysicalAsync(TestContext.Current.CancellationToken);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        other.Exit();
        using (await waiting)
        {
            Assert.False(other.TryEnter());
        }

        Assert.True(other.TryEnter());
        using var cts = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lanes.EnterPhysicalAsync(cts.Token));
        Assert.Equal(1, lanes.PhysicalFree);           // a cancelled wait leaves nothing held
        other.Exit();
    }

    [Fact]
    public void A_real_second_process_that_holds_the_file_blocks_the_lane_and_its_exit_frees_it()
    {
        // PowerShell opens the same file exclusively, says so, and holds it until its input closes.
        var script = $"$f=[IO.File]::Open('{_file.Replace("'", "''", StringComparison.Ordinal)}','OpenOrCreate','ReadWrite','None'); 'HELD'; [Console]::In.ReadLine() | Out-Null; $f.Dispose()";
        var info = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        try
        {
            Assert.Equal("HELD", process.StandardOutput.ReadLine());
            var lane = new MachineLane(_file);
            Assert.False(lane.TryEnterBriefly());

            process.Kill();                             // a crash, not a clean release
            process.WaitForExit(5000);
            Assert.True(lane.TryEnterBriefly());        // Windows freed the file with the process
            lane.Exit();
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }
}
