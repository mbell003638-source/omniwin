using System.Collections.Concurrent;

namespace OmniWin.Core;

/// <summary>
/// <see cref="IMetrics"/> that keeps the last <see cref="Capacity"/> samples per operation and computes
/// nearest-rank percentiles on demand. <see cref="Record"/> is a short lock and an array write; no allocation after
/// the first sample of an operation.
/// </summary>
public sealed class RollingMetrics : IMetrics
{
    public const int Capacity = 2048;

    private readonly ConcurrentDictionary<string, Series> _ops = new(StringComparer.Ordinal);

    public void Record(string op, double ms)
    {
        ArgumentNullException.ThrowIfNull(op);
        _ops.GetOrAdd(op, static _ => new Series()).Add(ms);
    }

    public MetricsSnapshot Snapshot()
    {
        var result = new Dictionary<string, (double P50, double P95, double P99, long Count)>(_ops.Count, StringComparer.Ordinal);
        foreach (var (op, series) in _ops)
        {
            result[op] = series.Percentiles();
        }

        return new MetricsSnapshot(result);
    }

    private sealed class Series
    {
        private readonly Lock _lock = new();
        private readonly double[] _ring = new double[Capacity];
        private int _next;
        private long _count;

        public void Add(double ms)
        {
            lock (_lock)
            {
                _ring[_next] = ms;
                _next = (_next + 1) % Capacity;
                _count++;
            }
        }

        public (double P50, double P95, double P99, long Count) Percentiles()
        {
            double[] sorted;
            long count;
            lock (_lock)
            {
                count = _count;
                // Until the ring wraps, the samples are exactly entries 0.._count-1.
                sorted = new double[(int)Math.Min(count, Capacity)];
                Array.Copy(_ring, sorted, sorted.Length);
            }

            if (sorted.Length == 0)
            {
                return (0, 0, 0, 0);
            }

            Array.Sort(sorted);
            return (Rank(sorted, 0.50), Rank(sorted, 0.95), Rank(sorted, 0.99), count);
        }

        private static double Rank(double[] sorted, double p)
        {
            var index = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }
}
