using System.Diagnostics;
using System.Globalization;
using OmniWin.Core;

namespace OmniWin.SystemTools;

/// <summary>The text of the clipboard as read by <c>clipboard_get</c>, capped at <see cref="SystemToolsService.MaxClipboardChars"/>.</summary>
public sealed record ClipboardText(string Text, int Chars, bool Truncated);

internal sealed record SystemBackends(
    IAppCatalog Apps,
    IShellLauncher Launcher,
    IWindowOps Windows,
    IAudioBackend Audio,
    IMediaBackend Media,
    ISystemInfoProvider Info,
    IClipboardBackend Clipboard)
{
    public static SystemBackends CreateDefault() => new(
        new ShellAppCatalog(), new ShellLauncher(), new Win32WindowOps(), new CoreAudioBackend(),
        new MediaSessionBackend(), new WindowsSystemInfo(), new Win32Clipboard());
}

/// <summary>
/// The system tools (contract §7, §8). Every act runs resolve → precheck → gate → input → verify, reports an accurate
/// <c>input_sent</c>, records phase timings, and never throws. Direct Windows APIs only: nothing here runs a shell,
/// a script or an arbitrary executable.
/// <para>The §7 methods return plain values and cannot say denied / blocked / engine_timeout (proposal Q5). Each has a
/// richer <c>...OutcomeAsync</c> twin that the MCP tool files use; the plain method throws <see cref="SystemToolFault"/>.</para>
/// </summary>
public sealed class SystemToolsService : ISystemTools, IComponentStatus
{
    public const int MaxClipboardChars = 20_000;
    public const int MaxClipboardSetChars = 1_000_000;
    private const int SettleMs = 250;
    private const int AppsCacheSeconds = 60;
    private const int LaunchWaitMs = 2000;

    private static readonly string[] WindowActions =
        ["focus", "minimize", "maximize", "restore", "move", "resize", "snap_left", "snap_right", "close"];

    private static readonly string[] MediaActions = ["play_pause", "play", "pause", "next", "previous", "stop"];

    private readonly ISafetyGate _gate;
    private readonly IKillSwitch _kill;
    private readonly IUserActivity _activity;
    private readonly IMetrics _metrics;
    private readonly IDesktopEngine _desktop;
    private readonly IForegroundService _foreground;
    private readonly SystemToolsOptions _options;
    private readonly SystemBackends _backends;

    private readonly SemaphoreSlim _appsLock = new(1, 1);
    private IReadOnlyList<InstalledApp>? _apps;
    private long _appsAt;

    public SystemToolsService(
        ISafetyGate gate, IKillSwitch kill, IUserActivity activity, IMetrics metrics,
        IDesktopEngine desktop, IForegroundService foreground, SystemToolsOptions options)
        : this(gate, kill, activity, metrics, desktop, foreground, options, SystemBackends.CreateDefault())
    {
    }

