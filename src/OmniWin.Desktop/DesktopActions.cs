using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using OmniWin.Core;
using OmniWin.Safety;
using static Interop.UIAutomationClient.UIA_PatternIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace OmniWin.Desktop;

internal sealed record DesktopCommand(string Tool, string Action, string? Ref = null, long? Generation = null,
    string? Window = null, string? Value = null, KeySequence? Keys = null, int Amount = 0, ExpectSpec? Expect = null)
{
    internal bool UsesSharedInput => Tool != "desktop_act" || Action is not
        ("invoke" or "toggle" or "select" or "expand" or "collapse" or "set_value" or "clear" or "scroll_into_view");
}

internal sealed record PreparedInput(string Window, DesktopSnapshot? Baseline, string? Identity, ActionDescriptor Descriptor)
{
    internal Point? OcrPoint { get; init; }
    internal Point? PhysicalClickPoint { get; init; }
    internal long Hwnd { get; init; }
}

internal sealed class InputProgress
{
    internal int Sent { get; private set; }
    internal string? Channel { get; private set; }
    internal double DispatchMs { get; private set; }
    internal string? PreviousToggle { get; set; }
    internal string? ExpectedTypeValue { get; set; }
    /// <summary>How many characters of this act were sent a second time because the editor never took the first copy.</summary>
    internal int Resends { get; set; }
    internal IReadOnlySet<string>? OpenWindows { get; set; }
    internal bool TypingPending { get; set; }
    internal int TypingPolls { get; set; }
    internal long TypingSentAt { get; set; }

    internal void Accept(InputReceipt receipt, string channel)
    {
        Sent += receipt.Sent;
        DispatchMs += receipt.DispatchMs;
        if (receipt.Sent != 0) Channel = channel;
        if (!receipt.Complete)
        {
            throw new DesktopFault(ActStatus.Error, Sent == 0 ? ReasonCodes.UnsupportedAction : ReasonCodes.InputInterrupted,
                $"Windows accepted {receipt.Sent} of {receipt.Requested} events in the last input batch.");
        }
    }

    internal void Pattern(Action send, string channel)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            send();
        }
        catch (Exception)
        {
            // The request has already crossed the provider boundary. Even a disappearing control can
            // mean that Invoke succeeded and closed its window before the provider returned.
            Sent++;
            Channel = channel;
            throw;
        }
        finally
        {
            DispatchMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Sent++;
        Channel = channel;
    }
}

internal sealed partial class UiaDesktop : IDesktopBackend
{
    // This continuation and its captured COM elements are accessed only on the MTA worker.
    private readonly TypingSession _typing = new();

    public PreparedInput Prepare(DesktopCommand command, PreparedInput? previous, CancellationToken ct,
        InputProgress? progress = null, Func<string?>? stopReason = null)
    {
        if (command.Expect is { } expectation) ValidateExpectation(expectation.Condition);
        var target = Check(command, previous, ct);
        if (command.Action == "type" && target.Live is { } typeTarget &&
            DesktopText.UnsupportedPacketText(NativeDesktop.NativeClassName(typeTarget.NativeHandle), command.Value!))
            throw Block(ReasonCodes.UnsupportedAction,
                "This native editor cannot reliably receive supplementary Unicode or newline packets. Use explicit set_value, or send Enter separately between supported text runs.");
        if (command.Action == "type") CheckTypedCommandSubmission(target, command.Value);
        if (previous is not null && command.Keys is not null && target.Live is null)
        {
            CheckInputBoundary(target.Window, stopReason ?? (() => null), ct);
            Activate(target.Window, progress!, stopReason ?? (() => null), ct);

            target = Check(command, previous, ct);
            if (target.Live is null)
            {
                throw Block(ReasonCodes.FocusFailed, "No keyboard focus could be verified in the requested window.");
            }
        }

        var baseline = target.Window.Latest ?? Snapshot(target.Window, false, ct);
        return new PreparedInput(target.Window.Id,
            command.Generation is { } generation && command.Ref?.StartsWith('e') == true ? target.Window.Snapshot(generation) : baseline,
            target.Live?.Sample.Identity, Descriptor(command, target))
        {
            OcrPoint = command.Ref?.StartsWith('o') == true ? target.Point : null,
            PhysicalClickPoint = command.Ref?.StartsWith('e') == true && IsClick(command.Action) &&
                target.Channel == Channels.PhysicalClick ? target.Point : null,
            Hwnd = target.Window.Sample.Hwnd,
        };
    }

