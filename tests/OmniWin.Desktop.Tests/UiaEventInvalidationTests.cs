namespace OmniWin.Desktop.Tests;

public sealed class UiaEventInvalidationTests
{
    [Fact]
    public async Task Control_changes_wake_waiters_without_invalidating_window_discovery()
    {
        var changes = new UiaChangeTracker();
        var firstWaiter = changes.After(changes.Version);
        Assert.False(firstWaiter.IsCompleted);

        for (var i = 0; i < 100; i++) changes.Notify();
        await firstWaiter.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(100, changes.Version);
        Assert.Equal(0, changes.WindowVersion);
        Assert.True(changes.After(0).IsCompleted);
        var nextWaiter = changes.After(changes.Version);
        Assert.False(nextWaiter.IsCompleted);

        changes.Notify(windowChanged: true);
        await nextWaiter.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(101, changes.Version);
        Assert.Equal(1, changes.WindowVersion);
    }

    [Fact]
    public void Modality_changes_invalidate_cached_window_properties_without_control_event_noise()
    {
        var changes = new UiaChangeTracker();
        changes.Notify(windowChanged: true);
        Assert.Equal(1, changes.WindowVersion);
        Assert.Equal(0, changes.ModalVersion);

        changes.Notify(modalChanged: true);
        Assert.Equal(2, changes.Version);
        Assert.Equal(2, changes.WindowVersion);
        Assert.Equal(1, changes.ModalVersion);

        changes.Notify();
        Assert.Equal(3, changes.Version);
        Assert.Equal(2, changes.WindowVersion);
        Assert.Equal(1, changes.ModalVersion);
    }
}
