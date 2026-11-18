using System.Globalization;
using System.Text;
using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>
/// Text for run_steps, workflow_run, workflow_save and workflow_list. The first line of each is a status line the audit
/// filter reads (status=, input_sent=, reason=, risk=). It never contains a step= token: that token belongs to single
/// acts (the run-log id), and a run's steps are listed below with their own.
/// </summary>
public static class WorkflowFormatter
{
    /// <summary>A model-supplied name as one token: one line, no spaces, at most 64 characters.</summary>
    private static string Token(string text)
    {
        var line = ResultFormatter.OneLine(text).Replace(' ', '_');
        return line.Length <= 64 ? line : line[..64];
    }

    public static string Format(RunOutcome run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append(run.Tool);
        if (run.Name is not null) sb.Append(" name=").Append(Token(run.Name));
        sb.Append(" status=").Append(ResultFormatter.StatusName(run.Status))
          .Append(" input_sent=").Append(run.InputSent ? "true" : "false");
        if (run.Reason is not null) sb.Append(" reason=").Append(run.Reason);
        sb.Append(" steps=").Append(run.Steps.Count(s => s.Passed).ToString(inv)).Append('/').Append(run.Total.ToString(inv));
        if (run.StoppedAt != 0) sb.Append(" stopped_at=").Append(run.StoppedAt.ToString(inv));
        if (run.Risk == RiskClass.Risky) sb.Append(" risk=risky");
        sb.Append(" total=").Append(ResultFormatter.FormatMs(run.TotalMs)).Append("ms");

        if (run.Detail is not null) sb.Append('\n').Append("detail: ").Append(ResultFormatter.OneLine(run.Detail));

        foreach (var step in run.Steps)
        {
            sb.Append('\n').Append("step ").Append(step.Index.ToString(inv)).Append('/').Append(run.Total.ToString(inv))
              .Append(' ').Append(Token(step.Call.Tool)).Append(step.Call.Action is { Length: > 0 } a ? " " + ResultFormatter.OneLine(a) : string.Empty)
              .Append(step.Passed ? " passed" : " stopped").Append('\n')
              .Append(ResultFormatter.Format(step.Result));
        }

        if (run.Observation is { Text.Length: > 0 })
        {
            sb.Append('\n').Append("current observation:").Append('\n').Append(run.Observation.Text);
        }

        return sb.ToString();
    }

    public static string Format(SaveOutcome save)
    {
        ArgumentNullException.ThrowIfNull(save);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append("workflow_save name=").Append(Token(save.Name));
        switch (save.Status)
        {
            case SaveStatus.Saved:
                sb.Append(" status=saved input_sent=false steps=").Append(save.StepCount.ToString(inv))
                  .Append(" params=").Append(save.ParamNames.Count == 0 ? "none" : string.Join(',', save.ParamNames));
                break;
            case SaveStatus.Denied:
                sb.Append(" status=denied input_sent=false reason=").Append(save.Reason);
                break;
            default:
                sb.Append(" status=blocked input_sent=false reason=").Append(save.Reason);
                break;
        }

        if (save.Detail is not null) sb.Append('\n').Append("detail: ").Append(ResultFormatter.OneLine(save.Detail));
        return sb.ToString();
    }

    public static string Format(IReadOnlyList<WorkflowSummary> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128);
        sb.Append("workflows count=").Append(list.Count.ToString(inv));
        foreach (var w in list)
        {
            sb.Append('\n').Append(w.Name).Append(' ').Append(ResultFormatter.Quote(w.Description, ResultFormatter.MaxNameChars))
              .Append(" steps=").Append(w.StepCount.ToString(inv))
              .Append(" params=").Append(w.ParamNames.Count == 0 ? "none" : string.Join(',', w.ParamNames));
        }

        return sb.ToString();
    }
}

/// <summary>The "workflows" row of omniwin_status.</summary>
public sealed class WorkflowsComponent(WorkflowStore store, IRunLog log) : IComponentStatus
{
    public string Component => "workflows";

    public string State => "ready";

    public string? Detail => string.Create(CultureInfo.InvariantCulture, $"{store.Count()} saved, {log.Steps.Count} steps in the run log");
}