    public void Dispatch(DesktopCommand command, PreparedInput prepared, InputProgress progress,
        Func<string?> stopReason, CancellationToken ct)
    {
        _typing.AbandonStale();
        var target = Check(command, prepared, ct);
        if (command.Action == "type" && target.Live is { } typeTarget &&
            DesktopText.UnsupportedPacketText(NativeDesktop.NativeClassName(typeTarget.NativeHandle), command.Value!))
            throw Block(ReasonCodes.UnsupportedAction, "This native editor cannot reliably receive this Unicode packet text.");
        if (command.Action == "type") CheckTypedCommandSubmission(target, command.Value);
        var descriptor = Descriptor(command, target);
        if (descriptor.ContainsDeleteCommand)
        {
            throw new DesktopFault(ActStatus.Denied, ReasonCodes.PolicyForbidden, "The current command contains a deletion operation.");
        }
        if (prepared.Identity != target.Live?.Sample.Identity ||
            prepared.OcrPoint is { } ocrPoint && ocrPoint != target.Point ||
            prepared.Descriptor.Process != descriptor.Process || prepared.Descriptor.WindowTitle != descriptor.WindowTitle ||
            !SamePolicyTarget(prepared.Descriptor.Target, descriptor.Target) ||
            !SamePolicyTarget(prepared.Descriptor.DropTarget, descriptor.DropTarget))
        {
            throw Block(command.Keys is not null ? ReasonCodes.FocusChanged : ReasonCodes.WindowChanged,
                "The authorized target changed before input.");
        }

        progress.PreviousToggle = target.Live?.Sample.Toggle;
        progress.OpenWindows = command.Expect is null ? null : _index.Windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        if (command.Keys is { } keys)
        {
            var live = target.Live!;
            foreach (var chord in keys.Chords)
            {
                CheckCommandSubmission(target, chord);
                CheckInputBoundary(target.Window, stopReason, ct);
                RequireKeyboardFocus(target.Window, live);
                CheckStopBoundary(stopReason, ct);
                progress.Accept(NativeInput.Keys(chord), Channels.Keys);
            }

            return;
        }

        if (target.Channel is Channels.PhysicalHover or Channels.PhysicalDrag)
        {
            // Repeat the native test immediately before the input, as for a click, on both ends of a drag.
            var from = target.Point!.Value;
            var source = target.Live!;
            CheckInputBoundary(target.Window, stopReason, ct, pointer: true);
            CheckPointerWindow(target.Window, source.NativeHandle, from);
            if (HitTest(source.Element, from) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The control at the input point changed before dispatch.");
            }

            if (target.Channel == Channels.PhysicalHover)
            {
                CheckStopBoundary(stopReason, ct);
                progress.Accept(NativeInput.Hover(from), Channels.PhysicalHover);
                return;
            }

            var to = target.DropPoint!.Value;
            var drop = target.DropLive!;
            CheckPointerWindow(target.Window, drop.NativeHandle, to);
            if (HitTest(drop.Element, to) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The drop target changed before dispatch.");
            }

            CheckStopBoundary(stopReason, ct);
            progress.Accept(NativeInput.Drag(from, to), Channels.PhysicalDrag);
            return;
        }

        if (target.Channel is Channels.PhysicalClick || command.Tool == "desktop_scroll")
        {
            // Repeat the native test immediately before the batch, including after the UIA hit test.
            var point = target.Point!.Value;
            CheckInputBoundary(target.Window, stopReason, ct, pointer: true);
            if (command.Tool == "desktop_scroll") CheckScrollRouting(target.Window, target.Live);
            if (prepared.OcrPoint is null && target.Live is { } live && HitTest(live.Element, point) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The control at the input point changed before dispatch.");
            }

            CheckPointerWindow(target.Window, target.Live?.NativeHandle ?? 0, point);
            CheckStopBoundary(stopReason, ct);
            var receipt = command.Tool == "desktop_scroll"
                ? NativeInput.Scroll(point, command.Action, command.Amount)
                : NativeInput.Click(point, command.Action == "right_click", command.Action == "double_click");
            progress.Accept(receipt, target.Channel!);
            return;
        }

        var element = target.Live!.Element;
        if (target.Channel == Channels.UnicodeTyping)
        {
            FocusForTyping(target.Window, target.Live, progress, stopReason, ct);
            // FocusForTyping checks the boundary before activation. Capture only reads this target;
            // the loop verifies focus and the boundary before every actual input batch.
            using (DesktopDiagnostics.Measure("CaptureTypingExpectation"))
            {
                progress.ExpectedTypeValue = CaptureTypingExpectation(target.Live.Element, command.Value!);
            }
            var text = command.Value!;
            var batchLimit = DesktopText.UnicodeBatchLimit(target.Live.Sample.ClassName);
            // Ordinary editor optimization does not change terminal focus/submission behavior.
            var nativeEditor = CommandTarget(descriptor.Target, target.Window) ? null : VerifyNativeEditor(target.Window, target.Live);
            int? packetCaret = null;
            if (nativeEditor?.ClassName.Equals("RichEditD2DPT", StringComparison.OrdinalIgnoreCase) == true)
            {
                // This provider can reuse the last VK_PACKET character while older input is queued.
                // A successful focus query alone is not an acknowledgement of inserted text.
                if (text.AsSpan().IndexOfAny('\r', '\n') < 0 &&
                    NativeDesktop.TryGetPacketEditorSelection(nativeEditor.Handle, out var caret, out _))
                    packetCaret = caret;
                else
                    nativeEditor = null; // Preserve the original full checks when native progress is unavailable.
            }
            void FullFocusCheck() => RequireKeyboardFocus(target.Window, target.Live);
            var seams = new TypingSeams
            {
                BeforeBatch = (first, token) =>
                {
                    CheckInputBoundary(target.Window, stopReason, token);
                    using (DesktopDiagnostics.Measure(first ? "TypingFocus.Initial" : "TypingFocus.Later"))
                        DesktopTypingFocus.CheckBatch(first, nativeEditor, FullFocusCheck, ReadNativeTypingFocus);
                },
                StopCheck = token => CheckStopBoundary(stopReason, token),
                CheckSubmission = batch => CheckTypedCommandSubmission(target, batch),
                Send = batch => NativeInput.Type(batch),
                CaretBefore = NativeDesktop.NativeCaret,
                AwaitScalar = (before, first) => NativeDesktop.AwaitTypedScalar(before, first),
                Acknowledged = (expectedCaret, sentAt, token) =>
                    PacketEditorAcknowledged(target.Window, target.Live, nativeEditor!, expectedCaret, sentAt, stopReason, token),
                Resend = (caretBefore, scalar, token) =>
                    ResendLostScalar(target, nativeEditor!, caretBefore, scalar, progress, stopReason, token),
            };
            _typing.Start(progress, new TypingPump(text, batchLimit, packetCaret, progress, seams), ct);
            return;
        }

        if (command.Action == "focus")
        {
            CheckInputBoundary(target.Window, stopReason, ct);
            // The provider owns cross-thread activation (notably WinUI and Chromium). Focus it
            // before the native foreground check, rather than rejecting a supported UIA focus
            // operation solely because SetForegroundWindow refused the client process.
            progress.Pattern(element.SetFocus, Channels.Api);
            CheckInputBoundary(target.Window, stopReason, ct);
            Activate(target.Window, progress, stopReason, ct);
            return;
        }

        var send = PatternOperation(command, element, target.Channel!);
        CheckInputBoundary(target.Window, stopReason, ct, sharedInput: command.UsesSharedInput);
        progress.Pattern(send, target.Channel!);
    }

