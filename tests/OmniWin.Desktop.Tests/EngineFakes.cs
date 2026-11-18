using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

/// <summary>What a fake editor does with a character it is sent.</summary>
internal enum Landing
{
    /// <summary>The editor takes it: the text grows and the caret moves.</summary>
    Takes,

    /// <summary>The editor never takes it (Windows dropped the VK_PACKET): nothing changes.</summary>
    Drops,

    /// <summary>The editor takes it only after <see cref="FakeEditor.LateAfterMs"/> (a slow editor that looked like it had dropped it).</summary>
    LandsLate,
}

/// <summary>
/// A text editor with a caret that can lose characters, take them late, or take them slowly. It stands in for the native
/// editor so the real typing pump and the real engine run against it without any window or input.
/// </summary>
internal sealed class FakeEditor
{
    private readonly object _sync = new();
    private readonly StringBuilder _text = new();
    private readonly Dictionary<char, int> _sends = [];
    private readonly List<Timer> _timers = [];   // held so a pending late character is not collected before it lands
    private int _caret;

    /// <summary>What happens to the Nth send (1-based) of a character. The default takes everything.</summary>
    internal Func<char, int, Landing> Policy { get; set; } = static (_, _) => Landing.Takes;

    internal int LateAfterMs { get; set; } = 300;

    /// <summary>Each send takes this long (a slow editor).</summary>
    internal int SendMs { get; set; }

    internal int TotalSends { get; private set; }

    internal string Text { get { lock (_sync) return _text.ToString(); } }

    internal int Caret { get { lock (_sync) return _caret; } }

    internal int SendsOf(char c) { lock (_sync) return _sends.GetValueOrDefault(c); }

    /// <summary>Raised on the sending thread after a character is sent, with the number of characters in the editor then.</summary>
    internal Action<int>? AfterSend { get; set; }

    internal InputReceipt Send(string batch)
    {
        if (SendMs > 0) Thread.Sleep(SendMs);
        foreach (var c in batch)
        {
            Landing landing;
            lock (_sync)
            {
                TotalSends++;
                var n = _sends[c] = _sends.GetValueOrDefault(c) + 1;
                landing = Policy(c, n);
            }

            switch (landing)
            {
                case Landing.Takes:
                    Insert(c);
                    break;
                case Landing.LandsLate:
                    lock (_sync) _timers.Add(new Timer(_ => Insert(c), null, LateAfterMs, Timeout.Infinite));
                    break;
            }
        }

        AfterSend?.Invoke(Text.Length);
        return new InputReceipt(batch.Length, batch.Length, 0.1);
    }

    private void Insert(char c)
    {
        lock (_sync)
        {
            _text.Insert(_caret, c);
            _caret++;
        }
    }

    internal (int Start, int End)? Selection { get { lock (_sync) return (_caret, _caret); } }
}

internal sealed class AllowAllGate : ISafetyGate
{
    public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct) =>
        Task.FromResult(new GateResult(true, RiskClass.Normal, null, false));
}

internal sealed class NeverTrippedKill : IKillSwitch
{
    public bool IsTripped => false;
    public CancellationToken Token => CancellationToken.None;
}

/// <summary>A stop key that is already pressed (round 24 review: the early refusal of a focus attempt).</summary>
internal sealed class TrippedKill : IKillSwitch
{
    public bool IsTripped => true;
    public CancellationToken Token => new(canceled: true);
}

internal sealed class RecordingMetrics : IMetrics
{
    public void Record(string op, double ms)
    {
    }

    public MetricsSnapshot Snapshot() => new(new Dictionary<string, (double, double, double, long)>());
}

/// <summary>
/// The UI Automation side, faked. A type act runs the real <see cref="TypingPump"/> against a <see cref="FakeEditor"/>; everything
/// else answers with what the test configured. It runs on the engine's worker thread, as the real one does.
/// </summary>
internal sealed class FakeDesktopBackend : IDesktopBackend
{
    private readonly TypingSession _typing = new();
    private int _observeCalls;

    internal FakeEditor Editor { get; } = new();

    /// <summary>The control the act is aimed at (matters to the value-setting policy).</summary>
    internal ElementDescriptor Target { get; set; } = new("e1", "desktop", "fake.exe", "Fake window", null, "edit", "Text", "", false, true, []);

    /// <summary>How long a fake packet editor waits before it calls a character lost (kept short so tests are quick).</summary>
    internal int AckWindowMs { get; set; } = 20;

    /// <summary>The readback for the default check. Null result means "cannot read". Default: the editor's text equals what was typed.</summary>
    internal Func<InputProgress, ConditionCheck?>? Readback { get; set; }

    internal int ReadbackCalls;
    internal int ObserveCalls => Volatile.Read(ref _observeCalls);

    /// <summary>The number of characters in the editor when the last observe was answered (proves it was answered while typing).</summary>
    internal int TypedAtLastObserve { get; private set; } = -1;

