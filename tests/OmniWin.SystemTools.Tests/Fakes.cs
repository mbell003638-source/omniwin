using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

internal sealed class FakeGate(List<string> log) : ISafetyGate
{
    public List<ActionDescriptor> Seen { get; } = [];

    public Func<ActionDescriptor, GateResult>? Decide { get; set; }

    public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
    {
        Seen.Add(a);
        log.Add("gate");
        return Task.FromResult(Decide?.Invoke(a) ?? new GateResult(true, RiskClass.Normal, null, false));
    }
}

internal sealed class FakeKill : IKillSwitch
{
    private readonly CancellationTokenSource _cts = new();

    public bool IsTripped { get; set; }

    public CancellationToken Token => _cts.Token;

    public void Trip()
    {
        IsTripped = true;
        _cts.Cancel();
    }
}

internal sealed class FakeActivity : IUserActivity
{
    public bool UserActiveRecently { get; set; }

    public DateTimeOffset LastRealInput => DateTimeOffset.UtcNow;
}

internal sealed class FakeMetrics : IMetrics
{
    public List<(string Op, double Ms)> Records { get; } = [];

    public void Record(string op, double ms)
    {
        lock (Records) Records.Add((op, ms));
    }

    public MetricsSnapshot Snapshot() => new(new Dictionary<string, (double, double, double, long)>());
}

internal sealed class FakeDesktop : IDesktopEngine
{
    public List<WindowInfo> Windows { get; set; } = [];

    public Func<Expectation, WaitResult>? OnWait { get; set; }

    /// <summary>Called on each list; lets a test add a window after the launch.</summary>
    public Action<int>? OnList { get; set; }

    private int _lists;

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        OnList?.Invoke(++_lists);
        return Task.FromResult<IReadOnlyList<WindowInfo>>(Windows.ToList());
    }

    public Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct) =>
        Task.FromResult(OnWait?.Invoke(condition) ?? new WaitResult(true, null, null, Timing.Total(0)));

    public Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) => throw new NotSupportedException();

    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeForeground(List<string> log) : IForegroundService, IForegroundDetails
{
    public bool Result { get; set; } = true;

    /// <summary>When true the fake reports that the F24 key was tapped during the attempt.</summary>
    public bool KeyTapped { get; set; }

    /// <summary>Whether the fake attempt sent any input (a window that was already in front needs none).</summary>
    public bool InputSent { get; set; } = true;

    /// <summary>When set, the detailed call throws this (a cancelled call).</summary>
    public Exception? Throws { get; set; }

    /// <summary>When set, the attempt was stopped or refused by a check, with this status, reason and detail.</summary>
    public ActStatus? StopStatus { get; set; }

    public string? StopReason { get; set; }

    public string? StopDetail { get; set; }

    public Task<bool> BringToForegroundAsync(long hwnd, CancellationToken ct) =>
        BringToForegroundDetailedAsync(hwnd, ct).ContinueWith(t => t.Result.Verified, ct);

    public Task<ForegroundAttempt> BringToForegroundDetailedAsync(long hwnd, CancellationToken ct)
    {
        log.Add("input:focus");
        if (Throws is { } error) return Task.FromException<ForegroundAttempt>(error);
        return Task.FromResult(new ForegroundAttempt(Result, InputSent, KeyTapped, StopStatus, StopReason, StopDetail));
    }
}

/// <summary>A foreground service that can only answer yes or no (it cannot say what it sent).</summary>
internal sealed class YesNoForeground(List<string> log) : IForegroundService
{
    public bool Result { get; set; }

    public Task<bool> BringToForegroundAsync(long hwnd, CancellationToken ct)
    {
        log.Add("input:focus");
        return Task.FromResult(Result);
    }
}

internal sealed class FakeApps : IAppCatalog
{
    public List<InstalledApp> Apps { get; } =
    [
        new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null),
        new("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", null),
        new("Windows Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", null),
        new("Windows PowerShell", "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe", null),
        new("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App", null),
        new("Paint 3D", "Microsoft.MSPaint_8wekyb3d8bbwe!Microsoft.MSPaint", null),
    ];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<InstalledApp>> ListAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<InstalledApp>>(Apps.ToList());
    }
}

internal sealed class FakeLauncher(List<string> log) : IShellLauncher
{
    public Func<CancellationToken, Task>? Hook { get; set; }

    public bool ExecutableStarts { get; set; } = true;

    public List<string> Started { get; } = [];

    public async Task StartAppAsync(string launchId, CancellationToken ct)
    {
        log.Add("input:launch");
        Started.Add("app:" + launchId);
        if (Hook is not null) await Hook(ct).ConfigureAwait(false);
    }

    public Task<bool> StartExecutableAsync(string path, string? args, CancellationToken ct)
    {
        log.Add("input:launch");
        Started.Add($"exe:{path}|{args}");
        return Task.FromResult(ExecutableStarts);
    }
}

internal sealed class FakeWindowOps(List<string> log) : IWindowOps
{
    public Dictionary<long, WindowState> States { get; } = new();

    public WorkArea Area { get; set; } = new(0, 0, 1920, 1040);

