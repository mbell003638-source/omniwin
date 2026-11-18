using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class DesktopTypingFocusTests
{
    private static readonly VerifiedNativeEditor Editor = new(100, 101, 10, "Edit");

    [Fact]
    public void First_batch_uses_full_focus_check_even_for_a_verified_native_editor()
    {
        var checks = 0;
        DesktopTypingFocus.CheckBatch(true, Editor, () => checks++, () => throw new InvalidOperationException());
        Assert.Equal(1, checks);
    }

    [Fact]
    public void Windowless_or_unverified_controls_use_full_check_on_every_batch()
    {
        var checks = 0;
        for (var i = 0; i < 3; i++)
            DesktopTypingFocus.CheckBatch(i == 0, null, () => checks++, () => throw new InvalidOperationException());
        Assert.Equal(3, checks);
    }

    [Fact]
    public void Verified_native_editor_uses_fresh_exact_focus_on_later_batches()
    {
        var fullChecks = 0;
        var nativeChecks = 0;
        for (var i = 0; i < 3; i++)
            DesktopTypingFocus.CheckBatch(i == 0, Editor, () => fullChecks++, () =>
            {
                nativeChecks++;
                return new NativeTypingFocus(100, 101, 100, 10, "Edit");
            });
        Assert.Equal(1, fullChecks);
        Assert.Equal(2, nativeChecks);
    }

    [Theory]
    [InlineData(100, 102, 100, 10, "Edit")] // sibling text box
    [InlineData(200, 201, 200, 20, "Edit")] // another foreground window
    [InlineData(100, 101, 200, 10, "Edit")] // ownership changed
    [InlineData(100, 101, 100, 20, "Edit")] // handle belongs to another process
    [InlineData(100, 101, 100, 10, "Button")] // handle was replaced
    [InlineData(0, 0, 0, 0, "")] // focus information unavailable
    public void Focus_changes_between_batches_stop_before_more_input(long foreground, long focus, long root,
        int pid, string nativeClass)
    {
        var fault = Assert.Throws<DesktopFault>(() => DesktopTypingFocus.CheckBatch(false, Editor,
            () => throw new InvalidOperationException(), () => new NativeTypingFocus(foreground, focus, root, pid, nativeClass)));
        Assert.Equal(ActStatus.Blocked, fault.Status);
        Assert.Equal(ReasonCodes.FocusChanged, fault.Reason);
    }

    [Theory]
    [InlineData("Notepad.exe", "document", "RichEditD2DPT", false, true)]
    [InlineData("fixture.exe", "edit", "WindowsForms10.EDIT.app.0", false, true)]
    [InlineData("editor.exe", "edit", "Edit", false, true)]
    [InlineData("chrome.exe", "edit", "Edit", false, false)]
    [InlineData("msedge.exe", "edit", "Edit", false, false)]
    [InlineData("editor.exe", "edit", "Edit", true, false)]
    [InlineData("editor.exe", "pane", "Edit", false, false)]
    [InlineData("editor.exe", "edit", "Chrome_RenderWidgetHostHWND", false, false)]
    [InlineData("editor.exe", "edit", "", false, false)]
    public void Only_known_native_editor_candidates_can_be_verified(string process, string role, string nativeClass,
        bool password, bool expected) => Assert.Equal(expected,
        DesktopTypingFocus.IsCandidate(process, role, nativeClass, password));
}
