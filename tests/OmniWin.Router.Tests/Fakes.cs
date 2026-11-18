using OmniWin.Core;
using OmniWin.Server.Router;
using OmniWin.Workflows;

namespace OmniWin.Router.Tests;

internal sealed class NullMetrics : IMetrics
{
    public void Record(string op, double ms)
    {
    }

    public MetricsSnapshot Snapshot() => new(new Dictionary<string, (double, double, double, long)>());
}

internal sealed class AllowGate : ISafetyGate
{
    public bool Deny { get; set; }

    public RiskClass Classify(ActionDescriptor a) => RiskClass.Normal;

    public Task<GateResult> AuthorizeAsync(ActionDescriptor a, CancellationToken ct) =>
        Task.FromResult(Deny ? new GateResult(false, RiskClass.Forbidden, ReasonCodes.PolicyForbidden, false, "denied by the test")
            : new GateResult(true, RiskClass.Normal, null, false));
}

internal sealed class FreeKill : IKillSwitch
{
    public bool IsTripped => false;

    public CancellationToken Token => CancellationToken.None;
}

/// <summary>Records every call so a test can say exactly what was (and was not) sent.</summary>
internal sealed class FakeSystem : ISystemTools
{
    public List<string> Media { get; } = [];
    public List<(int? Volume, bool? Mute)> AudioSets { get; } = [];
    public List<string> Launches { get; } = [];
    public List<WindowManageRequest> WindowActions { get; } = [];
    public int AudioReads;
    public AudioState Audio { get; set; } = new(40, false, "Speakers");
    public List<InstalledApp> Apps { get; } = [new("Notepad", "notepad!app", null), new("Calculator", "calc!app", null), new("Spotify", "spotify!app", null)];

    public int Total => Media.Count + AudioSets.Count + Launches.Count + WindowActions.Count;

    private static ActResult Done() => new(ActStatus.Confirmed, true, null, null, null, Channels.Api, Timing.Total(1));

    public Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string? filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledApp>>(Apps);

    public Task<ActResult> LaunchAppAsync(string app, string? args, ExpectSpec? expect, CancellationToken ct)
    {
        Launches.Add(app);
        return Task.FromResult(Done());
    }

    public Task<ActResult> WindowManageAsync(WindowManageRequest req, CancellationToken ct)
    {
        WindowActions.Add(req);
        return Task.FromResult(Done());
    }

    public Task<AudioState> GetAudioAsync(CancellationToken ct)
    {
        AudioReads++;
        return Task.FromResult(Audio);
    }

    public Task<ActResult> SetAudioAsync(int? volumePercent, bool? mute, CancellationToken ct)
    {
        AudioSets.Add((volumePercent, mute));
        return Task.FromResult(Done());
    }

    public Task<ActResult> MediaAsync(string action, CancellationToken ct)
    {
        Media.Add(action);
        return Task.FromResult(Done());
    }

    public Task<string> SystemInfoAsync(string kind, CancellationToken ct) => Task.FromResult(kind + " fake");

    public Task<string> ClipboardGetAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ClipboardSetAsync(string text, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeDesktop : IDesktopEngine
{
    public List<WindowInfo> Windows { get; } =
    [
        new("w1", 101, 1, "chrome.exe", "Inbox - Chrome", true, false, false, null, null),
        new("w2", 102, 2, "notepad.exe", "Untitled - Notepad", false, false, false, null, null),
        new("w3", 103, 3, "Spotify.exe", "Spotify", false, false, false, null, null),
    ];

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WindowInfo>>(Windows);

    public Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) => throw new NotSupportedException();

    public Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();

    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) => throw new NotSupportedException();

    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeWorkflows : IWorkflowCatalog
{
    public List<WorkflowSummary> Saved { get; } =
    [
        new("morning_setup", "opens the usual windows", 4, [], DateTimeOffset.UtcNow),
        new("save_draft", "saves a draft", 3, ["title"], DateTimeOffset.UtcNow),
    ];

    public List<string> Runs { get; } = [];

    public IReadOnlyList<WorkflowSummary> List() => Saved;

    public Task<string> RunAsync(string name, CancellationToken ct)
    {
        Runs.Add(name);
        return Task.FromResult("run status=ok");
    }
}

internal sealed class FakeClassifier(IntentGuess? answer) : IIntentClassifier
{
    public int Calls;

    public Task<IntentGuess?> ClassifyAsync(string text, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(answer);
    }
}