    internal SystemToolsService(
        ISafetyGate gate, IKillSwitch kill, IUserActivity activity, IMetrics metrics,
        IDesktopEngine desktop, IForegroundService foreground, SystemToolsOptions options, SystemBackends backends)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _kill = kill ?? throw new ArgumentNullException(nameof(kill));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
    }

    public string Component => "system_tools";

    public string State => "ready";

    public string? Detail => "direct Windows APIs; no shell, script or arbitrary executable";

    // ------------------------------------------------------------------ ISystemTools (plain values)

    public async Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string? filter, CancellationToken ct) =>
        Unwrap(await ListAppsOutcomeAsync(filter, ct).ConfigureAwait(false));

    public Task<ActResult> LaunchAppAsync(string app, string? args, ExpectSpec? expect, CancellationToken ct) =>
        RunActAsync("app_launch", _options.ShellTimeoutMs, (timer, progress, token) => LaunchAsync(app, args, expect, timer, progress, token), ct);

    public Task<ActResult> WindowManageAsync(WindowManageRequest req, CancellationToken ct) =>
        RunActAsync("window_manage", _options.OperationTimeoutMs, (timer, progress, token) => ManageWindowAsync(req, timer, progress, token), ct);

    public async Task<AudioState> GetAudioAsync(CancellationToken ct) =>
        Unwrap(await GetAudioOutcomeAsync(ct).ConfigureAwait(false));

    public Task<ActResult> SetAudioAsync(int? volumePercent, bool? mute, CancellationToken ct) =>
        RunActAsync("audio", _options.OperationTimeoutMs, (timer, progress, token) => SetAudioCoreAsync(volumePercent, mute, timer, progress, token), ct);

    public Task<ActResult> MediaAsync(string action, CancellationToken ct) =>
        RunActAsync("media", _options.OperationTimeoutMs, (timer, progress, token) => MediaCoreAsync(action, timer, progress, token), ct);

    public async Task<string> SystemInfoAsync(string kind, CancellationToken ct) =>
        Unwrap(await SystemInfoOutcomeAsync(kind, ct).ConfigureAwait(false));

    public async Task<string> ClipboardGetAsync(CancellationToken ct) =>
        Unwrap(await ClipboardGetOutcomeAsync(ct).ConfigureAwait(false)).Text;

    public Task<ActResult> ClipboardSetAsync(string text, CancellationToken ct) =>
        RunActAsync("clipboard_set", _options.OperationTimeoutMs, (timer, progress, token) => ClipboardSetCoreAsync(text, timer, progress, token), ct);

    // ------------------------------------------------------------------ richer reads (used by the tool files)

    public Task<SystemOutcome<IReadOnlyList<InstalledApp>>> ListAppsOutcomeAsync(string? filter, CancellationToken ct) =>
        RunReadAsync("apps_list", _options.ShellTimeoutMs, async (timer, token) =>
        {
            var apps = await GetAppsAsync(token).ConfigureAwait(false);
            timer.End("read");
            IReadOnlyList<InstalledApp> result = string.IsNullOrWhiteSpace(filter)
                ? apps
                : apps.Where(a => a.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            timer.End("format");
            return result;
        }, ct);

    public Task<SystemOutcome<AudioState>> GetAudioOutcomeAsync(CancellationToken ct) =>
        RunReadAsync("audio", _options.OperationTimeoutMs, async (timer, token) =>
        {
            var state = await Task.Run(_backends.Audio.Get, token).ConfigureAwait(false);
            timer.End("read");
            return state;
        }, ct);

    public Task<SystemOutcome<string>> SystemInfoOutcomeAsync(string kind, CancellationToken ct) =>
        RunReadAsync("system_info", _options.OperationTimeoutMs, async (timer, token) =>
        {
            var wanted = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (!WindowsSystemInfo.Kinds.Contains(wanted))
            {
                throw new SystemToolFault(ActResult.Failed(false, ReasonCodes.UnsupportedAction,
                    $"kind must be one of: {string.Join(", ", WindowsSystemInfo.Kinds)}.", Timing.Total(0)));
            }

            var text = await _backends.Info.GetAsync(wanted, token).ConfigureAwait(false);
            timer.End("read");
            return text;
        }, ct);

    /// <summary>
    /// <c>clipboard_get</c> is labelled Risky (record only) and goes through the gate. Its text is data from another app
    /// (untrusted header in the tool file) and is never logged.
    /// </summary>
    public async Task<SystemOutcome<ClipboardText>> ClipboardGetOutcomeAsync(CancellationToken ct)
    {
        var holder = new ClipboardHolder();
        var result = await RunActAsync("clipboard_get", _options.OperationTimeoutMs, async (timer, progress, token) =>
        {
            timer.End("resolve");
            if (Precheck(false, token) is { } stopped) return stopped;
            timer.End("precheck");

            var descriptor = Describe("clipboard_get", "clipboard_get", null, null, null);
            if (await GateAsync(descriptor, timer, token).ConfigureAwait(false) is { } denied) return denied;
            if (Precheck(false, token) is { } stoppedAfterGate) return stoppedAfterGate;

            var raw = await _backends.Clipboard.GetTextAsync(token).ConfigureAwait(false) ?? string.Empty;
            timer.End("read");
            holder.Value = raw.Length > MaxClipboardChars
                ? new ClipboardText(raw[..MaxClipboardChars], raw.Length, true)
                : new ClipboardText(raw, raw.Length, false);
            timer.End("format");
            return new ActResult(ActStatus.Confirmed, false, null, null, null, Channels.Api, Timing.Total(0), Risk: RiskClass.Risky);
        }, ct).ConfigureAwait(false);

        return holder.Value is { } value && result.Status == ActStatus.Confirmed
            ? SystemOutcome<ClipboardText>.Success(value, result.Timing)
            : SystemOutcome<ClipboardText>.Fail(result with { Risk = RiskClass.Risky });
    }

    private sealed class ClipboardHolder
    {
        public ClipboardText? Value { get; set; }
    }

    // ------------------------------------------------------------------ app_launch

    private async Task<ActResult> LaunchAsync(
        string app, string? args, ExpectSpec? expect, PhaseTimer timer, InputProgress progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            return Bad(ReasonCodes.UnsupportedAction, "app is required: a Start-menu app name or AUMID.");
        }

        // resolve: only apps the Start menu lists, or an exact allow-listed path.
        InstalledApp? known = null;
        string? allowListedPath = null;
        var apps = await GetAppsAsync(ct).ConfigureAwait(false);
        var match = AppLaunchRules.Match(app, apps);
        if (match.Single is { } single)
        {
            known = single;
        }
        else if (match.Candidates.Count > 1)
        {
            return Fail(ActStatus.Blocked, ReasonCodes.NotFound,
                $"\"{ResultFormatter.OneLine(app)}\" matches {match.Candidates.Count} apps (" +
                $"{string.Join(", ", match.Candidates.Take(5).Select(a => ResultFormatter.Quote(a.Name, 40)))}); use the exact name.");
        }
        else if (AppLaunchRules.AllowListedPath(app, _options.AppLaunchAllowPaths) is { } listed)
        {
            allowListedPath = listed;
        }
        else if (AppLaunchRules.LooksLikePathOrCommand(app))
        {
            return Fail(ActStatus.Denied, ReasonCodes.PolicyForbidden,
                "app_launch starts Start-menu apps by name or AUMID only. A path, URL, command line or file is refused " +
                "unless the exact path is in app_launch_allow_paths.");
        }
        else
        {
            return Fail(ActStatus.Blocked, ReasonCodes.NotFound,
                $"No Start-menu app is named \"{ResultFormatter.OneLine(app)}\". Use apps_list to see the names.");
        }

        timer.End("resolve");

        // precheck: args never reach a command processor or script host; Start-menu launches carry no args at all.
        var process = known is not null ? AppLaunchRules.ExecutableFor(known) : Path.GetFileName(allowListedPath!);
        var hasArgs = !string.IsNullOrWhiteSpace(args);
        if (hasArgs && AppLaunchRules.IsCommandProcessor(process))
        {
            return Fail(ActStatus.Denied, ReasonCodes.PolicyForbidden,
                "A command processor or script host (cmd, powershell, pwsh, wt, wscript, cscript, mshta) never receives args: " +
                "a command line there would bypass the no-delete check that typed commands go through.");
        }

        if (hasArgs && known is not null)
        {
            return Fail(ActStatus.Blocked, ReasonCodes.UnsupportedAction,
                "args are only supported for an allow-listed executable path, not for Start-menu apps.");
        }

        if (Precheck(true, ct) is { } stopped) return stopped;
        timer.End("precheck");

        // F2: starting an app is described as a click on the app's entry, with the Start-menu name as the target name, so the
        // owner's delete words (for example an "Uninstall ..." entry) are checked by the existing name rule.
        var appName = known?.Name ?? (allowListedPath is null ? process : Path.GetFileNameWithoutExtension(allowListedPath));
        var descriptor = new ActionDescriptor("app_launch", SystemToolsOptions.EngineName, "click",
            new ElementDescriptor("app:" + appName, SystemToolsOptions.EngineName, process ?? string.Empty, appName ?? string.Empty,
                null, "listitem", appName ?? string.Empty, null, false, true, []),
            process ?? known?.Name, known?.Name ?? process, null, hasArgs ? $"<{args!.Length} chars>" : null, null);
        if (await GateAsync(descriptor, timer, ct).ConfigureAwait(false) is { } denied) return denied;
        if (Precheck(true, ct) is { } stoppedAfterGate) return stoppedAfterGate;

        IReadOnlySet<string> before = await WindowIdsAsync(ct).ConfigureAwait(false);

        progress.Sent = true;
        if (known is not null)
        {
            await _backends.Launcher.StartAppAsync(known.LaunchId, ct).ConfigureAwait(false);
        }
        else if (!await _backends.Launcher.StartExecutableAsync(allowListedPath!, args, ct).ConfigureAwait(false))
        {
            return Fail(ActStatus.Error, ReasonCodes.NotFound, "Windows did not start the executable.", sent: true);
        }

        timer.End("input");

        // verify: the caller's expectation if given, otherwise a window that was not open before (bounded polling).
        if (expect is not null)
        {
            var wait = await _desktop.WaitForAsync(expect.Condition, expect.EffectiveTimeoutMs, ct).ConfigureAwait(false);
            timer.End("verify");
            return wait.Met
                ? Done(ActStatus.Confirmed, null, null)
                : Done(ActStatus.Timeout, wait.Reason ?? ReasonCodes.EngineTimeout, "The expected result did not appear in time.");
        }

        var opened = await WaitForNewWindowAsync(before, ct).ConfigureAwait(false);
        timer.End("verify");
        return opened is null
            ? Done(ActStatus.Unverified, null, "The app was started; no new window appeared yet.")
            : Done(ActStatus.Confirmed, null, $"Window {opened} opened.");
    }

    private async Task<IReadOnlySet<string>> WindowIdsAsync(CancellationToken ct)
    {
        var windows = await _desktop.ListWindowsAsync(ct).ConfigureAwait(false);
        return windows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<string?> WaitForNewWindowAsync(IReadOnlySet<string> before, CancellationToken ct)
    {
        // Bounded polling (every 100 ms, at most LaunchWaitMs): window creation has no event that is usable
        // before the window's process is known.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < LaunchWaitMs)
        {
            var windows = await _desktop.ListWindowsAsync(ct).ConfigureAwait(false);
            var added = windows.FirstOrDefault(w => !before.Contains(w.Id));
            if (added is not null) return added.Id;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return null;
    }

    // ------------------------------------------------------------------ window_manage

    private async Task<ActResult> ManageWindowAsync(WindowManageRequest req, PhaseTimer timer, InputProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var action = (req.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (!WindowActions.Contains(action))
        {
            return Bad(ReasonCodes.UnsupportedAction, $"action must be one of: {string.Join(", ", WindowActions)}.");
        }

        if (string.IsNullOrWhiteSpace(req.Window))
        {
            return Bad(ReasonCodes.UnsupportedAction, "window is required: a wN id, a process name or a title substring.");
        }

        if (action == "move" && (req.X is null || req.Y is null))
        {
            return Bad(ReasonCodes.UnsupportedAction, "move needs x and y.");
        }

        if (action == "resize" && (req.Width is not > 0 || req.Height is not > 0 || req.Width > 32000 || req.Height > 32000))
        {
            return Bad(ReasonCodes.UnsupportedAction, "resize needs width and height between 1 and 32000.");
        }

        var risk = action == "close" ? RiskClass.Risky : (RiskClass?)null;

        // resolve
        var (window, notFound) = await ResolveWindowAsync(req.Window, ct).ConfigureAwait(false);
        timer.End("resolve");
        if (window is null) return notFound! with { Risk = risk };

        // precheck
        if (window.Pid == Environment.ProcessId || window.Process.Equals("OmniWin.Server.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ActStatus.Denied, ReasonCodes.PolicyForbidden, "OmniWin's own windows can't be managed.", risk: risk);
        }

        switch (window.Inaccessible)
        {
            case "elevated":
                return Fail(ActStatus.Blocked, ReasonCodes.ElevatedTarget, "The window belongs to an elevated app OmniWin can't manage.", risk: risk);
            case "secure_desktop":
                return Fail(ActStatus.Blocked, ReasonCodes.SecureDesktop, "The window is on the secure desktop.", risk: risk);
            case "blocked_by_policy":
                return Fail(ActStatus.Denied, ReasonCodes.PolicyForbidden, $"{window.Process} can't be automated (secure desktop / sign-in, or process_blocklist_extra).", risk: risk);
        }

        if (Precheck(true, ct) is { } stopped) return stopped with { Risk = risk };
        timer.End("precheck");

        // gate
        var descriptor = Describe("window_manage", action, window.Process, window.Title, null);
        if (await GateAsync(descriptor, timer, ct).ConfigureAwait(false) is { } denied) return denied with { Risk = risk };
        if (Precheck(true, ct) is { } stoppedAfterGate) return stoppedAfterGate with { Risk = risk };

        // input + verify
        var hwnd = window.Hwnd;
        var ops = _backends.Windows;
        var before = ops.Read(hwnd);
        if (!before.Exists)
        {
            return Fail(ActStatus.Stale, ReasonCodes.NotFound, "The window closed before the action.", risk: risk);
        }

        switch (action)
        {
            case "focus":
            {
                // When the service can say what it sent, input_sent is what it reported (A11) and a tapped F24 key is reported as
                // a key press, not as "api". A service that only answers yes/no cannot say, so input_sent stays true for it, and
                // so does a call that was cancelled while the attempt may already have sent something.
                ForegroundAttempt attempt;
                if (_foreground is IForegroundDetails details)
                {
                    try
                    {
                        attempt = await details.BringToForegroundDetailedAsync(hwnd, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        progress.Sent = true;
                        throw;
                    }

                    progress.Sent = attempt.InputSent;
                }
                else
                {
                    progress.Sent = true;
                    attempt = new ForegroundAttempt(await _foreground.BringToForegroundAsync(hwnd, ct).ConfigureAwait(false), true, false);
                }

                var focused = attempt.Verified;
                timer.End("input");
                if (!focused && attempt.StopStatus is { } stopStatus)
                {
                    // The attempt was refused or stopped by a check: report that, not a generic focus failure. Nothing sent: the
                    // stop's own status and reason. Something sent before the stop: error input_interrupted, as for acts.
                    var channel = attempt.KeyTapped ? Channels.KeyF24 : Channels.Api;
                    return attempt.InputSent
                        ? Fail(ActStatus.Error, ReasonCodes.InputInterrupted,
                            $"The focus attempt stopped after input was sent ({attempt.StopReason}). {attempt.StopDetail}".TrimEnd(), sent: true) with { Channel = channel }
                        : Fail(stopStatus, attempt.StopReason ?? ReasonCodes.ForegroundFailed, attempt.StopDetail ?? "The focus attempt was stopped.", sent: false);
                }

                if (attempt.KeyTapped)
                {
                    return focused
                        ? Done(ActStatus.Confirmed, null, null) with { Channel = Channels.KeyF24 }
                        : Fail(ActStatus.Error, ReasonCodes.ForegroundFailed, "The window could not be verified in the foreground; the F24 key was tapped during the focus attempt.", sent: true) with { Channel = Channels.KeyF24 };
                }

                // A service that only answers yes/no (proposal Q2) cannot separate "refused before any input" from "tried and
                // not verified", so input_sent stays true for it; the detailed service says whether anything was sent.
                return focused
                    ? Done(ActStatus.Confirmed, null, null) with { InputSent = progress.Sent, Channel = progress.Sent ? Channels.Api : null }
                    : Fail(ActStatus.Error, ReasonCodes.ForegroundFailed,
                        attempt.InputSent
                            ? "The window could not be verified in the foreground; a focus attempt may have been made."
                            : "The window could not be brought to the foreground; no input was sent.",
                        sent: attempt.InputSent);
            }

            case "minimize":
                progress.Sent = true;
                ops.Minimize(hwnd);
                break;
            case "maximize":
                progress.Sent = true;
                ops.Maximize(hwnd);
                break;
            case "restore":
                progress.Sent = true;
                ops.Restore(hwnd);
                break;
            case "close":
                progress.Sent = true;
                ops.RequestClose(hwnd);
                break;
            default:
                progress.Sent = true;
                Place(ops, hwnd, action, req, before);
                break;
        }

        timer.End("input");
        var verified = await SettleAsync(() => Matches(ops.Read(hwnd), action, req, ops, hwnd), ct).ConfigureAwait(false);
        timer.End("verify");

        if (verified)
        {
            return Done(ActStatus.Confirmed, null, null, risk);
        }

        return action == "close"
            ? Done(ActStatus.Unverified, null, "The close request was sent; the window is still open (the app may be asking to save).", risk)
            : Done(ActStatus.Contradicted, ReasonCodes.ReadbackMismatch, "The window's size, position or state did not read back as requested.", risk);
    }

    private static void Place(IWindowOps ops, long hwnd, string action, WindowManageRequest req, WindowState current)
    {
        if (current.Minimized || current.Maximized) ops.Restore(hwnd);
        var now = ops.Read(hwnd);
        switch (action)
        {
            case "move":
                ops.Place(hwnd, req.X!.Value, req.Y!.Value, now.Width, now.Height);
                break;
            case "resize":
                ops.Place(hwnd, now.X, now.Y, req.Width!.Value, req.Height!.Value);
                break;
            case "snap_left":
            {
                var area = ops.WorkAreaOf(hwnd);
                ops.Place(hwnd, area.X, area.Y, area.Width / 2, area.Height);
                break;
            }

            case "snap_right":
            {
                var area = ops.WorkAreaOf(hwnd);
                ops.Place(hwnd, area.X + (area.Width / 2), area.Y, area.Width - (area.Width / 2), area.Height);
                break;
            }
        }
    }

    private static bool Matches(WindowState now, string action, WindowManageRequest req, IWindowOps ops, long hwnd)
    {
        const int tolerance = 2;
        switch (action)
        {
            case "close":
                return !now.Exists;
            case "minimize":
                return now.Exists && now.Minimized;
            case "maximize":
                return now.Exists && now.Maximized;
            case "restore":
                return now.Exists && !now.Minimized && !now.Maximized;
            case "move":
                return now.Exists && Math.Abs(now.X - req.X!.Value) <= tolerance && Math.Abs(now.Y - req.Y!.Value) <= tolerance;
            case "resize":
                return now.Exists && Math.Abs(now.Width - req.Width!.Value) <= tolerance && Math.Abs(now.Height - req.Height!.Value) <= tolerance;
            case "snap_left":
            {
                var area = ops.WorkAreaOf(hwnd);
                return now.Exists && Math.Abs(now.X - area.X) <= tolerance && Math.Abs(now.Width - (area.Width / 2)) <= tolerance;
            }

            case "snap_right":
            {
                var area = ops.WorkAreaOf(hwnd);
                return now.Exists && Math.Abs(now.X - (area.X + (area.Width / 2))) <= tolerance;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Re-reads until the condition holds or SettleMs passes. Bounded polling every 10 ms: a window changes state a
    /// moment after the call, and Win32 has no usable event for it.
    /// </summary>
    private static async Task<bool> SettleAsync(Func<bool> condition, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (sw.ElapsedMilliseconds >= SettleMs) return false;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True for a window id such as <c>w12</c> (the same rule the desktop index uses).</summary>
    internal static bool IsWindowId(string selector) =>
        selector.Length > 1 && selector[0] is 'w' or 'W' && selector.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private async Task<(WindowInfo? Window, ActResult? Failure)> ResolveWindowAsync(string selector, CancellationToken ct)
    {
        var windows = await _desktop.ListWindowsAsync(ct).ConfigureAwait(false);
        var wanted = selector.Trim();

        var matches = windows.Where(w => w.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        // A wN id names exactly one window. If that window is gone, say so: never fall back to process or title matching,
        // or "close w5" could close an unrelated window whose title merely contains "w5" (for example "Draw50.png").
        if (matches.Count == 0 && IsWindowId(wanted))
        {
            return (null, Fail(ActStatus.Blocked, ReasonCodes.NotFound,
                $"Window {ResultFormatter.OneLine(wanted)} no longer exists. Call windows_list for current ids."));
        }

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
        if (matches.Count == 0)
        {
            return (null, Fail(ActStatus.Blocked, ReasonCodes.NotFound, $"No window matches \"{ResultFormatter.OneLine(wanted)}\"."));
        }

        return (null, Fail(ActStatus.Blocked, ReasonCodes.NotFound,
            $"\"{ResultFormatter.OneLine(wanted)}\" matches {matches.Count} windows ({string.Join(", ", matches.Take(6).Select(w => w.Id))}); use a wN id."));
    }

    // ------------------------------------------------------------------ audio

    private async Task<ActResult> SetAudioCoreAsync(int? volume, bool? mute, PhaseTimer timer, InputProgress progress, CancellationToken ct)
    {
        if (volume is null && mute is null)
        {
            return Bad(ReasonCodes.UnsupportedAction, "Give volume (0-100), mute, or both. With neither, audio reads the state.");
        }

        if (volume is < 0 or > 100)
        {
            return Bad(ReasonCodes.UnsupportedAction, "volume must be between 0 and 100.");
        }

        AudioState before;
        try
        {
            before = await Task.Run(_backends.Audio.Get, ct).ConfigureAwait(false);
        }
        catch (NoAudioDeviceException ex)
        {
            return Fail(ActStatus.Blocked, ReasonCodes.NotFound, ex.Message);
        }

        timer.End("resolve");
        if (Precheck(false, ct) is { } stopped) return stopped;
        timer.End("precheck");

        var preview = string.Create(CultureInfo.InvariantCulture, $"volume={volume?.ToString(CultureInfo.InvariantCulture) ?? "-"} mute={mute?.ToString() ?? "-"}");
        var descriptor = Describe("audio", "set", null, before.DeviceName, preview);
        if (await GateAsync(descriptor, timer, ct).ConfigureAwait(false) is { } denied) return denied;
        if (Precheck(false, ct) is { } stoppedAfterGate) return stoppedAfterGate;

        progress.Sent = true;
        await Task.Run(() =>
        {
            if (volume is { } v) _backends.Audio.SetVolume(v);
            if (mute is { } m) _backends.Audio.SetMute(m);
        }, ct).ConfigureAwait(false);
        timer.End("input");

        var after = await Task.Run(_backends.Audio.Get, ct).ConfigureAwait(false);
        timer.End("verify");
        var ok = (volume is not { } want || Math.Abs(after.VolumePercent - want) <= 1) && (mute is not { } wantMute || after.Muted == wantMute);
        return ok
            ? Done(ActStatus.Confirmed, null, null)
            : Done(ActStatus.Contradicted, ReasonCodes.ReadbackMismatch, $"Read back volume {after.VolumePercent}%, muted {after.Muted}.");
    }

    // ------------------------------------------------------------------ media

    private async Task<ActResult> MediaCoreAsync(string action, PhaseTimer timer, InputProgress progress, CancellationToken ct)
    {
        var wanted = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!MediaActions.Contains(wanted))
        {
            return Bad(ReasonCodes.UnsupportedAction, $"action must be one of: {string.Join(", ", MediaActions)}.");
        }

        var before = await _backends.Media.GetStateAsync(ct).ConfigureAwait(false);
        timer.End("resolve");
        if (before is null)
        {
            return Fail(ActStatus.Blocked, ReasonCodes.NotFound, "No app has a media session right now.");
        }

        if (Precheck(false, ct) is { } stopped) return stopped;
        timer.End("precheck");

        var descriptor = Describe("media", wanted, null, null, null);
        if (await GateAsync(descriptor, timer, ct).ConfigureAwait(false) is { } denied) return denied;
        if (Precheck(false, ct) is { } stoppedAfterGate) return stoppedAfterGate;

        // play and pause are idempotent: when the session is already in the wanted state nothing is sent (a play_pause toggle
        // would do the opposite of what was asked).
        if (wanted is "play" or "pause")
        {
            var wantedState = wanted == "play" ? MediaPlayback.Playing : MediaPlayback.Paused;
            if (before == wantedState)
            {
                timer.End("input");
                timer.End("verify");
                return Done(ActStatus.Confirmed, null, wanted == "play" ? "It was already playing; nothing was sent." : "It was already paused; nothing was sent.")
                    with { InputSent = false, Channel = null };
            }

            progress.Sent = true;
            var toggled = await _backends.Media.SendAsync("play_pause", ct).ConfigureAwait(false);
            timer.End("input");
            if (!toggled)
            {
                return Fail(ActStatus.Error, ReasonCodes.UnsupportedAction, "The media session did not accept the command.", sent: true);
            }

            var reached = await SettleAsync(() => _backends.Media.GetStateAsync(ct).GetAwaiter().GetResult() == wantedState, ct).ConfigureAwait(false);
            timer.End("verify");
            return reached
                ? Done(ActStatus.Confirmed, null, null)
                : Done(ActStatus.Unverified, null, $"The command was accepted; the session did not reach \"{wanted}\" in time.");
        }

        progress.Sent = true;
        var accepted = await _backends.Media.SendAsync(wanted, ct).ConfigureAwait(false);
        timer.End("input");
        if (!accepted)
        {
            return Fail(ActStatus.Error, ReasonCodes.UnsupportedAction, "The media session did not accept the command.", sent: true);
        }

        if (wanted is "next" or "previous")
        {
            timer.End("verify");
            return Done(ActStatus.Unverified, null, "The command was accepted; skipping has no state to read back.");
        }

        var changed = await SettleAsync(() => _backends.Media.GetStateAsync(ct).GetAwaiter().GetResult() != before, ct).ConfigureAwait(false);
        timer.End("verify");
        return changed
            ? Done(ActStatus.Confirmed, null, null)
            : Done(ActStatus.Unverified, null, "The command was accepted; the playback state did not change in time.");
    }

    // ------------------------------------------------------------------ clipboard_set

    private async Task<ActResult> ClipboardSetCoreAsync(string text, PhaseTimer timer, InputProgress progress, CancellationToken ct)
    {
        if (text is null)
        {
            return Bad(ReasonCodes.UnsupportedAction, "text is required.");
        }

        if (text.Length > MaxClipboardSetChars)
        {
            return Bad(ReasonCodes.UnsupportedAction, $"text is limited to {MaxClipboardSetChars} characters.");
        }

        timer.End("resolve");
        if (Precheck(false, ct) is { } stopped) return stopped;
        timer.End("precheck");

        // The text itself never goes to the gate, a log or the run log: only its length (proposal Q7, work order §4).
        var descriptor = Describe("clipboard_set", "set", null, null, $"<{text.Length} chars>");
        if (await GateAsync(descriptor, timer, ct).ConfigureAwait(false) is { } denied) return denied;
        if (Precheck(false, ct) is { } stoppedAfterGate) return stoppedAfterGate;

        progress.Sent = true;
        await _backends.Clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
        timer.End("input");

        var back = await _backends.Clipboard.GetTextAsync(ct).ConfigureAwait(false);
        timer.End("verify");
        return string.Equals(back, text, StringComparison.Ordinal)
            ? Done(ActStatus.Confirmed, null, null)
            : Done(ActStatus.Contradicted, ReasonCodes.ReadbackMismatch, "The clipboard did not read back as set (another app may have changed it).");
    }

    // ------------------------------------------------------------------ shared pipeline

    private async Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct)
    {
        if (_apps is { } cached && Stopwatch.GetElapsedTime(_appsAt).TotalSeconds < AppsCacheSeconds) return cached;

        await _appsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_apps is { } again && Stopwatch.GetElapsedTime(_appsAt).TotalSeconds < AppsCacheSeconds) return again;
            var fresh = await _backends.Apps.ListAsync(ct).ConfigureAwait(false);
            _apps = fresh;
            _appsAt = Stopwatch.GetTimestamp();
            return fresh;
        }
        finally
        {
            _appsLock.Release();
        }
    }

    /// <summary>The stop key (always) and the user-activity pause (only for acts that send real input or change the foreground).</summary>
    private ActResult? Precheck(bool pausesForUser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_kill.IsTripped)
        {
            return ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0));
        }

        if (pausesForUser && _activity.UserActiveRecently)
        {
            return ActResult.Blocked(ReasonCodes.UserActive, "User active recently", Timing.Total(0));
        }

        return null;
    }

    private async Task<ActResult?> GateAsync(ActionDescriptor descriptor, PhaseTimer timer, CancellationToken ct)
    {
        timer.End("precheck");
        var result = await _gate.AuthorizeAsync(descriptor, ct).ConfigureAwait(false);
        timer.End("gate");
        return result.Allowed
            ? null
            : ActResult.Denied(result.Reason ?? ReasonCodes.PolicyForbidden, result.Detail ?? "Denied by safety policy", Timing.Total(0));
    }

    private static ActionDescriptor Describe(string tool, string action, string? process, string? title, string? preview) =>
        new(tool, SystemToolsOptions.EngineName, action, null, process, title, null, preview, null);

    private async Task<ActResult> RunActAsync(
        string op, int timeoutMs, Func<PhaseTimer, InputProgress, CancellationToken, Task<ActResult>> body, CancellationToken ct)
    {
        var timer = new PhaseTimer();
        var progress = new InputProgress();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _kill.Token);
        linked.CancelAfter(timeoutMs);

        ActResult result;
        try
        {
            result = await body(timer, progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CancelResult(progress, ct);
        }
        catch (SystemToolFault fault)
        {
            result = fault.Result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            result = progress.Sent
                ? new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted, Short(ex), null, Channels.Api, Timing.Total(0))
                : ActResult.Failed(false, ReasonCodes.NotFound, Short(ex), Timing.Total(0));
        }

        var timing = timer.ToTiming();
        RecordMetric(op, timing);
        return result with { Timing = timing, Channel = result.Channel ?? (result.InputSent ? Channels.Api : null) };
    }

    private async Task<SystemOutcome<T>> RunReadAsync<T>(
        string op, int timeoutMs, Func<PhaseTimer, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        var timer = new PhaseTimer();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _kill.Token);
        linked.CancelAfter(timeoutMs);

        try
        {
            var value = await body(timer, linked.Token).ConfigureAwait(false);
            var timing = timer.ToTiming();
            RecordMetric(op, timing);
            return SystemOutcome<T>.Success(value, timing);
        }
        catch (OperationCanceledException)
        {
            return Finish(CancelResult(new InputProgress(), ct));
        }
        catch (SystemToolFault fault)
        {
            return Finish(fault.Result);
        }
        catch (NoAudioDeviceException ex)
        {
            return Finish(Fail(ActStatus.Blocked, ReasonCodes.NotFound, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Finish(ActResult.Failed(false, ReasonCodes.NotFound, Short(ex), Timing.Total(0)));
        }

        SystemOutcome<T> Finish(ActResult failure)
        {
            var timing = timer.ToTiming();
            RecordMetric(op, timing);
            return SystemOutcome<T>.Fail(failure with { Timing = timing });
        }
    }

    private ActResult CancelResult(InputProgress progress, CancellationToken callerToken)
    {
        if (_kill.IsTripped || _kill.Token.IsCancellationRequested)
        {
            return progress.Sent
                ? new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted, "OmniWin stop key tripped during the call", null, Channels.Api, Timing.Total(0))
                : ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0));
        }

        if (callerToken.IsCancellationRequested)
        {
            return progress.Sent
                ? new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted, "The call was cancelled after it started", null, Channels.Api, Timing.Total(0))
                : ActResult.Failed(false, ReasonCodes.EngineTimeout, "The call was cancelled before anything was sent.", Timing.Total(0));
        }

        return new ActResult(ActStatus.Error, progress.Sent, ReasonCodes.EngineTimeout,
            "The system call did not finish within its time limit.", null, progress.Sent ? Channels.Api : null, Timing.Total(0));
    }

    private void RecordMetric(string op, Timing timing)
    {
        // Gate and confirm time are excluded (contract §15).
        var gate = timing.PhasesMs.TryGetValue("gate", out var g) ? g : 0;
        _metrics.Record(op, Math.Max(0, timing.TotalMs - gate));
    }

    private static T Unwrap<T>(SystemOutcome<T> outcome) =>
        outcome.Failure is { } failure ? throw new SystemToolFault(failure) : outcome.Value!;

    private static string Short(Exception ex) => ResultFormatter.OneLine(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);

    private static ActResult Bad(string reason, string detail) => ActResult.Failed(false, reason, detail, Timing.Total(0));

    private static ActResult Fail(ActStatus status, string reason, string detail, bool sent = false, RiskClass? risk = null) =>
        new(status, sent, reason, detail, null, sent ? Channels.Api : null, Timing.Total(0), Risk: risk);

    private static ActResult Done(ActStatus status, string? reason, string? detail, RiskClass? risk = null) =>
        new(status, true, reason, detail, null, Channels.Api, Timing.Total(0), Risk: risk);
}
