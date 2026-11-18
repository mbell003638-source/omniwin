using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Desktop;

/// <summary>What the typing pump needs from the outside: the real editor and windows, or fakes in tests.</summary>
internal sealed class TypingSeams
{
    /// <summary>Boundary, focus and stop checks before a batch. The flag says whether this is the first batch.</summary>
    internal required Action<bool, CancellationToken> BeforeBatch { get; init; }

    /// <summary>The kill switch / user-activity / cancellation check made right before a batch is sent.</summary>
    internal required Action<CancellationToken> StopCheck { get; init; }

    /// <summary>Checks a batch that contains a line break for a command submission. Called only for such batches.</summary>
    internal required Action<string> CheckSubmission { get; init; }

    /// <summary>Sends one batch as VK_PACKET input.</summary>
    internal required Func<string, InputReceipt> Send { get; init; }

    /// <summary>Non-packet editors: the caret before the batch (or null when the editor shows none).</summary>
    internal required Func<(int Start, int End)?> CaretBefore { get; init; }

    /// <summary>Non-packet editors: wait until the scalar was taken (the caret moves, or a settle for the first one).</summary>
    internal required Action<(int Start, int End)?, bool> AwaitScalar { get; init; }

    /// <summary>Packet editors: has the caret reached the expected offset? Throws readback_mismatch when it never does.</summary>
    internal required Func<int, long, CancellationToken, bool> Acknowledged { get; init; }

    /// <summary>Packet editors: send the lost scalar once more (only when the caret is provably unmoved). False means not sent.</summary>
    internal required Func<int, string, CancellationToken, bool> Resend { get; init; }
}

/// <summary>
/// Types one text, one scalar per VK_PACKET batch (F16). One <see cref="Step"/> does at most one native action (one send, or one
/// acknowledgement check) and returns, so the single UI Automation worker is free between characters: a long text never keeps
/// every other desktop call waiting (file 23, A10). A scalar the editor never takes is sent once more, and a second miss for the
/// same scalar ends the act (A2). The text is in the editor only when the act's readback says so; that is the engine's job.
/// </summary>
internal sealed class TypingPump(string text, int batchLimit, int? packetCaret, InputProgress progress, TypingSeams seams)
{
    private int _offset;
    private int? _awaitingCaret;
    private long _sentAt;
    private string _lastBatch = "";
    private bool _lastBatchResent;

    internal int Offset => _offset;

    /// <summary>A2: a character is sent again at most once; a second miss for the same character ends the act with the error.</summary>
    internal static bool MayResend(bool alreadyResentThisCharacter, int batchLength) => !alreadyResentThisCharacter && batchLength > 0;

    /// <summary>Does one step. True when the whole text was typed and acknowledged.</summary>
    internal bool Step(CancellationToken ct)
    {
        if (_awaitingCaret is { } expectedCaret)
        {
            bool acknowledged;
            try
            {
                acknowledged = seams.Acknowledged(expectedCaret, _sentAt, ct);
            }
            catch (DesktopFault lost) when (lost.Reason == ReasonCodes.ReadbackMismatch)
            {
                // Windows may have dropped this character (the caret never moved). It is sent again at most once (A2). After
                // any re-send the act is confirmed only by a readback taken once the text has been unchanged for 500 ms (A1, A8).
                if (MayResend(_lastBatchResent, _lastBatch.Length) &&
                    seams.Resend(expectedCaret - _lastBatch.Length, _lastBatch, ct))
                {
                    _lastBatchResent = true;
                    progress.Resends++;
                    _sentAt = Stopwatch.GetTimestamp();
                    progress.TypingSentAt = _sentAt;
                    progress.TypingPolls = 0;
                    return false;
                }

                // A character the editor still does not take ends the act: status error, readback_mismatch, input sent (some
                // of the text is already in the editor). It is never reported as blocked, because input was sent.
                throw new DesktopFault(ActStatus.Error, ReasonCodes.ReadbackMismatch, _lastBatchResent
                    ? $"The editor did not take a character even when it was sent again; typing stopped after {progress.Sent} input events."
                    : $"{lost.Message} Typing stopped after {progress.Sent} input events.");
            }

            if (!acknowledged)
            {
                progress.TypingPolls++;
                return false;
            }

            _awaitingCaret = null;
            progress.TypingPolls = 0;
        }

        if (_offset == text.Length) return true;

        seams.BeforeBatch(_offset == 0, ct);
        seams.StopCheck(ct);
        var length = DesktopText.UnicodeBatchLength(text.AsSpan(_offset), batchLimit);
        var nextCaret = packetCaret is { } previousCaret ? checked(previousCaret + length) : (int?)null;
        var batch = text.Substring(_offset, length);
        if (batch.AsSpan().IndexOfAny('\r', '\n') >= 0) seams.CheckSubmission(batch);
        seams.StopCheck(ct);
        var caretBefore = packetCaret is null ? seams.CaretBefore() : null;
        _lastBatch = batch;
        _lastBatchResent = false;
        progress.Accept(seams.Send(batch), Channels.UnicodeTyping);
        _offset += length;
        _sentAt = Stopwatch.GetTimestamp();
        progress.TypingSentAt = _sentAt;
        progress.TypingPolls = 0;
        if (packetCaret is null)
        {
            seams.AwaitScalar(caretBefore, _offset == length);
            return _offset == text.Length;
        }

        packetCaret = nextCaret;
        _awaitingCaret = nextCaret;
        return false;
    }
}

/// <summary>
/// The worker-owned continuation of a type act that is still being typed. Accessed only on the worker thread. One typing
/// operation at a time: starting another abandons the old one, and only its owner can continue or cancel it.
/// </summary>
internal sealed class TypingSession
{
    private TypingPump? _pump;
    private InputProgress? _owner;

    /// <summary>An earlier typing operation that was never finished (its owner is gone) is discarded.</summary>
    internal void AbandonStale()
    {
        if (_owner is { } abandoned) Cancel(abandoned);
    }

    internal void Start(InputProgress progress, TypingPump pump, CancellationToken ct)
    {
        _pump = pump;
        _owner = progress;
        Continue(progress, ct);
    }

    internal void Continue(InputProgress progress, CancellationToken ct)
    {
        if (!ReferenceEquals(_owner, progress) || _pump is null)
            throw new InvalidOperationException("There is no matching worker-owned typing operation.");
        try
        {
            progress.TypingPending = !_pump.Step(ct);
            if (!progress.TypingPending) Cancel(progress);
        }
        catch
        {
            Cancel(progress);
            throw;
        }
    }

    internal void Cancel(InputProgress progress)
    {
        if (!ReferenceEquals(_owner, progress)) return;
        _pump = null;
        _owner = null;
        progress.TypingPending = false;
    }
}
