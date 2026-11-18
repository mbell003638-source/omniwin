using OmniWin.Core;

namespace OmniWin.Desktop;

/// <summary>Checks observed native caret progress without blocking the UIA worker.</summary>
internal static class PacketEditorProgress
{
    internal static bool TryAcknowledge(int expectedCaret, Func<(int Start, int End)?> readSelection,
        Action boundary, Func<bool> deadlineExpired)
    {
        boundary();
        var selection = readSelection();
        if (selection is { } observed && observed.Start == expectedCaret && observed.End == expectedCaret)
            return true;

        if (deadlineExpired())
            throw new DesktopFault(ActStatus.Blocked, ReasonCodes.ReadbackMismatch,
                "The native editor did not acknowledge the previous character; no further typing was sent.");

        return false;
    }
}
