using System.Diagnostics.CodeAnalysis;

namespace OmniWin.Core;

// ---------- system tools and OCR (contract §7). WS3 implements in OmniWin.SystemTools / OmniWin.Ocr. ----------

public interface ISystemTools
{
    Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string? filter, CancellationToken ct);
    Task<ActResult> LaunchAppAsync(string app, string? args, ExpectSpec? expect, CancellationToken ct); // app = Start-menu name or AUMID
    Task<ActResult> WindowManageAsync(WindowManageRequest req, CancellationToken ct);
    Task<AudioState> GetAudioAsync(CancellationToken ct);
    Task<ActResult> SetAudioAsync(int? volumePercent, bool? mute, CancellationToken ct);
    Task<ActResult> MediaAsync(string action, CancellationToken ct);       // play_pause | next | previous | stop
    Task<string> SystemInfoAsync(string kind, CancellationToken ct);       // network | battery | displays | time
    Task<string> ClipboardGetAsync(CancellationToken ct);
    Task<ActResult> ClipboardSetAsync(string text, CancellationToken ct);
}

public sealed record InstalledApp(string Name, string LaunchId /* AUMID or parsing name */, string? Publisher);
public sealed record AudioState(int VolumePercent, bool Muted, string DeviceName);

/// <summary>Action: focus | minimize | maximize | restore | move | resize | snap_left | snap_right | close.</summary>
public sealed record WindowManageRequest(string Window, string Action, int? X = null, int? Y = null, int? Width = null, int? Height = null);

public interface IOcrFallback
{
    Task<OcrResult> RecognizeAsync(string? window, ScreenRect? region, CancellationToken ct);
}

public sealed record ScreenRect(int X, int Y, int Width, int Height);   // physical pixels
public sealed record OcrWord(string Text, ScreenRect Box, string? Id);   // Id like "o14" so it can be clicked via desktop_act
public sealed record OcrResult(string Text, IReadOnlyList<OcrWord> Words, Timing Timing);

/// <summary>
/// WS3 implements in OmniWin.Ocr; WS1 consumes so desktop_act can click "oN" refs.
/// desktop_act on an "oN" ref supports only click / double_click / right_click. The pre-check verifies the point is
/// inside the same window and not covered by another top-level window. Without an explicit expectation the result
/// can be at best "unverified" (an OCR word has no element identity).
/// </summary>
public interface IOcrRefResolver
{
    /// <param name="hwnd">Window the capture was taken from.</param>
    /// <param name="capturedAt">Lets WS1 reject old captures (older than 10 s → stale).</param>
    bool TryResolve(string ocrRef, [MaybeNullWhen(false)] out OcrWord word, out long hwnd, out DateTimeOffset capturedAt);
}
