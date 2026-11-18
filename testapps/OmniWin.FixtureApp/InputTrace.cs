using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace OmniWin.FixtureApp;

/// <summary>Opt-in, fixture-only tracing. The timed base.WndProc call performs no trace I/O or allocations.</summary>
internal sealed class InputTrace
{
    private const int Capacity = 16_384;
    private readonly Entry[] _entries = new Entry[Capacity];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private int _count;
    private int _dropped;
    private bool _written;

    internal static InputTrace? TryCreate() => Environment.GetEnvironmentVariable("OMNIWIN_FIXTURE_TRACE_INPUT") == "1"
        ? new InputTrace() : null;

    internal TextBox CreateEditor() => new TracedTextBox(this);
    internal Button CreateButton() => new TracedButton(this);

    private static bool Tracked(int message) => message is
        0x0007 or // WM_SETFOCUS
        0x0008 or // WM_KILLFOCUS
        0x0020 or // WM_SETCURSOR
        0x0021 or // WM_MOUSEACTIVATE
        0x0201 or // WM_LBUTTONDOWN
        0x0202 or // WM_LBUTTONUP
        0x0203;   // WM_LBUTTONDBLCLK (the second down can be translated to this message)

    private void Record(Control control, Message message, long start, long end)
    {
        // All fixture controls and these callbacks belong to the same UI thread. Store completion order;
        // timestamps identify nesting across controls. No strings or objects are allocated per entry.
        if (_count == Capacity)
        {
            _dropped++;
            return;
        }
        _entries[_count++] = new Entry(control.Name, message.HWnd.ToInt64(), message.Msg,
            start, end, Environment.CurrentManagedThreadId);
    }

    internal void WriteReport()
    {
        if (_written) return;
        _written = true;
        var report = new
        {
            StartedUtc = _started, FinishedUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId, StopwatchFrequency = Stopwatch.Frequency,
            Capacity, Count = _count, Dropped = _dropped,
            TimingScope = "Inclusive base.WndProc elapsed ticks; nested message intervals must not be summed.",
            Entries = _entries.AsSpan(0, _count).ToArray(),
        };
        // Only on closure, after the existing READY protocol. The parent must drain redirected stdout.
        Console.WriteLine("OMNIWIN_INPUT_TRACE " + JsonSerializer.Serialize(report));
    }

    private sealed class TracedTextBox(InputTrace trace) : TextBox
    {
        protected override void WndProc(ref Message message)
        {
            if (!Tracked(message.Msg))
            {
                base.WndProc(ref message);
                return;
            }
            var start = Stopwatch.GetTimestamp();
            try { base.WndProc(ref message); }
            finally { trace.Record(this, message, start, Stopwatch.GetTimestamp()); }
        }
    }

    private sealed class TracedButton(InputTrace trace) : Button
    {
        protected override void WndProc(ref Message message)
        {
            if (!Tracked(message.Msg))
            {
                base.WndProc(ref message);
                return;
            }
            var start = Stopwatch.GetTimestamp();
            try { base.WndProc(ref message); }
            finally { trace.Record(this, message, start, Stopwatch.GetTimestamp()); }
        }
    }

    private readonly record struct Entry(string Control, long Hwnd, int Message, long StartTicks, long EndTicks,
        int ManagedThreadId);
}
