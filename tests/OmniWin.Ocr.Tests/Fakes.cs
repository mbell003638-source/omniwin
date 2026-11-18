using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

internal sealed class FakeGate : ISafetyGate
{
    public List<ActionDescriptor> Seen { get; } = [];

    public Func<ActionDescriptor, GateResult>? Decide { get; set; }

    public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct)
    {
        Seen.Add(a);
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

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WindowInfo>>(Windows.ToList());

    public Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();

    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) => throw new NotSupportedException();

    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeCapture : IScreenCapture
{
    public Dictionary<long, ScreenRect> Rects { get; } = new();

    public CapturedImage? Image { get; set; } = new([1, 2, 3], 400, 100, 1000, 500);

    public int WindowCaptures { get; private set; }

    public int RegionCaptures { get; private set; }

    public IReadOnlyList<ScreenRect>? LastMasks { get; private set; }

    /// <summary>Runs right after each capture (the window "moves" while the capture was being taken).</summary>
    public Action? AfterCapture { get; set; }

    public Func<int, int, long> WindowAt { get; set; } = (_, _) => 0;

    public ScreenRect VirtualScreen => new(0, 0, 3840, 1080);

    public ScreenRect? WindowRect(long hwnd) => Rects.TryGetValue(hwnd, out var r) ? r : null;

    public long TopLevelWindowAt(int x, int y) => WindowAt(x, y);

    public CapturedImage? CaptureWindow(long hwnd)
    {
        WindowCaptures++;
        var image = Image;
        AfterCapture?.Invoke();
        return image;
    }

    public CapturedImage? CaptureRegion(ScreenRect region, IReadOnlyList<ScreenRect> maskOut)
    {
        RegionCaptures++;
        LastMasks = maskOut;
        var image = Image is null ? null : Image with { OriginX = region.X, OriginY = region.Y };
        AfterCapture?.Invoke();
        return image;
    }
}

internal sealed class FakeEngine : IOcrEngine
{
    public int MaxImageDimension { get; set; } = 10_000;

    public List<RecognizedLine> Lines { get; set; } =
    [
        new("Save As", [new RecognizedWord("Save", 10, 20, 40, 14), new RecognizedWord("As", 60, 20, 20, 14)]),
        new("Cancel", [new RecognizedWord("Cancel", 200, 60, 50, 14)]),
    ];

    public Func<CancellationToken, Task>? Hook { get; set; }

    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public async Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] bmp, CancellationToken ct)
    {
        Calls++;
        if (Throw is not null) throw Throw;
        if (Hook is not null) await Hook(ct).ConfigureAwait(false);
        return Lines;
    }
}

internal sealed class Setup
{
    public FakeGate Gate { get; } = new();

    public FakeKill Kill { get; } = new();

    public FakeMetrics Metrics { get; } = new();

    public FakeDesktop Desktop { get; } = new();

    public FakeCapture Capture { get; } = new();

    public FakeEngine Engine { get; } = new();

    public OcrRefStore Refs { get; } = new();

    public OcrService Service { get; }

    public Setup(OcrOptions? options = null)
    {
        Service = new OcrService(Gate, Kill, Metrics, Desktop, Refs, options ?? new OcrOptions(), Capture, Engine);
    }

    public WindowInfo AddWindow(string id, long hwnd, string process, string title, bool foreground = false,
        string? inaccessible = null, bool minimized = false, int pid = 4242, ScreenRect? rect = null)
    {
        var info = new WindowInfo(id, hwnd, pid, process, title, foreground, minimized, false, null, inaccessible);
        Desktop.Windows.Add(info);
        Capture.Rects[hwnd] = rect ?? new ScreenRect(1000, 500, 400, 100);
        return info;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