    /// <summary>When true the window ignores every request (to prove read-back catches it).</summary>
    public bool IgnoreRequests { get; set; }

    public bool IgnoreClose { get; set; }

    public WindowState Read(long hwnd) => States.TryGetValue(hwnd, out var s) ? s : new WindowState(false, false, false, 0, 0, 0, 0);

    public void Minimize(long hwnd) => Change(hwnd, "minimize", s => s with { Minimized = true, Maximized = false });

    public void Maximize(long hwnd) => Change(hwnd, "maximize", s => s with { Maximized = true, Minimized = false });

    public void Restore(long hwnd) => Change(hwnd, "restore", s => s with { Maximized = false, Minimized = false });

    public void Place(long hwnd, int x, int y, int width, int height) =>
        Change(hwnd, "place", s => s with { X = x, Y = y, Width = width, Height = height });

    public WorkArea WorkAreaOf(long hwnd) => Area;

    public void RequestClose(long hwnd)
    {
        log.Add("input:close");
        if (!IgnoreClose && !IgnoreRequests) States.Remove(hwnd);
    }

    private void Change(long hwnd, string what, Func<WindowState, WindowState> change)
    {
        log.Add("input:" + what);
        if (!IgnoreRequests && States.TryGetValue(hwnd, out var s)) States[hwnd] = change(s);
    }
}

internal sealed class FakeAudio(List<string> log) : IAudioBackend
{
    public int Volume { get; set; } = 40;

    public bool Muted { get; set; }

    public bool NoDevice { get; set; }

    public bool Ignore { get; set; }

    public Func<CancellationToken, Task>? Hook { get; set; }

    public AudioState Get() =>
        NoDevice ? throw new NoAudioDeviceException("There is no default audio output device.") : new AudioState(Volume, Muted, "Test Speakers");

    public void SetVolume(int percent)
    {
        log.Add("input:volume");
        if (!Ignore) Volume = percent;
        Hook?.Invoke(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void SetMute(bool muted)
    {
        log.Add("input:mute");
        if (!Ignore) Muted = muted;
    }
}

internal sealed class FakeMedia(List<string> log) : IMediaBackend
{
    public MediaPlayback? State { get; set; } = MediaPlayback.Playing;

    public bool Accept { get; set; } = true;

    public MediaPlayback? StateAfterSend { get; set; }

    public Task<MediaPlayback?> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

    public Task<bool> SendAsync(string action, CancellationToken ct)
    {
        log.Add("input:media:" + action);
        if (Accept && StateAfterSend is { } next) State = next;
        return Task.FromResult(Accept);
    }
}

internal sealed class FakeInfo : ISystemInfoProvider
{
    public Task<string> GetAsync(string kind, CancellationToken ct) => Task.FromResult("info:" + kind);
}

internal sealed class FakeClipboard(List<string> log) : IClipboardBackend
{
    public string? Text { get; set; } = "hello";

    public bool Corrupt { get; set; }

    public Task<string?> GetTextAsync(CancellationToken ct) => Task.FromResult(Text);

    public Task SetTextAsync(string text, CancellationToken ct)
    {
        log.Add("input:clipboard");
        Text = Corrupt ? text + "!" : text;
        return Task.CompletedTask;
    }
}

/// <summary>A service wired to fakes. Every fake writes to <see cref="Log"/> in call order.</summary>
internal sealed class Harness
{
    public List<string> Log { get; } = [];

    public FakeGate Gate { get; }

    public FakeKill Kill { get; } = new();

    public FakeActivity Activity { get; } = new();

    public FakeMetrics Metrics { get; } = new();

    public FakeDesktop Desktop { get; } = new();

    public FakeForeground Foreground { get; }

    public FakeApps Apps { get; } = new();

    public FakeLauncher Launcher { get; }

    public FakeWindowOps Windows { get; }

    public FakeAudio Audio { get; }

    public FakeMedia Media { get; }

    public FakeInfo Info { get; } = new();

    public FakeClipboard Clipboard { get; }

    public SystemToolsService Service { get; }

    public Harness(SystemToolsOptions? options = null, IForegroundService? foreground = null)
    {
        Gate = new FakeGate(Log);
        Foreground = new FakeForeground(Log);
        Launcher = new FakeLauncher(Log);
        Windows = new FakeWindowOps(Log);
        Audio = new FakeAudio(Log);
        Media = new FakeMedia(Log);
        Clipboard = new FakeClipboard(Log);
        Service = new SystemToolsService(Gate, Kill, Activity, Metrics, Desktop, foreground ?? Foreground, options ?? new SystemToolsOptions(),
            new SystemBackends(Apps, Launcher, Windows, Audio, Media, Info, Clipboard));
    }

    public WindowInfo AddWindow(string id, long hwnd, string process, string title, string? inaccessible = null, int pid = 4242)
    {
        var info = new WindowInfo(id, hwnd, pid, process, title, false, false, false, null, inaccessible);
        Desktop.Windows.Add(info);
        Windows.States[hwnd] = new WindowState(true, false, false, 100, 100, 800, 600);
        return info;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