    public void ContinueTyping(InputProgress progress, CancellationToken ct) => _typing.Continue(progress, ct);

    public void CancelTyping(InputProgress progress) => _typing.Cancel(progress);

    /// <summary>What the last <see cref="BringToForeground"/> call sent: any window input, and whether the F24 key was tapped.</summary>
    public (bool InputSent, bool KeyTapped) LastForegroundAttempt { get; private set; }

    public bool BringToForeground(long hwnd, Func<string?> stopReason, CancellationToken ct)
    {
        LastForegroundAttempt = (false, false);
        RefreshWindows(ct);
        if (_index.ByHandle(hwnd) is not { } window || !_onInputDesktop || window.Sample.Inaccessible is not null ||
            window.BlockedBy is not null || !NativeDesktop.Enabled(hwnd))
        {
            return false;
        }

        void Boundary()
        {
            CheckStopBoundary(stopReason, ct);
            if (!NativeDesktop.OnInputDesktop()) throw Block(ReasonCodes.SecureDesktop, "The input desktop changed.");
            // This service may restore minimized windows. The normal input boundary rejects them.
            if (NativeDesktop.ProcessId(hwnd) != window.Sample.Pid || !NativeDesktop.IsTopLevelWindow(hwnd) ||
                !NativeDesktop.Enabled(hwnd))
            {
                throw Block(ReasonCodes.WindowChanged, "The target window changed before activation.");
            }
            if (NativeDesktop.ModifiersHeld()) throw Block(ReasonCodes.UserActive, "A modifier is active.");
        }

        var windowInput = false;
        var keyTapped = false;
        bool activated;
        try
        {
            activated = NativeDesktop.TryForeground(hwnd, out windowInput, out keyTapped, Boundary);
        }
        finally
        {
            // Kept, not dropped, also when the attempt ends by throwing (a boundary can stop it after the F24 tap): the caller
            // (window_manage focus) reports whether anything was sent and through which channel.
            LastForegroundAttempt = (windowInput, keyTapped);
        }

        return activated && NativeDesktop.ProcessId(hwnd) == window.Sample.Pid;
    }