    /// <summary>Set by tests that fake the foreground attempt.</summary>
    internal Func<long, bool>? ForegroundImpl { get; set; }

    public (bool InputSent, bool KeyTapped) LastForegroundAttempt { get; set; }

    public IReadOnlyList<WindowInfo> ListWindows(CancellationToken ct) => [];

    public Observation Observe(DesktopObserveRequest request, CancellationToken ct, bool fresh = false)
    {
        Interlocked.Increment(ref _observeCalls);
        TypedAtLastObserve = Editor.Text.Length;
        return new Observation("w1", 1, "w1 \"Fake window\"", 0, 0, false, null, Timing.Total(0));
    }

    public ConditionCheck Evaluate(Expectation condition, string? windowId, IReadOnlySet<string>? openBefore, CancellationToken ct) =>
        new(true, "fake");

    public ElementDescriptor? DescribeRef(string elementRef, CancellationToken ct) => Target;

    public ResolvedRef? ResolveSelector(string process, string titlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) => null;

    public string? WindowIdOfRef(string? reference) => "w1";

    public PreparedInput Prepare(DesktopCommand command, PreparedInput? previous, CancellationToken ct,
        InputProgress? progress = null, Func<string?>? stopReason = null) =>
        new("w1", null, "id1", new ActionDescriptor(command.Tool, "desktop", command.Action, Target, Target.Process, Target.WindowTitle, null, null, null))
        {
            Hwnd = 0x1234,
        };

    public void Dispatch(DesktopCommand command, PreparedInput prepared, InputProgress progress, Func<string?> stopReason, CancellationToken ct)
    {
        _typing.AbandonStale();
        switch (command.Action)
        {
            case "type":
                progress.ExpectedTypeValue = command.Value;
                var seams = new TypingSeams
                {
                    BeforeBatch = (_, token) => token.ThrowIfCancellationRequested(),
                    StopCheck = token => token.ThrowIfCancellationRequested(),
                    CheckSubmission = _ => { },
                    Send = Editor.Send,
                    CaretBefore = () => Editor.Selection,
                    AwaitScalar = (_, _) => { },
                    Acknowledged = (expected, sentAt, _) => PacketEditorProgress.TryAcknowledge(expected, () => Editor.Selection, () => { },
                        () => Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds >= AckWindowMs),
                    Resend = (caretBefore, scalar, _) =>
                    {
                        if (Editor.Caret != caretBefore) return false;
                        progress.Accept(Editor.Send(scalar), Channels.UnicodeTyping);
                        return true;
                    },
                };
                _typing.Start(progress, new TypingPump(command.Value!, 1, Editor.Caret, progress, seams), ct);
                break;
            case "focus":
                UiaDesktop.RunForegroundAttempt(FocusAttempt, 0x1234, progress, null);
                break;
            default:
                progress.Pattern(() => { }, command.Action == "set_value" ? Channels.SetValue : Channels.Api);
                break;
        }
    }

    /// <summary>What the fake native focus attempt does (default: nothing sent).</summary>
    internal UiaDesktop.ForegroundAttemptFn FocusAttempt { get; set; } = (long hwnd, out bool input, out bool key, Action? boundary) =>
    {
        input = false;
        key = false;
        return true;
    };

    public void ContinueTyping(InputProgress progress, CancellationToken ct) => _typing.Continue(progress, ct);

    public void CancelTyping(InputProgress progress) => _typing.Cancel(progress);

    public ConditionCheck? EvaluateDefault(DesktopCommand command, PreparedInput prepared, InputProgress progress, CancellationToken ct)
    {
        Interlocked.Increment(ref ReadbackCalls);
        if (command.Action is not ("type" or "set_value" or "clear")) return null;
        if (Readback is { } custom) return custom(progress);
        var expected = command.Action == "type" ? progress.ExpectedTypeValue : command.Value;
        var text = Editor.Text;
        return new ConditionCheck(text == expected, "fake readback", text != expected);
    }

    public LabelSet? Labels(string windowId) => null;

    public Observation? ReadAfter(string windowId, DesktopSnapshot? baseline, long? since, CancellationToken ct) => null;

    public bool BringToForeground(long hwnd, Func<string?> stopReason, CancellationToken ct) => ForegroundImpl?.Invoke(hwnd) ?? true;

    public void Dispose()
    {
    }

    internal DesktopEngine NewEngine(int connectionMs = 2000, int transactionMs = 1000, IKillSwitch? kill = null)
    {
        var config = new OmniWinConfig { Timeouts = new TimeoutsConfig { UiaConnectionMs = connectionMs, UiaTransactionMs = transactionMs } };
        return new DesktopEngine(config, new AllowAllGate(), kill ?? new NeverTrippedKill(), new RecordingMetrics(),
            NullLogger<DesktopEngine>.Instance, this);
    }
}
