using OmniWin.Core;

namespace OmniWin.Safety;

/// <summary>
/// The one gate every engine calls after checking its target and before sending input (contract §7).
/// Owner's policy: everything is allowed except deleting (<see cref="DeletePolicy"/>) and the secure desktop.
/// No confirmation dialog, so <see cref="GateResult.UserConfirmed"/> is always false.
/// </summary>
public sealed class SafetyGate : ISafetyGate, IComponentStatus
{
    private readonly IKillSwitch _killSwitch;
    private readonly HashSet<string> _blockedProcesses;
    private readonly IReadOnlyList<string> _extraWords;
    private readonly string _stopChord;

    public SafetyGate(OmniWinConfig config, IKillSwitch killSwitch)
    {
        ArgumentNullException.ThrowIfNull(config);
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _blockedProcesses = new HashSet<string>(DeletePolicy.DefaultBlockedProcesses, StringComparer.OrdinalIgnoreCase);
        _blockedProcesses.UnionWith(config.ProcessBlocklistExtra);
        _blockedProcesses.UnionWith(DeletePolicy.OwnProcesses);
        _extraWords = [.. config.BlockedWordsExtra, .. config.RiskLexiconExtra];
        _stopChord = DeletePolicy.CanonicalChord(config.KillHotkey);
    }

    public string Component => "safety_gate";

    public string State => "no_delete";

    public string? Detail => "everything allowed except deleting; no confirmation prompts";

    public RiskClass Classify(ActionDescriptor a) =>
        WhyForbidden(a) is null ? RiskClass.Normal : RiskClass.Forbidden;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(a);
        ct.ThrowIfCancellationRequested();
        if (_killSwitch.IsTripped)
        {
            return Task.FromResult(new GateResult(false, RiskClass.Normal, ReasonCodes.KillSwitch, false,
                "OmniWin is stopped (Ctrl+Alt+Shift+K). Press it again to resume."));
        }

        var why = WhyForbidden(a);
        return Task.FromResult(why is null
            ? new GateResult(true, RiskClass.Normal, null, false)
            : new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, why));
    }

    private string? WhyForbidden(ActionDescriptor action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Keys is { } keys)
        {
            foreach (var chord in keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (DeletePolicy.CanonicalChord(chord) == _stopChord)
                {
                    return "The stop key is reserved for real keyboard input.";
                }
            }
        }

        return DeletePolicy.WhyForbidden(action, _blockedProcesses, _extraWords);
    }
}
