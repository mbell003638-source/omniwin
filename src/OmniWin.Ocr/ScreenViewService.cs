using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Ocr;

public sealed record ScreenViewOptions
{
    public int OperationTimeoutMs { get; init; } = 8000;

    /// <summary>The most windows whose password fields are looked up for a whole-screen or region view.</summary>
    public int MaxWindowsToMask { get; init; } = 60;

    public const string EngineName = "ocr";
    public const string ScreenSelector = "screen";
}

/// <summary>
/// <c>screen_view</c>: a look-only picture of one window, a region, or the whole screen (owner decision 2026-11-08).
/// It reads pixels and sends no input, takes no coordinate to act on, and brings no window to the front.
/// <list type="bullet">
/// <item>Default is the foreground window. The whole screen needs <c>window: "screen"</c>.</item>
/// <item>Before the image leaves, password fields (found through UI Automation) and the owner's blocked windows (secure
/// desktop, sign-in prompts, OmniWin itself) are blacked out on the full-resolution pixels. If masking fails there is no
/// image at all.</item>
/// <item>Nothing is logged or written to disk: only the returned bytes carry the image.</item>
/// </list>
/// </summary>
public sealed class ScreenViewService : IScreenViewer
{
    private readonly ISafetyGate _gate;
    private readonly IKillSwitch _kill;
    private readonly IMetrics _metrics;
    private readonly IDesktopEngine _desktop;
    private readonly ScreenViewOptions _options;
    private readonly IScreenCapture _capture;
    private readonly IPasswordLocator _passwords;
    private readonly ILabelSource? _labels;

    public ScreenViewService(ISafetyGate gate, IKillSwitch kill, IMetrics metrics, IDesktopEngine desktop, ScreenViewOptions options,
        ILabelSource? labels = null)
        : this(gate, kill, metrics, desktop, options, new GdiScreenCapture(), new UiaPasswordLocator(), labels)
    {
    }

    internal ScreenViewService(
        ISafetyGate gate, IKillSwitch kill, IMetrics metrics, IDesktopEngine desktop,
        ScreenViewOptions options, IScreenCapture capture, IPasswordLocator passwords, ILabelSource? labels = null)
    {
        _labels = labels;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _kill = kill ?? throw new ArgumentNullException(nameof(kill));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
    }

    public Task<ViewOutcome> ViewAsync(string? window, ScreenRect? region, int? maxSide, CancellationToken ct) =>
        ViewAsync(window, region, maxSide, false, ct);