    private InputTarget Check(DesktopCommand command, PreparedInput? previous, CancellationToken ct)
    {
        RefreshWindows(ct);
        if (!_onInputDesktop)
        {
            throw Block(ReasonCodes.SecureDesktop, "OmniWin is not on the active input desktop.");
        }

        if (command.Ref?.StartsWith('o') == true)
        {
            return CheckOcr(command, previous);
        }

        LiveElement? live = null;
        DesktopWindow window;
        if (command.Ref is { } reference)
        {
            window = _index.FindRef(reference, out var identity) ??
                throw Stale("The element reference is no longer available.");
            CheckWindow(window, previous);
            if (command.Window is not null && _index.ResolveWindow(command.Window)?.Id != window.Id)
            {
                throw Block(ReasonCodes.WindowChanged, "The element does not belong to the requested window.");
            }

            live = ResolveElement(window, identity!, command.Generation, ct);
            if (!live.Sample.Enabled)
            {
                throw Block(ReasonCodes.Disabled, "The target control is disabled.");
            }

            if (command.Action != "scroll_into_view" && !NativeDesktop.OnMonitor(live.Sample.VisibleBounds))
            {
                throw Block(ReasonCodes.Offscreen, "The target control is outside its visible viewport.");
            }
        }
        else
        {
            window = previous is null ? RequireWindow(command.Window) :
                _index.ById(previous.Window) ?? throw Block(ReasonCodes.WindowChanged, "The target window closed.");
            CheckWindow(window, previous);
        }

        if (command.Keys is not null)
        {
            if (NativeDesktop.Foreground != window.Sample.Hwnd)
            {
                if (previous?.Identity is not null || command.Window is null)
                {
                    throw Block(ReasonCodes.FocusChanged, "The target window lost the foreground.");
                }

                return new InputTarget(window, null, Channels.Keys);
            }

            live = FocusedIn(window, ct) ?? throw Block(ReasonCodes.FocusFailed, "No keyboard focus could be verified in the target window.");
            if (!live.Sample.Enabled)
            {
                throw Block(ReasonCodes.Disabled, "The focused control is disabled.");
            }

            return new InputTarget(window, live, Channels.Keys);
        }

        if (command.Tool == "desktop_scroll")
        {
            CheckScrollRouting(window, live);
            var rect = live?.Sample.VisibleBounds ?? NativeDesktop.Intersect(window.Sample.Bounds, NativeDesktop.VirtualScreen);
            var point = PointFor(live, rect);
            CheckPointerWindow(window, live?.NativeHandle ?? 0, point);
            if (live is not null && HitTest(live.Element, point) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The scroll point does not hit the requested control.");
            }

            return new InputTarget(window, live, Channels.Api, point);
        }

        if (command.Action is "hover" or "drag")
        {
            // Same pointer checks as a click, on the source and (for a drag) on the drop target. No Click support is required.
            var source = live!;
            var from = PointFor(source, source.Sample.VisibleBounds);
            CheckPointerWindow(window, source.NativeHandle, from);
            if (HitTest(source.Element, from) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The point does not hit the requested control.");
            }

            if (command.Action == "hover") return new InputTarget(window, source, Channels.PhysicalHover, from);

            var dropWindow = _index.FindRef(command.Value!, out var dropIdentity) ??
                throw Stale("The drop target reference is no longer available.");
            if (dropWindow.Id != window.Id)
            {
                throw Block(ReasonCodes.WindowChanged, "The drop target belongs to another window.");
            }

            var drop = ResolveElement(window, dropIdentity!, command.Generation, ct);
            if (!drop.Sample.Enabled) throw Block(ReasonCodes.Disabled, "The drop target is disabled.");
            if (!NativeDesktop.OnMonitor(drop.Sample.VisibleBounds))
            {
                throw Block(ReasonCodes.Offscreen, "The drop target is outside its visible viewport.");
            }

            var to = PointFor(drop, drop.Sample.VisibleBounds);
            CheckPointerWindow(window, drop.NativeHandle, to);
            if (HitTest(drop.Element, to) != Hit.Exact)
            {
                throw Block(ReasonCodes.OccludedSameWindow, "The point does not hit the drop target.");
            }

            return new InputTarget(window, source, Channels.PhysicalDrag, from, null, drop, to);
        }

        if (IsClick(command.Action))
        {
            if (!live!.Sample.Actions.HasFlag(ElementActions.Click))
            {
                throw Block(ReasonCodes.UnsupportedAction, "The observed control does not support clicks.");
            }

            if (previous?.PhysicalClickPoint is { } preparedPoint &&
                NativeDesktop.Contains(live!.Sample.VisibleBounds, preparedPoint) &&
                CanReuseClickPoint(window, live, preparedPoint))
            {
                return new InputTarget(window, live, Channels.PhysicalClick, preparedPoint);
            }

            var point = PointFor(live, live!.Sample.VisibleBounds);
            CheckPointerWindow(window, live.NativeHandle, point);
            var hit = HitTest(live.Element, point);
            if (hit == Hit.Exact)
            {
                return new InputTarget(window, live, Channels.PhysicalClick, point);
            }

            if (hit == Hit.Ancestor && live.NativeHandle == 0 && command.Action == "click")
            {
                if (live.Sample.Actions.HasFlag(ElementActions.Invoke)) return new InputTarget(window, live, Channels.Invoke);
                if (live.Sample.Actions.HasFlag(ElementActions.Toggle)) return new InputTarget(window, live, Channels.Toggle);
            }

            throw Block(ReasonCodes.OccludedSameWindow, "The point does not hit the requested control.");
        }

        return new InputTarget(window, live, PatternChannel(command.Action, live!.Sample.Actions));
    }

