using OmniWin.Core;

namespace OmniWin.Safety;

/// <summary>
/// The one gate every engine calls after checking its target and before sending input (contract §7).
/// Owner's policy: everything is allowed except deleting (<see cref="DeletePolicy"/>) and the secure desktop.
/// No confirmation dialog, so <see cref="GateResult.UserConfirmed"/> is always false.
/// With <c>safety.allow_delete</c> (owner decision 2026-11-19) deleting is allowed too and labelled <see cref="RiskClass.Risky"/>;
/// blocked processes and the reserved stop key are still refused.
/// </summary>
public sealed class SafetyGate : ISafetyGate, IComponentStatus
{
    private readonly IKillSwitch _killSwitch;
    private readonly HashSet<string> _blockedProcesses;
    private readonly IReadOnlyList<string> _extraWords;
    private readonly string _stopChord;
    private readonly bool _allowDelete;

    public SafetyGate(OmniWinConfig config, IKillSwitch killSwitch)
    {
        ArgumentNullException.ThrowIfNull(config);
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _blockedProcesses = new HashSet<string>(DeletePolicy.DefaultBlockedProcesses, StringComparer.OrdinalIgnoreCase);
        _blockedProcesses.UnionWith(config.ProcessBlocklistExtra);
        _blockedProcesses.UnionWith(DeletePolicy.OwnProcesses);
        _extraWords = [.. config.BlockedWordsExtra, .. config.RiskLexiconExtra];
        _stopChord = DeletePolicy.CanonicalChord(config.KillHotkey);
        _allowDelete = config.Safety.AllowDelete;
    }

    public string Component => "safety_gate";

    public string State => _allowDelete ? "off" : "no_delete";

    public string? Detail => _allowDelete
        ? "deleting allowed (safety.allow_delete) and labelled risk=risky; blocked processes and the stop key still apply"
        : "everything allowed except deleting; no confirmation prompts";

    public RiskClass Classify(ActionDescriptor a)
    {
        var (why, deleting) = Evaluate(a);
        return why is null ? RiskClass.Normal : deleting && _allowDelete ? RiskClass.Risky : RiskClass.Forbidden;
    }

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        ct.ThrowIfCancellationRequested();
        if (_killSwitch.IsTripped)
        {
            return Task.FromResult(new GateResult(false, RiskClass.Normal, ReasonCodes.KillSwitch, false,
                "OmniWin is stopped (Ctrl+Alt+Shift+K). Press it again to resume."));
        }

        var (why, deleting) = Evaluate(a);
        if (why is null) return Task.FromResult(new GateResult(true, RiskClass.Normal, null, false));
        // Allowed, but the delete reason stays in Detail and the risk label records it.
        if (deleting && _allowDelete) return Task.FromResult(new GateResult(true, RiskClass.Risky, null, false, why));
        return Task.FromResult(new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, why));
    }

    /// <summary>Why the action is refused (null when allowed), and whether the reason is deleting (the part allow_delete lifts).</summary>
    private (string? Why, bool Deleting) Evaluate(ActionDescriptor action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Keys is { } keys)
        {
            foreach (var chord in keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (DeletePolicy.CanonicalChord(chord) == _stopChord)
                {
                    return ("The stop key is reserved for real keyboard input.", false);
                }
            }
        }

        if (DeletePolicy.WhyProcessBlocked(action, _blockedProcesses) is { } blocked) return (blocked, false);
        return DeletePolicy.WhyDeleting(action, _extraWords) is { } deleting ? (deleting, true) : (null, false);
    }
}
