namespace OmniWin.Core;

// ---------- safety (contract §7, §13). WS0 implements in OmniWin.Safety; every engine calls the gate. ----------

public enum RiskClass { Read, Normal, Risky, Forbidden }

public sealed record ActionDescriptor(
    string Tool, string Engine, string Action,
    ElementDescriptor? Target,        // focused element for keys when resolved; null for untargeted system actions
    string? Process, string? WindowTitle, string? Url,
    string? ValuePreview,             // already redacted by the engine (passwords never included)
    string? Keys)
{
    /// <summary>Lets the gate reject a detected delete command without receiving or logging the typed text.</summary>
    public bool ContainsDeleteCommand { get; init; }

    /// <summary>For a drag: the control the item is dropped on. A drop onto the Recycle Bin or a delete action is a delete.</summary>
    public ElementDescriptor? DropTarget { get; init; }
}

/// <summary>
/// When <see cref="UserConfirmed"/> is true the user was shown the confirm dialog, which may have been open for up to 60 s.
/// The engine must then run its pre-checks again before sending input; if they fail the result is blocked (contract 2.1).
/// </summary>
public sealed record GateResult(bool Allowed, RiskClass Risk, string? Reason, bool UserConfirmed, string? Detail = null);

public interface ISafetyGate
{
    RiskClass Classify(ActionDescriptor a);

    /// <summary>Applies the configured owner policy before input dispatch.</summary>
    Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct);
}

public interface IKillSwitch
{
    bool IsTripped { get; }
    CancellationToken Token { get; }
}

public interface IUserActivity
{
    bool UserActiveRecently { get; }
    DateTimeOffset LastRealInput { get; }
}

public interface IAuditLog
{
    void Record(AuditEntry e);
}

public sealed record AuditEntry(DateTimeOffset At, string Tool, string ArgsRedactedJson, string Status,
                                bool InputSent, double TotalMs, string? Reason, string? StepId,
                                string? Risk = null); // Q6: "risky" when the result line carried risk=risky; label only, never blocks

/// <summary>p50/p95/p99 per operation.</summary>
public interface IMetrics
{
    void Record(string op, double ms);
    MetricsSnapshot Snapshot();
}

public sealed record MetricsSnapshot(IReadOnlyDictionary<string, (double P50, double P95, double P99, long Count)> Ops);

/// <summary>
/// Contract 2.1. Each engine and safety component registers one of these in DI so omniwin_status can report it
/// (e.g. Component="browser", State="attached"). Components with no registration are reported as "not_built".
/// </summary>
public interface IComponentStatus
{
    string Component { get; }
    string State { get; }
    string? Detail { get; }
}
