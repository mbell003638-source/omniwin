using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

/// <summary>The terminal Enter check (and its OCR fallback) exists only for deleting; safety.allow_delete turns it off.</summary>
public sealed class AllowDeleteSubmissionTests
{
    [Fact]
    public void Submission_guard_follows_the_allow_delete_switch()
    {
        Assert.True(UiaDesktop.EnforcesSubmissionGuard(new OmniWinConfig()));
        Assert.False(UiaDesktop.EnforcesSubmissionGuard(new OmniWinConfig { Safety = new SafetyConfig { AllowDelete = true } }));
    }
}