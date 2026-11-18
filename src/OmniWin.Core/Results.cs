namespace OmniWin.Core;

// ---------- timing (contract §6) ----------

/// <summary>Server-side timing. Phase keys used by engines: resolve, precheck, gate, input, verify, read, format.</summary>
public sealed record Timing(double TotalMs, IReadOnlyDictionary<string, double> PhasesMs)
{
    public static readonly IReadOnlyDictionary<string, double> NoPhases = new Dictionary<string, double>(0);

    public static Timing Total(double totalMs) => new(totalMs, NoPhases);
}

// ---------- observations ----------

public sealed record Observation(
    string Target,            // "w3" (desktop window) or "t1" (browser tab)
    long Generation,          // generation of Target after this read
    string Text,              // compact text in the §9 format: this is what the model reads
    int ElementCount,
    int OmittedCount,
    bool IsDiff,
    long? BaseGeneration,     // set when IsDiff
    Timing Timing);

// ---------- action results ----------

public enum ActStatus { Confirmed, Contradicted, Unverified, Timeout, Blocked, Stale, Denied, Error }

public sealed record ActResult(
    ActStatus Status,
    bool InputSent,           // MUST be accurate. false = nothing reached the target app.
    string? Reason,           // machine-readable code from ReasonCodes (§10.2)
    string? Detail,           // one short human sentence
    Observation? After,       // diff since the request's generation, when available
    string? Channel,          // see Channels
    Timing Timing,
    string? StepId = null,    // run-log id when the action was recorded (§12)
    string? ExpectOutcome = null, // rendered as "expect: ..." (§9.3), e.g. "window_opened \"Save As\" → met"
    RiskClass? Risk = null)   // Q6: label only. Risky is printed as risk=risky and recorded in the audit log; it never changes or blocks the action.
{
    public static ActResult Blocked(string reason, string detail, Timing timing) =>
        new(ActStatus.Blocked, false, reason, detail, null, null, timing);

    public static ActResult Denied(string reason, string detail, Timing timing) =>
        new(ActStatus.Denied, false, reason, detail, null, null, timing);

    public static ActResult Failed(bool inputSent, string reason, string detail, Timing timing) =>
        new(ActStatus.Error, inputSent, reason, detail, null, null, timing);
}

public sealed record WaitResult(bool Met, string? Reason, Observation? After, Timing Timing);

/// <summary>Values for <see cref="ActResult.Channel"/>.</summary>
public static class Channels
{
    public const string PhysicalClick = "physical_click";
    public const string Invoke = "invoke";
    public const string Toggle = "toggle";
    public const string Select = "select";
    public const string SetValue = "set_value";
    public const string UnicodeTyping = "unicode_typing";
    public const string Keys = "keys";
    /// <summary>The focus fallback tapped the F24 key so Windows would allow the foreground change.</summary>
    public const string KeyF24 = "key:F24";
    /// <summary>desktop_act hover: the pointer was moved onto a control and nothing was pressed.</summary>
    public const string PhysicalHover = "physical_hover";
    /// <summary>desktop_act drag: the button was pressed on one control, the pointer moved, and the button released on another.</summary>
    public const string PhysicalDrag = "physical_drag";
    public const string CdpMouse = "cdp_mouse";
    public const string CdpInsertText = "cdp_insert_text";
    public const string Api = "api";
}
