using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.SystemTools;

/// <summary>Settings the system tools need from config (contract §14) plus their own time limits.</summary>
public sealed record SystemToolsOptions
{
    /// <summary>Exact executable paths that <c>app_launch</c> may start besides Start-menu apps (config <c>app_launch_allow_paths</c>).</summary>
    public IReadOnlyList<string> AppLaunchAllowPaths { get; init; } = [];

    /// <summary>Time limit for a normal system call. A hung call returns <c>error engine_timeout</c>.</summary>
    public int OperationTimeoutMs { get; init; } = 5000;

    /// <summary>Time limit for <c>apps_list</c> and <c>app_launch</c>, which talk to the shell.</summary>
    public int ShellTimeoutMs { get; init; } = 15000;

    /// <summary>The user-activity pause is not applied to audio, media and clipboard (they send no real input).</summary>
    public const string EngineName = "system";
}

/// <summary>
/// A plain value, or the result that explains why there is none (contract proposal Q5: the §7 methods return plain
/// values and cannot say denied / blocked / engine_timeout, so the tool files read this richer shape).
/// </summary>
public sealed record SystemOutcome<T>(T? Value, ActResult? Failure, Timing Timing)
{
    public bool Ok => Failure is null;

    public static SystemOutcome<T> Success(T value, Timing timing) => new(value, null, timing);

    public static SystemOutcome<T> Fail(ActResult failure) => new(default, failure, failure.Timing);
}

/// <summary>Thrown by the plain §7 methods when the richer outcome is a failure.</summary>
public sealed class SystemToolFault(ActResult result) : Exception(result.Detail ?? result.Reason ?? "system tool failed")
{
    public ActResult Result { get; } = result;
}

/// <summary>Whether any real effect has been started. Read when a call is cancelled so input_sent stays accurate.</summary>
internal sealed class InputProgress
{
    public volatile bool Sent;
}

/// <summary>Stopwatch with the phase keys of contract §6 (resolve, precheck, gate, input, verify, read, format).</summary>
internal sealed class PhaseTimer
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _phaseStart = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> _phases = new(StringComparer.Ordinal);

    public void End(string phase)
    {
        var now = Stopwatch.GetTimestamp();
        _phases[phase] = _phases.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(_phaseStart, now).TotalMilliseconds;
        _phaseStart = now;
    }

    /// <summary>Restarts the phase clock without recording, so gate or wait time can be excluded from the next phase.</summary>
    public void Skip() => _phaseStart = Stopwatch.GetTimestamp();

    public double TotalMs => Stopwatch.GetElapsedTime(_start).TotalMilliseconds;

    public Timing ToTiming() => new(TotalMs, new Dictionary<string, double>(_phases, StringComparer.Ordinal));
}
