namespace OmniWin.Core;

/// <summary>
/// Contract 2.5: the <c>retry=</c> token on every action result line. It tells the model, in one word, what it may do next, so it
/// never has to infer it from the status text (borrowed from agent-desktop's "retry only when it is safe" disposition).
/// One rule in one place, derived from the status, the reason and whether input was sent.
/// <list type="bullet">
/// <item><c>done</c>: confirmed. Nothing to retry.</item>
/// <item><c>safe</c>: nothing was sent and nothing about the target is stale; the same call may be repeated.</item>
/// <item><c>observe_first</c>: nothing was sent, but the target changed or is not ready; observe again, fix the cause, then act.</item>
/// <item><c>check_state</c>: input WAS sent (or may have been) and the result is not confirmed; do not repeat the same input, because a
/// click, a typed character or a toggle can then happen twice; read the screen first.</item>
/// <item><c>wait</c>: nothing was sent because something else is busy (a running type, recent user input, a slow window); the same call
/// may succeed shortly.</item>
/// <item><c>no</c>: repeating the same call cannot work (denied, stopped, forbidden, unsupported); choose a different approach.</item>
/// </list>
/// </summary>
public static class RetryAdvice
{
    public const string Done = "done";
    public const string Safe = "safe";
    public const string ObserveFirst = "observe_first";
    public const string CheckState = "check_state";
    public const string Wait = "wait";
    public const string No = "no";

    public static string For(ActResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return For(result.Status, result.InputSent, result.Reason);
    }

    public static string For(ActStatus status, bool inputSent, string? reason)
    {
        if (status == ActStatus.Confirmed) return Done;

        // Once anything may have reached the app, a repeat can act twice. This holds for every non-confirmed status.
        if (inputSent) return CheckState;

        if (status == ActStatus.Denied) return No;

        switch (reason)
        {
            case ReasonCodes.KillSwitch:
            case ReasonCodes.PolicyForbidden:
            case ReasonCodes.UserDenied:
            case ReasonCodes.UnsupportedAction:
            case ReasonCodes.ElevatedTarget:
            case ReasonCodes.SecureDesktop:
            case ReasonCodes.CrossOriginFrame:
                return No;

            case ReasonCodes.UserActive:
            case ReasonCodes.RateLimit:
            case ReasonCodes.ConfirmTimeout:
            case ReasonCodes.BrowserNotConnected:
                return Wait;

            case ReasonCodes.NotFound:
            case ReasonCodes.OccludedByWindow:
            case ReasonCodes.OccludedSameWindow:
            case ReasonCodes.ModalBlocking:
            case ReasonCodes.Disabled:
            case ReasonCodes.Offscreen:
            case ReasonCodes.FocusFailed:
            case ReasonCodes.FocusChanged:
            case ReasonCodes.ForegroundFailed:
            case ReasonCodes.WindowChanged:
            case ReasonCodes.NavigationFailed:
                return ObserveFirst;
        }

        return status switch
        {
            ActStatus.Stale => ObserveFirst,
            ActStatus.Blocked => ObserveFirst,
            // An engine timeout before anything was sent: the same call can be tried again.
            _ => Safe,
        };
    }

    /// <summary>All values, for the guide and for tests.</summary>
    public static readonly IReadOnlyList<string> All = [Done, Safe, ObserveFirst, CheckState, Wait, No];
}
