using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using OmniWin.Core;

namespace OmniWin.Ocr;

/// <summary>
/// Remembers OCR words under ids "o1", "o2", ... so <c>desktop_act</c> can click them (contract §7, §9.1).
/// Ids grow for the life of the session and are never reused. <see cref="TryResolve"/> is synchronous, takes no lock
/// and does not touch UI Automation, so it is safe to call from the desktop worker while captures run.
/// Entries stay well past the 10 s staleness limit WS1 applies, so an old id comes back as stale, not not_found.
/// </summary>
public sealed class OcrRefStore : IOcrRefResolver
{
    public const int RetentionSeconds = 120;
    public const int MaxEntries = 20_000;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<long, Entry> _entries = new();
    private long _next;

    public OcrRefStore() : this(TimeProvider.System)
    {
    }

    internal OcrRefStore(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public int Count => _entries.Count;

    /// <summary>Stores one word and returns its new id.</summary>
    public string Add(OcrWord word, long hwnd, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(word);
        var number = Interlocked.Increment(ref _next);
        var id = "o" + number.ToString(CultureInfo.InvariantCulture);
        _entries[number] = new Entry(word with { Id = id }, hwnd, capturedAt);
        return id;
    }

    /// <summary>Drops entries past the retention time, and the oldest ones when the store is too big.</summary>
    public void Prune()
    {
        var cutoff = _time.GetUtcNow() - TimeSpan.FromSeconds(RetentionSeconds);
        foreach (var (key, entry) in _entries)
        {
            if (entry.CapturedAt < cutoff) _entries.TryRemove(key, out _);
        }

        var excess = _entries.Count - MaxEntries;
        if (excess <= 0) return;
        foreach (var key in _entries.Keys.Order().Take(excess))
        {
            _entries.TryRemove(key, out _);
        }
    }

    public bool TryResolve(string ocrRef, [MaybeNullWhen(false)] out OcrWord word, out long hwnd, out DateTimeOffset capturedAt)
    {
        word = null;
        hwnd = 0;
        capturedAt = default;
        if (ocrRef is not { Length: > 1 } || ocrRef[0] != 'o' ||
            !long.TryParse(ocrRef.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            !_entries.TryGetValue(number, out var entry))
        {
            return false;
        }

        word = entry.Word;
        hwnd = entry.Hwnd;
        capturedAt = entry.CapturedAt;
        return true;
    }

    private sealed record Entry(OcrWord Word, long Hwnd, DateTimeOffset CapturedAt);
}
