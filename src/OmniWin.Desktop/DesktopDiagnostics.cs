using System.Diagnostics;

namespace OmniWin.Desktop;

internal readonly record struct DiagnosticTiming(double TotalMs, int Calls);

/// <summary>Opt-in worker-local diagnostic timings, separate from the contracted action phases.</summary>
internal static class DesktopDiagnostics
{
    [ThreadStatic] private static Capture? _current;
    [ThreadStatic] private static string? _phase;

    internal sealed class Capture
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<string, DiagnosticTiming> _timings = new(StringComparer.Ordinal);

        internal void Record(string phase, string name, long started)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var key = string.Concat(phase, ".", name);
            lock (_sync)
            {
                var previous = _timings.GetValueOrDefault(key);
                _timings[key] = new DiagnosticTiming(previous.TotalMs + elapsed, previous.Calls + 1);
            }
        }

        // A read-only preparation can time out before its provider returns on the worker.
        internal IReadOnlyDictionary<string, DiagnosticTiming> Snapshot()
        {
            lock (_sync)
            {
                return new Dictionary<string, DiagnosticTiming>(_timings, StringComparer.Ordinal);
            }
        }
    }

    internal static Activation Activate(Capture? capture, string phase) => new(capture, phase);

    internal static Scope Measure(string name) => _current is { } capture
        ? new Scope(capture, _phase!, name) : default;

    internal readonly struct Activation : IDisposable
    {
        private readonly Capture? _previous;
        private readonly string? _previousPhase;

        internal Activation(Capture? capture, string phase)
        {
            _previous = _current;
            _previousPhase = _phase;
            _current = capture;
            _phase = phase;
        }

        public void Dispose()
        {
            _current = _previous;
            _phase = _previousPhase;
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Capture? _capture;
        private readonly string? _phase;
        private readonly string? _name;
        private readonly long _started;

        internal Scope(Capture capture, string phase, string name)
        {
            _capture = capture;
            _phase = phase;
            _name = name;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose() => _capture?.Record(_phase!, _name!, _started);
    }
}