    private InputTarget CheckOcr(DesktopCommand command, PreparedInput? previous)
    {
        if (!IsClick(command.Action))
        {
            throw Block(ReasonCodes.UnsupportedAction, "An OCR word only supports clicks.");
        }

        var now = DateTimeOffset.UtcNow;
        if (_ocr is null || !_ocr.TryResolve(command.Ref!, out var word, out var hwnd, out var capturedAt) ||
            capturedAt > now || now - capturedAt > TimeSpan.FromSeconds(10) || _index.ByHandle(hwnd) is not { } window)
        {
            throw Stale("The OCR reference is missing or its capture has expired.");
        }

        CheckWindow(window, previous);
        if (command.Window is not null && _index.ResolveWindow(command.Window)?.Id != window.Id)
        {
            throw Block(ReasonCodes.WindowChanged, "The OCR reference does not belong to the requested window.");
        }

        var clip = NativeDesktop.Intersect(window.Sample.Bounds, NativeDesktop.VirtualScreen);
        var point = PointFor(null, word.Box);
        if (!NativeDesktop.Contains(clip, point))
        {
            throw Block(ReasonCodes.Offscreen, "The OCR point is outside the capture window.");
        }

        CheckPointerWindow(window, 0, point);
        var element = _uia.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y });
        if (element is not null)
        {
            var parent = element;
            for (var depth = 0; parent is not null && depth < 64; depth++)
            {
                if (IndependentlyActionable(parent))
                {
                    element = parent;
                    break;
                }

                if ((long)parent.CurrentNativeWindowHandle == window.Sample.Hwnd) break;
                parent = _rawWalker.GetParentElement(parent);
            }
        }

        var live = element is null ? null : ReadLive(window, element, 0, true);
        if (live?.Sample.Enabled == false)
        {
            throw Block(ReasonCodes.Disabled, "The OCR point hits a disabled control.");
        }

        return new InputTarget(window, live, Channels.PhysicalClick, point, word.Text);
    }

    private void CheckWindow(DesktopWindow window, PreparedInput? previous)
    {
        var hwnd = window.Sample.Hwnd;
        if (previous is not null && previous.Window != window.Id || NativeDesktop.ProcessId(hwnd) != window.Sample.Pid)
        {
            throw Block(ReasonCodes.WindowChanged, "The target window changed.");
        }

        CheckAccess(window.Sample.Inaccessible);
        if (window.BlockedBy is not null)
        {
            throw Block(ReasonCodes.ModalBlocking, $"A modal dialog ({window.BlockedBy}) blocks the target window.");
        }

        if (!NativeDesktop.Enabled(hwnd))
        {
            throw Block(ReasonCodes.Disabled, "The target window is disabled.");
        }

        if (window.Sample.Minimized)
        {
            throw Block(ReasonCodes.Offscreen, "The target window is minimized.");
        }
    }

    private static void CheckAccess(string? inaccessible)
    {
        switch (inaccessible)
        {
            case "elevated": throw Block(ReasonCodes.ElevatedTarget, "The target process is elevated or could not be inspected.");
            case "secure_desktop": throw Block(ReasonCodes.SecureDesktop, "The target is on a secure desktop or sign-in surface.");
            case "blocked_by_policy": throw new DesktopFault(ActStatus.Denied, ReasonCodes.PolicyForbidden, "The target process is blocked by policy.");
            case "slow": throw Block(ReasonCodes.EngineTimeout, "The target window's accessibility provider is too slow right now (inaccessible=slow); it is skipped for a short while. Try again shortly or use another window.");
        }
    }

    private LiveElement ResolveElement(DesktopWindow window, string identity, long? generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = generation is { } gen ? window.Snapshot(gen) : window.Latest;
        var observed = snapshot?.Elements.FirstOrDefault(node => node.Sample.Identity == identity);
        if (observed is null || !_reads.TryGetValue(window.Id, out var read) || !read.Elements.TryGetValue(identity, out var element))
        {
            throw Stale("The reference was not present in the requested observation generation.", window.Id);
        }

        try
        {
            var live = ReadLive(window, element, observed.Sample.Index, false);
            if (live?.Sample.Enabled == false && observed.Sample.Enabled)
            {
                throw Block(ReasonCodes.Disabled, "The target control became disabled.");
            }

            if (live is null || !observed.Sample.SameTarget(live.Sample) ||
                NativeDesktop.HasArea(live.Sample.VisibleBounds) && !observed.Sample.PositionMatches(live.Sample))
            {
                throw Stale("The element changed since it was observed.", window.Id);
            }

            return live with { Ref = observed.Ref };
        }
        catch (COMException ex) when (ReferenceUnavailable(ex))
        {
            throw Stale("The control no longer exists.", window.Id);
        }
    }

    private LiveElement? FocusedIn(DesktopWindow window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var focused = _uia.GetFocusedElement();
        if (focused is null || ReadLive(window, focused, 0, true) is not { } live || !live.Sample.Focused)
        {
            return null;
        }

        return live with { Ref = window.Refs.GetValueOrDefault(live.Sample.Identity) };
    }

    private LiveElement? ReadLive(DesktopWindow window, IUIAutomationElement element, int index, bool raw)
    {
        using var diagnostic = DesktopDiagnostics.Measure("ReadLive");
        var current = element.BuildUpdatedCache(_elementCache);
        var root = _uia.ElementFromHandle((nint)window.Sample.Hwnd);
        var native = (long)current.CachedNativeWindowHandle;
        if (native != 0 && NativeDesktop.Root(native) != window.Sample.Hwnd)
        {
            return null;
        }

        var walker = raw ? _rawWalker : _walker;
        var clip = NativeDesktop.Intersect(window.Sample.Bounds, NativeDesktop.VirtualScreen);
        var identity = CachedIdentity(current);
        if (identity is null)
        {
            return null;
        }

        var reachedRoot = _uia.CompareElements(element, root) != 0;
        var path = new List<SelectorSegment>();
        string? parentId = null;
        ScrollPosition? scroll = null;
        var parent = element;
        for (var depth = 0; !reachedRoot && depth < 64; depth++)
        {
            parent = walker.GetParentElement(parent);
            if (parent is null) break;
            var cachedParent = parent.BuildUpdatedCache(_elementCache);
            var bounds = cachedParent.CachedBoundingRectangle;
            var rect = NativeDesktop.Rect(bounds.left, bounds.top, bounds.right, bounds.bottom);
            if (NativeDesktop.HasArea(rect)) clip = NativeDesktop.Intersect(clip, rect);
            parentId ??= CachedIdentity(cachedParent);
            if (scroll is null && cachedParent.GetCachedPropertyValueEx(UIA_ScrollHorizontalScrollPercentPropertyId, 1) is double horizontal &&
                cachedParent.GetCachedPropertyValueEx(UIA_ScrollVerticalScrollPercentPropertyId, 1) is double vertical)
            {
                scroll = new ScrollPosition(Identity(parent) ?? string.Empty, horizontal, vertical);
            }

            reachedRoot = _uia.CompareElements(parent, root) != 0;
            if (!reachedRoot)
            {
                path.Add(new SelectorSegment(Role(cachedParent.CachedControlType), cachedParent.CachedAutomationId,
                    cachedParent.CachedName, cachedParent.CachedClassName, null));
            }
        }

        if (!reachedRoot) return null;
        var sample = Sample(current, identity, parentId, index, clip, scroll, includeValue: false);
        CheckAccess(Inaccessible(sample.Pid, ProcessInfo(sample.Pid)));
        path.Reverse();
        path.Add(new SelectorSegment(sample.Role, sample.AutomationId, sample.Name, sample.ClassName, sample.Index));
        return new LiveElement(element, sample, native, path);
    }

    private ActionDescriptor Descriptor(DesktopCommand command, InputTarget target)
    {
        using var diagnostic = DesktopDiagnostics.Measure("Descriptor");
        var window = target.Window;
        var live = target.Live;
        var element = live is null ? null : Describe(window, live.Sample, live.Ref ?? command.Ref ?? string.Empty);
        if (element is not null) element = element with { Path = live!.Path };
        var dropTarget = target.DropLive is { } dropLive
            ? Describe(window, dropLive.Sample, dropLive.Ref ?? command.Value ?? string.Empty) with { Path = dropLive.Path }
            : null;
        if (target.OcrText is { } word)
        {
            // Include the OCR label as well as the hit element for delete-name checks.
            element = element is null
                ? new ElementDescriptor(command.Ref!, "desktop", window.Sample.Process, window.Sample.Title, null, "text", word, null, false, true, [])
                : element with { Name = $"{element.Name} {word}" };
        }

        var process = element?.Process ?? window.Sample.Process;
        var password = element?.IsPassword == true;
        var value = command.Value;
        return new ActionDescriptor(command.Tool, "desktop", command.Action, element, process, window.Sample.Title, null,
            value is null ? null : password ? "<redacted>" : $"<{value.Length} chars>", command.Keys?.Canonical)
        {
            ContainsDeleteCommand = live is not null && CommandTarget(element, window) && HasDeleteCommand(command, live),
            DropTarget = dropTarget,
        };
    }

    private static bool SamePolicyTarget(ElementDescriptor? expected, ElementDescriptor? actual) =>
        expected is null ? actual is null : actual is not null && expected.Process == actual.Process &&
        expected.Name == actual.Name && expected.AutomationId == actual.AutomationId && expected.Role == actual.Role &&
        expected.IsPassword == actual.IsPassword;

    private static Point PointFor(LiveElement? live, ScreenRect rect)
    {
        using var diagnostic = DesktopDiagnostics.Measure("PointFor");
        if (!NativeDesktop.OnMonitor(rect))
        {
            throw Block(ReasonCodes.Offscreen, "There is no visible point on the target.");
        }

        if (live is not null)
        {
            try
            {
                if (live.Element.GetClickablePoint(out var candidate) != 0)
                {
                    var point = new Point(candidate.x, candidate.y);
                    if (NativeDesktop.Contains(rect, point)) return point;
                }
            }
            catch (COMException ex) when (ex.HResult is unchecked((int)0x80040204) or unchecked((int)0x80004001))
            {
                // Custom providers may expose patterns and bounds without a clickable point.
            }
        }

        return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    private bool CanReuseClickPoint(DesktopWindow window, LiveElement live, Point point)
    {
        try
        {
            CheckPointerWindow(window, live.NativeHandle, point);
        }
        catch (DesktopFault ex) when (ex.Reason is ReasonCodes.OccludedByWindow or ReasonCodes.OccludedSameWindow)
        {
            // The provider may choose a different visible point. Retry the normal selection path.
            return false;
        }

        // An ancestor-only hit also retries normal point selection, preserving late pattern fallback.
        return HitTest(live.Element, point) == Hit.Exact;
    }

    private void CheckPointerWindow(DesktopWindow window, long nativeTarget, Point point)
    {
        var hit = NativeDesktop.WindowAt(point);
        if (hit == 0 || NativeDesktop.Root(hit) != window.Sample.Hwnd)
        {
            var covering = _index.ByHandle(NativeDesktop.Root(hit));
            var detail = covering is null ? "an unindexed window" : $"{covering.Id} ({covering.Sample.Process})";
            throw Block(ReasonCodes.OccludedByWindow,
                $"Point ({point.X}, {point.Y}) is covered by {detail}. Target window {window.Id} received no click.");
        }

        if (nativeTarget != 0 && !NativeDesktop.IsWithin(nativeTarget, hit))
        {
            throw Block(ReasonCodes.OccludedSameWindow, "Another control in the target window covers the point.");
        }

        var pid = NativeDesktop.ProcessId(hit);
        CheckAccess(Inaccessible(pid, ProcessInfo(pid)));
    }

    private Hit HitTest(IUIAutomationElement target, Point point)
    {
        using var diagnostic = DesktopDiagnostics.Measure("HitTest");
        var hit = _uia.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y });
        if (hit is null) return Hit.Other;
        if (_uia.CompareElements(target, hit) != 0) return Hit.Exact;
        if (IsAncestor(hit, target)) return Hit.Ancestor;
        var current = hit;
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            if (_uia.CompareElements(target, current) != 0) return Hit.Exact;
            if (IndependentlyActionable(current)) return Hit.Other;
            current = _rawWalker.GetParentElement(current);
        }

        return Hit.Other;
    }

    private static bool IndependentlyActionable(IUIAutomationElement element) =>
        element.CurrentIsKeyboardFocusable != 0 ||
        element.GetCurrentPropertyValueEx(UIA_IsInvokePatternAvailablePropertyId, 1) is true ||
        element.GetCurrentPropertyValueEx(UIA_IsTogglePatternAvailablePropertyId, 1) is true ||
        element.GetCurrentPropertyValueEx(UIA_IsSelectionItemPatternAvailablePropertyId, 1) is true ||
        element.GetCurrentPropertyValueEx(UIA_IsExpandCollapsePatternAvailablePropertyId, 1) is true;

    private bool IsAncestor(IUIAutomationElement ancestor, IUIAutomationElement? element)
    {
        for (var depth = 0; element is not null && depth < 64; depth++)
        {
            if (_uia.CompareElements(ancestor, element) != 0) return true;
            element = _rawWalker.GetParentElement(element);
        }

        return false;
    }

    /// <summary>A2: a character is sent again at most once; a second miss for the same character ends the act with the error.</summary>
    internal static bool MayResend(bool alreadyResentThisCharacter, int batchLength) => TypingPump.MayResend(alreadyResentThisCharacter, batchLength);

    /// <summary>
    /// The editor did not acknowledge the last character within 250 ms. When its caret is still exactly where it was before that
    /// character (so nothing was inserted, even after another 80 ms), the same character is sent once more; the caller allows this
    /// once per character. Returns false when the state is anything else. An editor that is only slow can still take both copies:
    /// the engine therefore never reports "confirmed" after a re-send without a settled readback.
    /// </summary>
    private bool ResendLostScalar(InputTarget target, VerifiedNativeEditor editor, int caretBefore, string scalar,
        InputProgress progress, Func<string?> stopReason, CancellationToken ct)
    {
        bool Unchanged() => NativeDesktop.TryGetPacketEditorSelection(editor.Handle, out var start, out var end) &&
            start == caretBefore && end == caretBefore;
        if (!Unchanged()) return false;
        Thread.Sleep(80);
        if (!Unchanged()) return false;
        CheckInputBoundary(target.Window, stopReason, ct);
        DesktopTypingFocus.CheckBatch(false, editor, () => RequireKeyboardFocus(target.Window, target.Live!), ReadNativeTypingFocus);
        CheckStopBoundary(stopReason, ct);
        progress.Accept(NativeInput.Type(scalar), Channels.UnicodeTyping);
        return true;
    }

    private void FocusForTyping(DesktopWindow window, LiveElement live, InputProgress progress, Func<string?> stopReason, CancellationToken ct)
    {
        using var diagnostic = DesktopDiagnostics.Measure("FocusForTyping");
        CheckInputBoundary(window, stopReason, ct);
        var focused = _uia.GetFocusedElement();
        if (focused is null || _uia.CompareElements(focused, live.Element) == 0)
        {
            CheckInputBoundary(window, stopReason, ct);
            progress.Pattern(live.Element.SetFocus, Channels.Api);
        }
        CheckInputBoundary(window, stopReason, ct);
        Activate(window, progress, stopReason, ct);
        focused = _uia.GetFocusedElement();
        if (focused is null || _uia.CompareElements(focused, live.Element) == 0)
            throw Block(ReasonCodes.FocusFailed, "The target control did not take keyboard focus.");
    }

    private static void Activate(DesktopWindow window, InputProgress progress, Func<string?> stopReason, CancellationToken ct)
    {
        var activated = RunForegroundAttempt(NativeDesktop.TryForeground, window.Sample.Hwnd, progress,
            () => CheckInputBoundary(window, stopReason, ct));
        if (!activated)
        {
            throw Block(ReasonCodes.ForegroundFailed, "Windows did not bring the requested window to the foreground.");
        }
    }

    internal delegate bool ForegroundAttemptFn(long hwnd, out bool windowInput, out bool keyTapped, Action? boundary);

    /// <summary>
    /// One focus attempt and its input accounting. A later boundary can stop activation after an earlier restore, foreground or
    /// F24 operation: the input that was completed is kept even when the native helper exits by throwing, and a tapped F24 is
    /// a key press and is reported as one (<see cref="Channels.KeyF24"/>). The native call is passed in so this is tested without one.
    /// </summary>
    internal static bool RunForegroundAttempt(ForegroundAttemptFn attempt, long hwnd, InputProgress progress, Action? boundary)
    {
        var start = Stopwatch.GetTimestamp();
        var windowInput = false;
        var keyTapped = false;
        try
        {
            return attempt(hwnd, out windowInput, out keyTapped, boundary);
        }
        finally
        {
            if (windowInput)
                progress.Accept(new InputReceipt(1, 1, Stopwatch.GetElapsedTime(start).TotalMilliseconds),
                    keyTapped ? Channels.KeyF24 : Channels.Api);
        }
    }

    private void RequireKeyboardFocus(DesktopWindow window, LiveElement live)
    {
        using var diagnostic = DesktopDiagnostics.Measure("RequireKeyboardFocus");
        var focused = _uia.GetFocusedElement();
        var native = NativeDesktop.FocusHandle();
        if (focused is null || _uia.CompareElements(focused, live.Element) == 0 ||
            NativeDesktop.Foreground != window.Sample.Hwnd || NativeDesktop.Root(native) != window.Sample.Hwnd ||
            live.NativeHandle != 0 && !NativeDesktop.IsWithin(live.NativeHandle, native))
        {
            throw Block(ReasonCodes.FocusChanged, "Keyboard focus changed before the next input batch.");
        }
    }

    private void CheckScrollRouting(DesktopWindow window, LiveElement? live)
    {
        if (NativeDesktop.WheelFollowsPointer) return;
        if (NativeDesktop.Foreground != window.Sample.Hwnd || NativeDesktop.Root(NativeDesktop.FocusHandle()) != window.Sample.Hwnd)
        {
            throw Block(ReasonCodes.FocusFailed, "Windows routes wheel input to keyboard focus, which is outside the requested window.");
        }

        if (live is not null)
        {
            var focused = _uia.GetFocusedElement();
            if (focused is null || !IsAncestor(live.Element, focused))
            {
                throw Block(ReasonCodes.FocusFailed, "Windows routes wheel input to a different focused control.");
            }
        }
    }

    private static void CheckInputBoundary(DesktopWindow window, Func<string?> stopReason, CancellationToken ct,
        bool pointer = false, bool sharedInput = true)
    {
        using var diagnostic = DesktopDiagnostics.Measure("CheckInputBoundary");
        CheckStopBoundary(stopReason, ct);
        if (!NativeDesktop.OnInputDesktop()) throw Block(ReasonCodes.SecureDesktop, "The input desktop changed.");
        if (NativeDesktop.ProcessId(window.Sample.Hwnd) != window.Sample.Pid ||
            !NativeDesktop.IsTopLevelWindow(window.Sample.Hwnd) || NativeDesktop.Minimized(window.Sample.Hwnd) ||
            !NativeDesktop.Enabled(window.Sample.Hwnd))
        {
            throw Block(ReasonCodes.WindowChanged, "The target window changed before input.");
        }

        if (sharedInput && (NativeDesktop.ModifiersHeld() || pointer && (NativeDesktop.MouseButtonsHeld() || NativeDesktop.PointerCaptured())))
        {
            throw Block(ReasonCodes.UserActive, "A modifier, mouse button, or pointer capture is active.");
        }
    }

    private static void CheckStopBoundary(Func<string?> stopReason, CancellationToken ct)
    {
        if (stopReason() is { } reason)
        {
            throw new DesktopFault(reason == ReasonCodes.KillSwitch ? ActStatus.Denied : ActStatus.Blocked,
                reason, "The input was stopped before the next batch.");
        }

        ct.ThrowIfCancellationRequested();
    }

    private static string PatternChannel(string action, ElementActions actions)
    {
        var (required, channel) = action switch
        {
            "invoke" => (ElementActions.Invoke, Channels.Invoke),
            "toggle" => (ElementActions.Toggle, Channels.Toggle),
            "select" => (ElementActions.Select, Channels.Select),
            "expand" or "collapse" => (ElementActions.ExpandCollapse, Channels.Api),
            "set_value" or "clear" => (ElementActions.Value, Channels.SetValue),
            "type" => (ElementActions.Type, Channels.UnicodeTyping),
            "focus" => (ElementActions.Focus, Channels.Api),
            "scroll_into_view" => (ElementActions.ScrollIntoView, Channels.Api),
            _ => (ElementActions.None, Channels.Api),
        };
        return required != ElementActions.None && actions.HasFlag(required) ? channel :
            throw Block(ReasonCodes.UnsupportedAction, "The control does not support this action.");
    }

    private static Action PatternOperation(DesktopCommand command, IUIAutomationElement element, string channel)
    {
        if (channel == Channels.Invoke) return Pattern<IUIAutomationInvokePattern>(element, UIA_InvokePatternId).Invoke;
        if (channel == Channels.Toggle) return Pattern<IUIAutomationTogglePattern>(element, UIA_TogglePatternId).Toggle;
        return command.Action switch
        {
            "select" => Pattern<IUIAutomationSelectionItemPattern>(element, UIA_SelectionItemPatternId).Select,
            "expand" => Pattern<IUIAutomationExpandCollapsePattern>(element, UIA_ExpandCollapsePatternId).Expand,
            "collapse" => Pattern<IUIAutomationExpandCollapsePattern>(element, UIA_ExpandCollapsePatternId).Collapse,
            "set_value" or "clear" => SetValue(element, command.Action == "clear" ? string.Empty : command.Value!),
            "focus" => element.SetFocus,
            "scroll_into_view" => Pattern<IUIAutomationScrollItemPattern>(element, UIA_ScrollItemPatternId).ScrollIntoView,
            _ => throw Block(ReasonCodes.UnsupportedAction, "The control does not support this action."),
        };
    }

    private static Action SetValue(IUIAutomationElement element, string value)
    {
        var pattern = Pattern<IUIAutomationValuePattern>(element, UIA_ValuePatternId);
        if (pattern.CurrentIsReadOnly != 0) throw Block(ReasonCodes.Disabled, "The value is read-only.");
        return () => pattern.SetValue(value);
    }

    private static T Pattern<T>(IUIAutomationElement element, int id) where T : class =>
        element.GetCurrentPattern(id) as T ?? throw Block(ReasonCodes.UnsupportedAction, "The UI Automation pattern is unavailable.");

    private static bool IsClick(string action) => action is "click" or "double_click" or "right_click";
    private static bool ReferenceUnavailable(COMException error) =>
        ElementUnavailable(error) || error.HResult is unchecked((int)0x80070057) or unchecked((int)0x80070578);
    private static DesktopFault Block(string reason, string detail) => new(ActStatus.Blocked, reason, detail);
    private static DesktopFault Stale(string detail, string? window = null) => new(ActStatus.Stale, ReasonCodes.NotFound, detail, window);

    private enum Hit { Exact, Ancestor, Other }
    private sealed record LiveElement(IUIAutomationElement Element, ElementSample Sample, long NativeHandle,
        IReadOnlyList<SelectorSegment> Path, string? Ref = null);
    private sealed record InputTarget(DesktopWindow Window, LiveElement? Live, string Channel, Point? Point = null, string? OcrText = null,
        LiveElement? DropLive = null, Point? DropPoint = null);
}
