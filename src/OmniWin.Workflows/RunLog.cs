using System.Globalization;
using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>
/// In-memory run log (contract §12). Thread safe. Keeps the newest <see cref="Capacity"/> steps; ids keep counting up and are
/// never reused, so an old id that was dropped simply returns null.
/// </summary>
public sealed class RunLog : IRunLog
{
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly List<RunStep> _steps = [];
    private readonly TimeProvider _time;
    private int _nextStep = 1;
    private int _nextParam = 1;

    public RunLog(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public IReadOnlyList<RunStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public RunStep Append(RunStepDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            var id = "s" + (_nextStep++).ToString(CultureInfo.InvariantCulture);
            var param = draft.HasValue ? "p" + (_nextParam++).ToString(CultureInfo.InvariantCulture) : null;
            var step = new RunStep(id, draft.Tool, draft.Engine, draft.Action, draft.Target,
                new Dictionary<string, string>(draft.Args, StringComparer.Ordinal), param, draft.ExpectOutcome, _time.GetUtcNow(), draft.TargetEmptyBefore);
            _steps.Add(step);
            if (_steps.Count > Capacity)
            {
                _steps.RemoveRange(0, _steps.Count - Capacity);
            }

            return step;
        }
    }

    public RunStep? Get(string stepId)
    {
        lock (_gate)
        {
            return _steps.Find(s => string.Equals(s.StepId, stepId, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<RunStep>? Range(string fromStepId, string toStepId)
    {
        lock (_gate)
        {
            var from = _steps.FindIndex(s => string.Equals(s.StepId, fromStepId, StringComparison.Ordinal));
            var to = _steps.FindIndex(s => string.Equals(s.StepId, toStepId, StringComparison.Ordinal));
            if (from < 0 || to < 0 || to < from)
            {
                return null;
            }

            return [.. _steps.Skip(from).Take(to - from + 1)];
        }
    }
}
