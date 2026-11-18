using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Ocr;

/// <summary>One recognised line of text with its words (each word has an <c>oN</c> id when it could be registered).</summary>
public sealed record OcrLineResult(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>The result of one <c>screen_ocr</c> call, with the line structure the plain <see cref="OcrResult"/> lacks.</summary>
public sealed record OcrCapture(OcrResult Result, IReadOnlyList<OcrLineResult> Lines, string Target);

/// <summary>An OCR capture, or the result that says why there is none (proposal Q5).</summary>
public sealed record OcrOutcome(OcrCapture? Value, ActResult? Failure, Timing Timing)
{
    public bool Ok => Failure is null;
}

/// <summary>Thrown by the plain §7 method when the richer outcome is a failure.</summary>
public sealed class OcrFault(ActResult result) : Exception(result.Detail ?? result.Reason ?? "screen_ocr failed")
{
    public ActResult Result { get; } = result;
}

public sealed record OcrOptions
{
    public int OperationTimeoutMs { get; init; } = 5000;

    public const string EngineName = "ocr";
}

/// <summary>
/// <c>screen_ocr</c> (contract §7, §8). Captures a window or a screen region, runs Windows OCR, and registers each word
/// under an <c>oN</c> id that <c>desktop_act</c> can click. Text only: no image or pixel data ever leaves this class.
/// <para>Forbidden windows (secure desktop, sign-in prompts, process blocklist, OmniWin itself) are denied when named and
/// painted over when they lie inside a region (proposal Q6). With neither window nor region, the foreground window is used.</para>
/// </summary>
public sealed class OcrService : IOcrFallback, IComponentStatus
{
    private readonly ISafetyGate _gate;
    private readonly IKillSwitch _kill;
    private readonly IMetrics _metrics;
    private readonly IDesktopEngine _desktop;
    private readonly OcrRefStore _refs;
    private readonly OcrOptions _options;
    private readonly IScreenCapture _capture;
    private readonly IOcrEngine _engine;

    public OcrService(ISafetyGate gate, IKillSwitch kill, IMetrics metrics, IDesktopEngine desktop, OcrRefStore refs, OcrOptions options)
        : this(gate, kill, metrics, desktop, refs, options, new GdiScreenCapture(), new WinRtOcrEngine())
    {
    }

    internal OcrService(
        ISafetyGate gate, IKillSwitch kill, IMetrics metrics, IDesktopEngine desktop,
        OcrRefStore refs, OcrOptions options, IScreenCapture capture, IOcrEngine engine)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _kill = kill ?? throw new ArgumentNullException(nameof(kill));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public string Component => "ocr";

    public string State => "ready";

    public string? Detail => "Windows.Media.Ocr; text only";

    public async Task<OcrResult> RecognizeAsync(string? window, ScreenRect? region, CancellationToken ct)
    {
        var outcome = await RecognizeOutcomeAsync(window, region, ct).ConfigureAwait(false);
        return outcome.Failure is { } failure ? throw new OcrFault(failure) : outcome.Value!.Result;
    }

    public async Task<OcrOutcome> RecognizeOutcomeAsync(string? window, ScreenRect? region, CancellationToken ct)
    {
        var timer = new Stopwatch();
        timer.Start();
        var phases = new Dictionary<string, double>(StringComparer.Ordinal);
        var phaseStart = Stopwatch.GetTimestamp();

        void End(string phase)
        {
            var now = Stopwatch.GetTimestamp();
            phases[phase] = phases.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(phaseStart, now).TotalMilliseconds;
            phaseStart = now;
        }

        Timing Now() => new(timer.Elapsed.TotalMilliseconds, new Dictionary<string, double>(phases, StringComparer.Ordinal));

        OcrOutcome Fail(ActResult result) => new(null, result with { Timing = Now() }, Now());

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _kill.Token);
        linked.CancelAfter(_options.OperationTimeoutMs);
        var token = linked.Token;

        try
        {
            if (region is { } r && (r.Width <= 0 || r.Height <= 0 || r.Width > 20000 || r.Height > 20000))
            {
                return Fail(ActResult.Failed(false, ReasonCodes.UnsupportedAction, "region needs a width and height between 1 and 20000 pixels.", Timing.Total(0)));
            }

            if (_kill.IsTripped)
            {
                return Fail(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)));
            }

            // resolve
            var windows = await _desktop.ListWindowsAsync(token).ConfigureAwait(false);
            WindowInfo? target = null;
            if (!string.IsNullOrWhiteSpace(window))
            {
                var (found, failure) = ResolveWindow(windows, window);
                if (found is null) return Fail(failure!);
                target = found;
            }
            else if (region is null)
            {
                target = windows.FirstOrDefault(w => w.IsForeground);
                if (target is null) return Fail(Blocked(ReasonCodes.NotFound, "No window is in the foreground; name a window or a region."));
            }

            End("resolve");

            // precheck
            if (target is not null)
            {
                if (IsOwn(target))
                {
                    return Fail(ActResult.Denied(ReasonCodes.PolicyForbidden, "OmniWin's own windows can't be read.", Timing.Total(0)));
                }

                switch (target.Inaccessible)
                {
                    case "elevated":
                        return Fail(Blocked(ReasonCodes.ElevatedTarget, "The window belongs to an elevated app OmniWin can't capture."));
                    case "secure_desktop":
                        return Fail(Blocked(ReasonCodes.SecureDesktop, "The window is on the secure desktop."));
                    case "blocked_by_policy":
                        return Fail(ActResult.Denied(ReasonCodes.PolicyForbidden,
                            $"{target.Process} can't be automated (secure desktop / sign-in, or process_blocklist_extra).", Timing.Total(0)));
                }

                if (target.IsMinimized)
                {
                    return Fail(Blocked(ReasonCodes.Offscreen, "The window is minimized; restore it first."));
                }
            }

            End("precheck");

            // gate (the gate also checks the process blocklist)
            var descriptor = new ActionDescriptor("screen_ocr", OcrOptions.EngineName, "read", null, target?.Process, target?.Title, null, null, null);
            var gateStart = Stopwatch.GetTimestamp();
            var gate = await _gate.AuthorizeAsync(descriptor, token).ConfigureAwait(false);
            phases["gate"] = Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
            if (!gate.Allowed)
            {
                return Fail(ActResult.Denied(gate.Reason ?? ReasonCodes.PolicyForbidden, gate.Detail ?? "Denied by safety policy", Timing.Total(0)));
            }

            if (_kill.IsTripped)
            {
                return Fail(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)));
            }

            // read: capture and recognise
            var capturedAt = DateTimeOffset.UtcNow;
            CapturedImage? image;
            string targetName;
            if (target is not null)
            {
                image = _capture.CaptureWindow(target.Hwnd);
                targetName = target.Id;
            }
            else
            {
                var area = region!;
                var masks = MaskRectangles(windows);
                image = _capture.CaptureRegion(area, masks);
                targetName = $"region {area.X},{area.Y} {area.Width}x{area.Height}";
            }

            if (image is null)
            {
                return Fail(Blocked(ReasonCodes.NotFound, "The window or region could not be captured."));
            }

            if (image.Width > _engine.MaxImageDimension || image.Height > _engine.MaxImageDimension)
            {
                return Fail(Blocked(ReasonCodes.UnsupportedAction,
                    $"The capture is {image.Width}x{image.Height}; Windows OCR accepts at most {_engine.MaxImageDimension} pixels on a side. Use a smaller region."));
            }

            IReadOnlyList<RecognizedLine> recognized;
            try
            {
                recognized = await _engine.RecognizeAsync(image.Bmp, token).ConfigureAwait(false);
            }
            catch (OcrUnavailableException ex)
            {
                return Fail(Blocked(ReasonCodes.NotFound, ex.Message));
            }

            End("read");

            // format: boxes in physical screen pixels, ids registered for desktop_act
            var lines = new List<OcrLineResult>();
            var all = new List<OcrWord>();
            foreach (var line in recognized)
            {
                var words = new List<OcrWord>();
                foreach (var word in line.Words)
                {
                    var box = new ScreenRect(
                        image.OriginX + (int)Math.Round(word.X),
                        image.OriginY + (int)Math.Round(word.Y),
                        Math.Max(1, (int)Math.Round(word.Width)),
                        Math.Max(1, (int)Math.Round(word.Height)));

                    if (target is not null && region is { } within && !Contains(within, box)) continue;

                    long hwnd = target?.Hwnd ?? _capture.TopLevelWindowAt(box.X + (box.Width / 2), box.Y + (box.Height / 2));
                    var draft = new OcrWord(word.Text, box, null);
                    OcrWord stored = draft;
                    if (hwnd != 0)
                    {
                        var id = _refs.Add(draft, hwnd, capturedAt);
                        stored = draft with { Id = id };
                    }

                    words.Add(stored);
                    all.Add(stored);
                }

                if (words.Count > 0) lines.Add(new OcrLineResult(string.Join(' ', words.Select(w => w.Text)), words));
            }

            _refs.Prune();
            var text = string.Join('\n', lines.Select(l => l.Text));
            End("format");

            var timing = Now();
            RecordMetric(timing);
            return new OcrOutcome(new OcrCapture(new OcrResult(text, all, timing), lines, targetName), null, timing);
        }
        catch (OperationCanceledException)
        {
            return Fail(_kill.IsTripped || _kill.Token.IsCancellationRequested
                ? ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0))
                : ActResult.Failed(false, ct.IsCancellationRequested ? ReasonCodes.EngineTimeout : ReasonCodes.EngineTimeout,
                    ct.IsCancellationRequested ? "The call was cancelled." : "OCR did not finish within its time limit.", Timing.Total(0)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Fail(ActResult.Failed(false, ReasonCodes.NotFound, ResultFormatter.OneLine(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message), Timing.Total(0)));
        }
    }

    private void RecordMetric(Timing timing)
    {
        // Gate and confirm time are excluded (contract §15).
        var gate = timing.PhasesMs.TryGetValue("gate", out var g) ? g : 0;
        _metrics.Record("screen_ocr", Math.Max(0, timing.TotalMs - gate));
    }

    private static ActResult Blocked(string reason, string detail) => ActResult.Blocked(reason, detail, Timing.Total(0));

    internal static bool IsOwn(WindowInfo w) =>
        w.Pid == Environment.ProcessId || w.Process.Equals("OmniWin.Server.exe", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(ScreenRect outer, ScreenRect box)
    {
        var cx = box.X + (box.Width / 2);
        var cy = box.Y + (box.Height / 2);
        return cx >= outer.X && cx < outer.X + outer.Width && cy >= outer.Y && cy < outer.Y + outer.Height;
    }

    private List<ScreenRect> MaskRectangles(IReadOnlyList<WindowInfo> windows)
    {
        var masks = new List<ScreenRect>();
        foreach (var w in windows)
        {
            if (!IsOwn(w) && w.Inaccessible is not ("secure_desktop" or "blocked_by_policy")) continue;
            if (_capture.WindowRect(w.Hwnd) is { } rect) masks.Add(rect);
        }

        return masks;
    }

    internal static (WindowInfo? Window, ActResult? Failure) ResolveWindow(IReadOnlyList<WindowInfo> windows, string selector)
    {
        var wanted = selector.Trim();
        var matches = windows.Where(w => w.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = windows.Where(w => w.Process.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                                         w.Process.Equals(wanted + ".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 0)
        {
            matches = windows.Where(w => w.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 1) return (matches[0], null);
        return matches.Count == 0
            ? (null, Blocked(ReasonCodes.NotFound, $"No window matches \"{ResultFormatter.OneLine(wanted)}\"."))
            : (null, Blocked(ReasonCodes.NotFound,
                $"\"{ResultFormatter.OneLine(wanted)}\" matches {matches.Count} windows ({string.Join(", ", matches.Take(6).Select(w => w.Id))}); use a wN id."));
    }
}
