using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

/// <summary>Regression (2026-11-19): a fresh desktop backend refused every selection read as "secure desktop", because only a
/// window listing set the input-desktop flag. Read-only: reads the focused control's selection, sends no input.</summary>
[Collection("Desktop input")]
public sealed class SelectionInputDesktopTests
{
    public static bool OnInputDesktop => NativeDesktop.OnInputDesktop();

    [Fact(Skip = "Needs an active, unlocked input desktop.", SkipUnless = nameof(OnInputDesktop))]
    public async Task A_selection_read_on_a_fresh_backend_is_not_refused_as_the_secure_desktop()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new OmniWinConfig();
        await using var events = new UiaEvents(config.Timeouts, registerEvents: false);
        UiaDesktop? desktop = null;
        await using var worker = new UiaWorker("uia-selection-regression", config.Timeouts, _ => desktop?.Dispose());

        var read = await worker.InvokeAsync((uia, token) =>
        {
            desktop = new UiaDesktop(uia, events, config, null);   // nothing listed first: the read is the first call
            return desktop.ReadSelection(0, token);
        }, ct);

        Assert.NotEqual(ReasonCodes.SecureDesktop, read.Reason);
    }
}
