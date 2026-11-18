namespace OmniWin.Desktop.Tests;

public sealed class DesktopWindowDiscoveryTests
{
    [Fact]
    public void Foreground_shell_window_missing_from_native_enumeration_is_a_discovery_candidate()
    {
        var candidates = UiaDesktop.DiscoveryCandidates([100, 200], 300, []);

        Assert.Equal(new[] { 100L, 200L, 300L }, candidates);
    }

    [Fact]
    public void Previously_discovered_shell_window_is_retained_when_native_enumeration_omits_it()
    {
        var candidates = UiaDesktop.DiscoveryCandidates([100], 100, [100, 300]);

        Assert.Equal(new[] { 100L, 300L }, candidates);
    }

    [Fact]
    public void Overlapping_discovery_sources_do_not_duplicate_windows_or_include_a_null_handle()
    {
        var candidates = UiaDesktop.DiscoveryCandidates([0, 100, 100, 200], 200, [0, 100, 300, 300]);

        Assert.Equal(new[] { 100L, 200L, 300L }, candidates);
    }
}
