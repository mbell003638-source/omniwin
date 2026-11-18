using System.Diagnostics;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

/// <summary>Shutdown must not hang when the UI Automation thread is stuck (for example no desktop is attached).</summary>
public sealed class UiaWorkerShutdownTests
{
    [Fact]
    public async Task Disposing_a_worker_stuck_in_a_call_returns_after_the_bounded_wait()
    {
        var worker = new UiaWorker("uia-shutdown-test", new TimeoutsConfig());
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource();

        // A UI Automation call that does not come back (stands in for a hung provider).
        _ = worker.InvokeAsync<int>((_, _) =>
        {
            started.SetResult();
            release.Wait(TimeSpan.FromSeconds(30));
            return 0;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        await worker.DisposeAsync();
        sw.Stop();

        release.Set();
        Assert.InRange(sw.Elapsed.TotalSeconds, 4, 12);   // gave up after about 5 s instead of waiting for the stuck call
    }
}
