namespace OmniWin.Core;

/// <summary>Version of the shared contract (OmniWin_Handoff\02_Architecture_and_Contracts.md). WS0 bumps it on every accepted change (§17).</summary>
public static class OmniWinContract
{
    /// <summary>
    /// 2.6: learning. New tools <c>task_done</c> and <c>workflow_manage</c>; <c>omniwin_do</c> may run a trusted learned recipe and
    /// lists similar recipes with <c>needs_brain</c>; recipe cards and an append-only events file; one input lane shared by every
    /// OmniWin process in the session; selection tools; an optional decision-model slot (off by default).
    /// 2.5: every action result line carries <c>retry=</c> (done, safe, observe_first, check_state, wait, no); new tool
    /// <c>omniwin_guide</c>; <c>omniwin_do</c> (fast router); optional <c>engine_call</c> / <c>engines_list</c> (bridged engines,
    /// results always unverified); optional power tools; <c>desktop_act</c> drag and hover; <c>screen_view labels</c>.
    /// 2.3: integrated owner approvals for WS2 browser engine (Q1, Q3, Q6-Q16). Dedicated profile allowlist,
    /// isolated world execution, live target pre-checks, guarded navigation, honestly reported uncertainty,
    /// payment redaction, and in-page occlusion.
    /// </summary>
    public const string ContractVersion = "2.6";
}