    public async Task<ViewOutcome> ViewAsync(string? window, ScreenRect? region, int? maxSide, bool labels, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>(StringComparer.Ordinal);
        var phaseStart = Stopwatch.GetTimestamp();

        void End(string phase)
        {
            var now = Stopwatch.GetTimestamp();
            phases[phase] = phases.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(phaseStart, now).TotalMilliseconds;
            phaseStart = now;
        }

        Timing Now() => new(timer.Elapsed.TotalMilliseconds, new Dictionary<string, double>(phases, StringComparer.Ordinal));

        ViewOutcome Fail(ActResult result) => new(null, result with { Timing = Now() }, string.Empty, string.Empty, DateTimeOffset.UtcNow, Now());

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

            var side = ViewLimits.Clamp(maxSide);
            if (labels && _labels is null)
            {
                return Fail(ActResult.Failed(false, ReasonCodes.UnsupportedAction, "labels are not available in this build.", Timing.Total(0)));
            }

            // resolve: foreground by default, the whole screen only when asked for by name
            var windows = await _desktop.ListWindowsAsync(token).ConfigureAwait(false);
            var wholeScreen = string.Equals(window?.Trim(), ScreenViewOptions.ScreenSelector, StringComparison.OrdinalIgnoreCase);
            WindowInfo? target = null;
            if (!wholeScreen)
            {
                if (!string.IsNullOrWhiteSpace(window))
                {
                    var (found, failure) = OcrService.ResolveWindow(windows, window);
                    if (found is null) return Fail(failure!);
                    target = found;
                }
                else if (region is null)
                {
                    target = windows.FirstOrDefault(w => w.IsForeground);
                    if (target is null) return Fail(Blocked(ReasonCodes.NotFound, "No window is in the foreground; name a window, a region, or window \"screen\"."));
                }
            }

            End("resolve");

            // precheck
            if (target is not null)
            {
                if (OcrService.IsOwn(target))
                {
                    return Fail(ActResult.Denied(ReasonCodes.PolicyForbidden, "OmniWin's own windows can't be viewed.", Timing.Total(0)));
                }

                switch (target.Inaccessible)
                {
                    case "elevated":
                        return Fail(Blocked(ReasonCodes.ElevatedTarget, "The window belongs to an elevated app OmniWin can't capture."));
                    case "secure_desktop":
                        return Fail(Blocked(ReasonCodes.SecureDesktop, "The window is on the secure desktop."));
                    case "blocked_by_policy":
                        return Fail(ActResult.Denied(ReasonCodes.PolicyForbidden,
                            $"{target.Process} can't be viewed (secure desktop / sign-in, or process_blocklist_extra).", Timing.Total(0)));
                }

                if (target.IsMinimized)
                {
                    return Fail(Blocked(ReasonCodes.Offscreen, "The window is minimized; restore it first."));
                }
            }

            End("precheck");

            // gate (it also checks the process blocklist)
            var descriptor = new ActionDescriptor("screen_view", ScreenViewOptions.EngineName, "read", null, target?.Process, target?.Title, null, null, null);
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

            // capture + mask list. Every sensitive rectangle is read BEFORE and AFTER the capture. If anything moved in between
            // (a window moved or scrolled, a window opened over the area) the capture is repeated once; if the two reads still
            // disagree, nothing is returned, so a black box can never sit in the wrong place.
            var capturedAt = DateTimeOffset.UtcNow;
            CapturedImage? image = null;
            string targetName;
            string title;
            var maskScreen = new List<ScreenRect>();
            var area = region ?? _capture.VirtualScreen;
            if (target is not null)
            {
                targetName = target.Id;
                title = target.Title;
            }
            else
            {
                targetName = wholeScreen && region is null ? "screen" : $"region {area.X},{area.Y} {area.Width}x{area.Height}";
                title = wholeScreen && region is null ? "screen" : "region";
            }

            try
            {
                var stable = false;
                for (var attempt = 0; attempt < 2 && !stable; attempt++)
                {
                    capturedAt = DateTimeOffset.UtcNow;
                    var before = await ReadMasksAsync(target, windows, area, token).ConfigureAwait(false);
                    image = target is not null ? _capture.CaptureWindow(target.Hwnd) : _capture.CaptureRegion(area, before.Masks);
                    if (image is null) break;

                    var after = await ReadMasksAsync(target, windows, area, token).ConfigureAwait(false);
                    stable = before.Masks.SequenceEqual(after.Masks) && before.Geometry.SequenceEqual(after.Geometry);
                    maskScreen = before.Masks;
                }

                if (image is not null && !stable)
                {
                    return Fail(ActResult.Failed(false, ReasonCodes.NotFound,
                        "The window moved or changed while it was captured, so the sensitive areas could not be placed reliably; no image is returned. Try again.", Timing.Total(0)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Masking is not optional: if the sensitive areas can't be found, nothing is returned.
                return Fail(ActResult.Failed(false, ReasonCodes.NotFound, "Sensitive areas could not be masked, so no image is returned.", Timing.Total(0)));
            }

            if (image is null)
            {
                return Fail(Blocked(ReasonCodes.NotFound, "The window or region could not be captured."));
            }

            End("capture");

            // render: mask rectangles move into image pixels; the mask is applied to the full-resolution pixels first
            var pixelMasks = maskScreen
                .Select(m => new PixelRect(m.X - image.OriginX, m.Y - image.OriginY, m.Width, m.Height))
                .ToList();
            PixelRect? crop = target is not null && region is { } within
                ? new PixelRect(within.X - image.OriginX, within.Y - image.OriginY, within.Width, within.Height)
                : null;

            // labels=true: the refs of this window's last desktop_observe, drawn on the picture (only for a window view)
            List<LabelBox>? labelBoxes = null;
            long labelGeneration = 0;
            if (labels)
            {
                if (target is null)
                {
                    return Fail(ActResult.Failed(false, ReasonCodes.UnsupportedAction,
                        "labels needs one window: pass window=wN (or leave it out for the foreground window) and no screen or region.", Timing.Total(0)));
                }

                var set = await _labels!.LabelsAsync(target.Id, token).ConfigureAwait(false);
                if (set is null || set.Labels.Count == 0)
                {
                    return Fail(Blocked(ReasonCodes.NotFound,
                        $"{target.Id} has no observed refs yet. Run desktop_observe on it first, then ask for the view with labels."));
                }

                labelGeneration = set.Generation;
                labelBoxes = set.Labels
                    .Select(l => new LabelBox(l.Ref, new PixelRect(l.Bounds.X - image.OriginX, l.Bounds.Y - image.OriginY, l.Bounds.Width, l.Bounds.Height)))
                    .ToList();
            }

            ViewImage rendered;
            try
            {
                rendered = await ViewImaging.RenderAsync(image.Bmp, pixelMasks, crop, side, token, labelBoxes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(ActResult.Failed(false, ReasonCodes.NotFound, "The image could not be masked and encoded, so no image is returned.", Timing.Total(0)));
            }

            End("render");

            if (_kill.IsTripped)
            {
                return Fail(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)));
            }

            var timing = Now();
            var gateMs = timing.PhasesMs.TryGetValue("gate", out var g) ? g : 0;
            _metrics.Record("screen_view", Math.Max(0, timing.TotalMs - gateMs));
            return new ViewOutcome(rendered, null, targetName, title, capturedAt, timing, labelBoxes?.Count ?? 0, labelGeneration);
        }
        catch (OperationCanceledException)
        {
            return Fail(_kill.IsTripped || _kill.Token.IsCancellationRequested
                ? ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0))
                : ActResult.Failed(false, ReasonCodes.EngineTimeout,
                    ct.IsCancellationRequested ? "The call was cancelled." : "The view did not finish within its time limit.", Timing.Total(0)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Fail(ActResult.Failed(false, ReasonCodes.NotFound, ResultFormatter.OneLine(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message), Timing.Total(0)));
        }
    }

    private static ActResult Blocked(string reason, string detail) => ActResult.Blocked(reason, detail, Timing.Total(0));

    private sealed record MaskRead(List<ScreenRect> Masks, List<ScreenRect> Geometry);

    /// <summary>
    /// The rectangles to black out, in screen coordinates, plus the geometry they depend on (the window's own rectangle, or the
    /// rectangles of every window over the area). Windows past <see cref="ScreenViewOptions.MaxWindowsToMask"/> are not
    /// searched for password fields, so they are blacked out WHOLE: no window is ever left unmasked and unchecked.
    /// </summary>
    private async Task<MaskRead> ReadMasksAsync(WindowInfo? target, IReadOnlyList<WindowInfo> windows, ScreenRect area, CancellationToken token)
    {
        var masks = new List<ScreenRect>();
        var geometry = new List<ScreenRect>();
        if (target is not null)
        {
            if (_capture.WindowRect(target.Hwnd) is { } own) geometry.Add(own);
            masks.AddRange(await _passwords.FindPasswordRectsAsync(target.Hwnd, token).ConfigureAwait(false));
            return new MaskRead(masks, geometry);
        }

        // Windows that must never appear in an image: OmniWin's own, the secure desktop, sign-in and policy-blocked apps.
        foreach (var w in windows)
        {
            if (!OcrService.IsOwn(w) && w.Inaccessible is not ("secure_desktop" or "blocked_by_policy")) continue;
            if (_capture.WindowRect(w.Hwnd) is { } forbidden)
            {
                masks.Add(forbidden);
                geometry.Add(forbidden);
            }
        }

        var searched = 0;
        foreach (var w in windows)
        {
            if (w.IsMinimized || OcrService.IsOwn(w) || w.Inaccessible is "secure_desktop" or "blocked_by_policy") continue;
            if (_capture.WindowRect(w.Hwnd) is not { } rect) continue;
            if (rect.X >= area.X + area.Width || rect.X + rect.Width <= area.X ||
                rect.Y >= area.Y + area.Height || rect.Y + rect.Height <= area.Y)
            {
                continue;
            }

            geometry.Add(rect);
            if (w.Inaccessible is not null || ++searched > _options.MaxWindowsToMask)
            {
                // Can't be inspected (or too many to inspect): it might hold a password field, so black the whole window out.
                masks.Add(rect);
                continue;
            }

            masks.AddRange(await _passwords.FindPasswordRectsAsync(w.Hwnd, token).ConfigureAwait(false));
        }

        return new MaskRead(masks, geometry);
    }
}
