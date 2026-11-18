namespace OmniWin.Core;

/// <summary>Reason codes (contract §10.2). Use these exact strings; new ones need a proposal.</summary>
public static class ReasonCodes
{
    public const string OccludedByWindow = "occluded_by_window";
    public const string OccludedSameWindow = "occluded_same_window";
    public const string ModalBlocking = "modal_blocking";
    public const string Disabled = "disabled";
    public const string Offscreen = "offscreen";
    public const string NotFound = "not_found";
    public const string FocusFailed = "focus_failed";
    public const string FocusChanged = "focus_changed";
    public const string ForegroundFailed = "foreground_failed";
    public const string WindowChanged = "window_changed";
    public const string ElevatedTarget = "elevated_target";
    public const string SecureDesktop = "secure_desktop";
    public const string UserActive = "user_active";
    public const string KillSwitch = "kill_switch";
    public const string RateLimit = "rate_limit";
    public const string PolicyForbidden = "policy_forbidden";
    public const string UserDenied = "user_denied";
    public const string ConfirmTimeout = "confirm_timeout";
    public const string UnsupportedAction = "unsupported_action";
    public const string ReadbackMismatch = "readback_mismatch";
    public const string EngineTimeout = "engine_timeout";
    public const string BrowserNotConnected = "browser_not_connected";
    public const string NavigationFailed = "navigation_failed";
    public const string CrossOriginFrame = "cross_origin_frame";

    /// <summary>
    /// Contract 2.1. Input stopped partway (kill switch, user activity, cancellation) after some of it was sent.
    /// Always status=error with input_sent=true; the detail says how much was sent and why it stopped.
    /// </summary>
    public const string InputInterrupted = "input_interrupted";

    /// <summary>Contract 2.6: a learned type step found text in a field that was empty when the task was learned; nothing was typed.</summary>
    public const string FieldNotEmpty = "field_not_empty";
}
