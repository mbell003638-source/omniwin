using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Interop.UIAutomationClient;
using Microsoft.Extensions.Logging.Abstractions;
using OmniWin.Core;
using OmniWin.Safety;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace OmniWin.Desktop.Tests;

[Collection("Desktop input")]
[Trait("Category", "UiInputDiagnostic")]
public sealed class DesktopPointerDiagnosticTests
{
    public static bool DiagnosticsEnabled => Environment.GetEnvironmentVariable("OMNIWIN_POINTER_DIAGNOSTICS") == "1";

    [Fact(Skip = "Set OMNIWIN_POINTER_DIAGNOSTICS=1 on an idle interactive desktop.", SkipUnless = nameof(DiagnosticsEnabled))]
    public Task Compare_atomic_and_split_pointer_batches_with_and_without_activity_hooks()
    {
        var output = TestContext.Current.TestOutputHelper;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(line => output?.WriteLine(line));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "omniwin-pointer-diagnostics" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Run(Action<string> output)
    {
        using var mutex = new Mutex(false, @"Local\OmniWin.UiInputTestLock");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            Assert.True(acquired, "Another test holds the desktop input lock.");
            Measure(output);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void Measure(Action<string> output)
    {
        const int warmups = 10;
        const int measured = 200;
        var samples = new List<Sample>();
        var started = DateTimeOffset.UtcNow;
        var resetFocus = Environment.GetEnvironmentVariable("OMNIWIN_POINTER_RESET_FOCUS") == "1";
        var focusSource = (Environment.GetEnvironmentVariable("OMNIWIN_POINTER_FOCUS_SOURCE") ?? "editor")
            .Trim().ToLowerInvariant();
        var config = new OmniWinConfig { UserActivityCooldownMs = 500 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var worker = new WorkerLifetime(new UiaWorker("uia-pointer-diagnostics", config.Timeouts));
        var withUiaEvents = Environment.GetEnvironmentVariable("OMNIWIN_POINTER_UIA_EVENTS") == "1";
        using var eventLifetime = withUiaEvents ? new EventLifetime(new UiaEvents(config.Timeouts)) : null;
        if (eventLifetime is not null)
        {
            var registrationStarted = Stopwatch.GetTimestamp();
            while (!eventLifetime.Value.Available && Stopwatch.GetElapsedTime(registrationStarted) < TimeSpan.FromSeconds(5))
                deadline.Token.WaitHandle.WaitOne(25);
            Assert.True(eventLifetime.Value.Available, "UIA event registration failed.");
        }
        Process? fixture = null;
        Task<string>? fixtureOutput = null;
        string? fixtureTrace = null;
        Probe? probe = null; // Created and accessed only by the dedicated MTA worker.
        string? failure = null;
        var expectedCount = 0;
        var complete = false;
        try
        {
            Assert.True(focusSource is "editor" or "button", "OMNIWIN_POINTER_FOCUS_SOURCE must be editor or button.");
            var title = "OmniWin pointer diagnostics " + Guid.NewGuid().ToString("N");
            var launch = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, RedirectStandardOutput = true };
            launch.ArgumentList.Add("--title");
            launch.ArgumentList.Add(title);
            fixture = Process.Start(launch) ?? throw new InvalidOperationException("Fixture launch failed.");
            Assert.Equal("OMNIWIN_FIXTURE_READY", fixture.StandardOutput.ReadLineAsync(deadline.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), deadline.Token).GetAwaiter().GetResult());
            // Drain closure-only traces concurrently so a large report cannot block FormClosed.
            fixtureOutput = fixture.StandardOutput.ReadToEndAsync();
            var pid = fixture.Id;
            var processName = fixture.ProcessName + ".exe";
            worker.Value.InvokeAsync((uia, ct) =>
            {
                probe = new Probe(uia, pid, processName, title, ct);
                return true;
            }, deadline.Token).GetAwaiter().GetResult();

            // Repeat the on condition after off to expose time/order drift. Pooled on/off each have 200 of each kind.
            bool[] modes = [true, false, true];
            int[] counts = [measured / 2, measured, measured / 2];
            for (var block = 0; block < modes.Length; block++)
            {
                var hooks = modes[block];
                var blockName = block switch { 0 => "hooks_on_1", 1 => "hooks_off_1", _ => "hooks_on_2" };
                var blockCount = counts[block];
                using var safety = new KillSwitch(config, NullLogger<KillSwitch>.Instance,
                    startThread: true, installActivityHooks: hooks);
                Assert.Equal("armed", safety.State);
                Assert.Equal(hooks ? "on" : "disabled_for_test", safety.ActivityStatus.State);
                var gate = new SafetyGate(config, safety);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, safety.Token);
                var ct = stop.Token;
                while (hooks && safety.UserActiveRecently)
                {
                    ct.ThrowIfCancellationRequested();
                    ct.WaitHandle.WaitOne(50);
                }
                if (block == 0)
                {
                    // A checked click on our own visible Apply button can acquire foreground permission.
                    var activation = worker.Value.InvokeAsync((_, token) =>
                    {
                        if (probe!.IsForeground) return (InputReceipt?)null;
                        var point = probe.AuthorizeAndCheck(safety, gate, hooks, token, requireForeground: false);
                        return NativeInput.Click(point, right: false, doubleClick: false);
                    }, ct).GetAwaiter().GetResult();
                    if (activation is { } receipt)
                    {
                        Assert.True(receipt.Complete, "Fixture activation click was incomplete.");
                        expectedCount++;
                        worker.Value.InvokeAsync((_, token) => { probe!.WaitForCount(expectedCount, token); return true; }, ct)
                            .GetAwaiter().GetResult();
                    }
                }

                for (var i = 0; i < warmups + blockCount; i++)
                {
                    // Alternate order within each pair, rather than measuring all normal sends first.
                    var splitFirst = (i + block) % 2 != 0;
                    Click(splitFirst);
                    Click(!splitFirst);
                    if (i == warmups - 1 || (i - warmups + 1) % 50 == 0)
                        output($"{blockName}: {Math.Max(0, i - warmups + 1)}/{blockCount} measured normal/split pairs.");

                    void Click(bool split)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (resetFocus)
                            worker.Value.InvokeAsync((_, token) => { probe!.FocusSource(focusSource, safety, gate, hooks, token); return true; }, ct)
                                .GetAwaiter().GetResult();
                        var index = samples.Count;
                        samples.Add(new Sample(blockName, hooks, i, i < warmups, split ? "split" : "normal",
                            null, null, null, 0, 0, false, expectedCount + 1));
                        if (split)
                        {
                            var move = worker.Value.InvokeAsync((_, token) =>
                            {
                                var point = probe!.AuthorizeAndCheck(safety, gate, hooks, token);
                                return NativeInput.DiagnosticMove(point);
                            }, ct).GetAwaiter().GetResult();
                            samples[index] = samples[index] with { MoveMs = move.DispatchMs, Sent = move.Sent, Requested = move.Requested };
                            Assert.True(move.Complete, "Pointer-only input batch was incomplete.");
                            var buttons = worker.Value.InvokeAsync((_, token) =>
                            {
                                var point = probe!.AuthorizeAndCheck(safety, gate, hooks, token);
                                // A button-only batch acts at the actual cursor, so validate it after the move.
                                Assert.Equal(point, NativeDesktop.CursorPosition());
                                return NativeInput.DiagnosticButtons();
                            }, ct).GetAwaiter().GetResult();
                            samples[index] = samples[index] with
                            {
                                ButtonsMs = buttons.DispatchMs, Sent = move.Sent + buttons.Sent,
                                Requested = move.Requested + buttons.Requested,
                            };
                            Assert.True(buttons.Complete, "Button-only input batch was incomplete.");
                        }
                        else
                        {
                            var click = worker.Value.InvokeAsync((_, token) =>
                            {
                                var point = probe!.AuthorizeAndCheck(safety, gate, hooks, token);
                                return NativeInput.Click(point, right: false, doubleClick: false);
                            }, ct).GetAwaiter().GetResult();
                            samples[index] = samples[index] with { AtomicMs = click.DispatchMs, Sent = click.Sent, Requested = click.Requested };
                            Assert.True(click.Complete, "Atomic click input batch was incomplete.");
                        }
                        expectedCount++;
                        worker.Value.InvokeAsync((_, token) => { probe!.WaitForCount(expectedCount, token); return true; }, ct)
                            .GetAwaiter().GetResult();
                        samples[index] = samples[index] with { Verified = true };
                    }
                }
            }
            complete = true;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            try
            {
                if (fixture is not null && !fixture.HasExited)
                {
                    fixture.CloseMainWindow();
                    if (!fixture.WaitForExit(3000)) fixture.Kill(entireProcessTree: true);
                }
                if (fixtureOutput is not null)
                {
                    var remainingOutput = fixtureOutput.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    const string prefix = "OMNIWIN_INPUT_TRACE ";
                    var traces = remainingOutput.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    if (Environment.GetEnvironmentVariable("OMNIWIN_FIXTURE_TRACE_INPUT") == "1")
                    {
                        Assert.Single(traces);
                        fixtureTrace = traces[0][prefix.Length..];
                        using var parsed = JsonDocument.Parse(fixtureTrace);
                        Assert.True(parsed.RootElement.GetProperty("Count").GetInt32() > 0, "The fixture trace is empty.");
                        Assert.Equal(0, parsed.RootElement.GetProperty("Dropped").GetInt32());
                    }
                }
            }
            catch (Exception ex)
            {
                complete = false;
                failure = (failure is null ? string.Empty : failure + "\n") + "Fixture cleanup/trace capture: " + ex;
                throw;
            }
            finally
            {
                fixture?.Dispose();
                WriteReport(output, samples, started, warmups, measured, expectedCount, complete, failure, fixtureTrace,
                    resetFocus, focusSource);
            }
        }
    }

    private static void WriteReport(Action<string> output, List<Sample> samples, DateTimeOffset started,
        int warmups, int measured, int clickCount, bool complete, string? failure, string? fixtureTrace,
        bool resetFocus, string focusSource)
    {
        var series = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var sample in samples.Where(sample => !sample.Warmup && sample.Verified))
        {
            foreach (var prefix in new[] { sample.Block, sample.ActivityHooks ? "hooks_on" : "hooks_off" })
            {
                if (sample.AtomicMs is { } atomic) Add(prefix + ".normal", atomic);
                if (sample.MoveMs is { } move) Add(prefix + ".move", move);
                if (sample.ButtonsMs is { } buttons) Add(prefix + ".buttons", buttons);
                if (sample.MoveMs is { } m && sample.ButtonsMs is { } b) Add(prefix + ".split_sum", m + b);
            }
        }
        var summaries = series.ToDictionary(pair => pair.Key, pair => Summary.From(pair.Value), StringComparer.Ordinal);
        foreach (var (name, value) in summaries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            output($"{name} p50={value.P50:F3}ms p95={value.P95:F3}ms p99={value.P99:F3}ms n={value.Count}");
        var report = new
        {
            StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name,
            Binaries = BenchmarkArtifacts.Hashes(FixturePath()),
            WarmupPairsPerBlock = warmups, MeasuredPairsPerHookCondition = measured,
            BlockPlan = new[] { new { Name = "hooks_on_1", MeasuredPairs = measured / 2 },
                new { Name = "hooks_off_1", MeasuredPairs = measured }, new { Name = "hooks_on_2", MeasuredPairs = measured / 2 } },
            StopHotkeyRegisteredInAllBlocks = true, UserActivityCooldownMs = 500,
            UiaEventsEnabled = Environment.GetEnvironmentVariable("OMNIWIN_POINTER_UIA_EVENTS") == "1",
            ResetFocusBeforeEachClick = resetFocus,
            PreClickFocusSource = resetFocus ? focusSource : "none",
            FocusSourceControl = resetFocus ? focusSource == "button" ? "Uncover target" : "Editor" : null,
            ResetEditorFocusBeforeEachClick = resetFocus && focusSource == "editor",
            Completed = complete, Failure = failure, ExpectedFixtureClickCount = clickCount,
            FixtureTraceCaptured = fixtureTrace is not null,
            VerifiedClickSamples = samples.Count(sample => sample.Verified),
            TimingScope = "Actual SendInput call durations. Split sum excludes the fresh checks between its two calls. Readback is outside all measured calls.",
            Summaries = summaries, Samples = samples,
        };
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("OMNIWIN_POINTER_DIAGNOSTICS_OUTPUT") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults", "desktop-pointer-diagnostics.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (fixtureTrace is not null)
        {
            var tracePath = Path.ChangeExtension(path, ".fixture-trace.json");
            File.WriteAllText(tracePath, fixtureTrace);
            output($"Fixture WndProc trace: {tracePath}");
        }
        output($"Raw pointer diagnostic report: {path}");

        void Add(string name, double elapsed)
        {
            if (!series.TryGetValue(name, out var values)) series[name] = values = [];
            values.Add(elapsed);
        }
    }

    private sealed class Probe
    {
        private readonly IUIAutomation _uia;
        private readonly IUIAutomationElement _window;
        private readonly IUIAutomationElement _button;
        private readonly IUIAutomationElement _counter;
        private readonly IUIAutomationElement _deletions;
        private readonly int[] _identity;
        private readonly long _hwnd;
        private readonly long _buttonHwnd;
        private readonly int _pid;
        private readonly string _process;
        private readonly string _title;

        internal Probe(IUIAutomation uia, int pid, string process, string title, CancellationToken ct)
        {
            _uia = uia;
            _pid = pid;
            _process = process;
            _title = title;
            ct.ThrowIfCancellationRequested();
            var condition = uia.CreateAndCondition(uia.CreatePropertyCondition(UIA_ProcessIdPropertyId, pid),
                uia.CreatePropertyCondition(UIA_NamePropertyId, title));
            IUIAutomationElement? window = null;
            var start = Stopwatch.GetTimestamp();
            while (window is null && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(15))
            {
                ct.ThrowIfCancellationRequested();
                window = uia.GetRootElement().FindFirst(TreeScope.TreeScope_Children, condition);
                if (window is null) ct.WaitHandle.WaitOne(25);
            }
            _window = window ?? throw new InvalidOperationException("Owned fixture window is unavailable in UIA.");
            _hwnd = (long)_window.CurrentNativeWindowHandle;
            _button = Find("Apply");
            _buttonHwnd = (long)_button.CurrentNativeWindowHandle;
            _identity = _button.GetRuntimeId();
            _counter = Find("Applied 0");
            _deletions = Find("Delete count 0");
            Assert.NotEqual(0, _buttonHwnd);
            Assert.True(_identity.Length > 0);
            Assert.Equal(pid, NativeDesktop.ProcessId(_hwnd));
        }

        internal bool IsForeground => NativeDesktop.Foreground == _hwnd;

        internal void FocusSource(string source, KillSwitch safety, SafetyGate gate, bool hooks, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(_pid, NativeDesktop.ProcessId(_hwnd));
            Assert.True(NativeDesktop.OnInputDesktop());
            Assert.Equal(_hwnd, NativeDesktop.Foreground);
            // SetFocus on this existing nondelete button has no Click/Invoke side effect.
            var isButton = source == "button";
            var name = isButton ? "Uncover target" : "Editor";
            var element = Find(name);
            Assert.Equal(_pid, element.CurrentProcessId);
            Assert.Equal(name, element.CurrentName);
            Assert.Equal(isButton ? UIA_ButtonControlTypeId : UIA_EditControlTypeId, element.CurrentControlType);
            Assert.NotEqual(0, element.CurrentIsEnabled);
            Assert.Equal(0, element.CurrentIsOffscreen);
            var native = (long)element.CurrentNativeWindowHandle;
            Assert.NotEqual(0, native);
            Assert.Equal(_pid, NativeDesktop.ProcessId(native));
            Assert.Equal(_hwnd, NativeDesktop.Root(native));
            var target = new ElementDescriptor(isButton ? "fixture-focus-button" : "fixture-editor", "desktop", _process, _title, null,
                isButton ? "button" : "edit", name, element.CurrentAutomationId, false, true, []);
            var authorization = gate.AuthorizeAsync(new ActionDescriptor("desktop_act", "desktop", "focus", target,
                _process, _title, null, null, null), ct).GetAwaiter().GetResult();
            Assert.True(authorization.Allowed, authorization.Detail ?? authorization.Reason);
            ct.ThrowIfCancellationRequested();
            Assert.False(safety.IsTripped || hooks && safety.UserActiveRecently, "Real input or stop key interrupted focus setup.");
            Assert.Equal(_hwnd, NativeDesktop.Foreground);
            Assert.True(NativeDesktop.OnInputDesktop() && NativeDesktop.Enabled(_hwnd));
            element.SetFocus();
            Assert.True(_uia.CompareElements(element, _uia.GetFocusedElement()) != 0);
        }

        internal Point AuthorizeAndCheck(KillSwitch safety, SafetyGate gate, bool hooks, CancellationToken ct, bool requireForeground = true)
        {
            CheckBoundary();
            Assert.Equal(_pid, _window.CurrentProcessId);
            Assert.Equal(_title, _window.CurrentName);
            var current = _uia.ElementFromHandle((nint)_buttonHwnd);
            Assert.NotNull(current);
            Assert.True(_uia.CompareElements(_button, current) != 0 && current.GetRuntimeId().SequenceEqual(_identity),
                "The owned Apply control identity changed.");
            Assert.Equal(_pid, current.CurrentProcessId);
            Assert.Equal(UIA_ButtonControlTypeId, current.CurrentControlType);
            Assert.Equal("Apply", current.CurrentName);
            Assert.Equal("Apply", current.CurrentAutomationId);
            Assert.NotEqual(0, current.CurrentIsEnabled);
            Assert.Equal(0, current.CurrentIsOffscreen);
            Assert.True(current.GetClickablePoint(out var candidate) != 0, "Apply has no clickable point.");
            var point = new Point(candidate.x, candidate.y);
            var bounds = current.CurrentBoundingRectangle;
            Assert.True(NativeDesktop.Contains(NativeDesktop.Rect(bounds.left, bounds.top, bounds.right, bounds.bottom), point));
            var descriptor = new ElementDescriptor("fixture-apply", "desktop", _process, _title, null,
                "button", current.CurrentName, current.CurrentAutomationId, false, true, []);
            var authorization = gate.AuthorizeAsync(new ActionDescriptor("desktop_act", "desktop", "click", descriptor,
                _process, _title, null, null, null), ct).GetAwaiter().GetResult();
            Assert.True(authorization.Allowed, authorization.Detail ?? authorization.Reason);
            var hit = _uia.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y });
            Assert.True(hit is not null && _uia.CompareElements(current, hit) != 0, "The UIA point no longer hits Apply.");
            CheckBoundary();
            var nativeHit = NativeDesktop.WindowAt(point);
            Assert.True(NativeDesktop.Root(nativeHit) == _hwnd && NativeDesktop.IsWithin(_buttonHwnd, nativeHit),
                "The input point is covered or no longer belongs to Apply.");
            Assert.Equal(_pid, NativeDesktop.ProcessId(nativeHit));
            return point;

            void CheckBoundary()
            {
                ct.ThrowIfCancellationRequested();
                Assert.False(safety.IsTripped, "The real stop key stopped pointer diagnostics.");
                Assert.False(hooks && safety.UserActiveRecently, "Real input interrupted pointer diagnostics.");
                Assert.True(NativeDesktop.OnInputDesktop(), "The input desktop changed.");
                Assert.True(NativeDesktop.IsTopLevelWindow(_hwnd) && NativeDesktop.IsWindow(_buttonHwnd));
                Assert.Equal(_pid, NativeDesktop.ProcessId(_hwnd));
                Assert.Equal(_pid, NativeDesktop.ProcessId(_buttonHwnd));
                Assert.Equal(_hwnd, NativeDesktop.Root(_buttonHwnd));
                Assert.Equal(_title, NativeDesktop.Title(_hwnd));
                Assert.True(NativeDesktop.Enabled(_hwnd) && NativeDesktop.Enabled(_buttonHwnd) && !NativeDesktop.Minimized(_hwnd));
                Assert.False(NativeDesktop.ModifiersHeld() || NativeDesktop.MouseButtonsHeld() || NativeDesktop.PointerCaptured(),
                    "Shared input is active.");
                if (requireForeground)
                {
                    Assert.Equal(_hwnd, NativeDesktop.Foreground);
                    Assert.Equal(_hwnd, NativeDesktop.Root(NativeDesktop.FocusHandle()));
                    Assert.Equal(_pid, _uia.GetFocusedElement()?.CurrentProcessId);
                }
            }
        }

        internal void WaitForCount(int expected, CancellationToken ct)
        {
            var start = Stopwatch.GetTimestamp();
            var actual = -1;
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                ct.ThrowIfCancellationRequested();
                var text = _counter.CurrentName;
                if (text.StartsWith("Applied ", StringComparison.Ordinal) &&
                    int.TryParse(text.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out actual) && actual >= expected) break;
                ct.WaitHandle.WaitOne(10);
            }
            Assert.Equal(expected, actual);
            Assert.Equal("Delete count 0", _deletions.CurrentName);
        }

        private IUIAutomationElement Find(string name) => _window.FindFirst(TreeScope.TreeScope_Descendants,
            _uia.CreatePropertyCondition(UIA_NamePropertyId, name)) ?? throw new InvalidOperationException($"Fixture control {name} is missing.");
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = directory.Name;
        var configuration = directory.Parent!.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "OmniWin.sln")) && directory.Parent is not null) directory = directory.Parent;
        var path = Path.Combine(directory.FullName, "testapps", "OmniWin.FixtureApp", "bin", configuration, framework, "OmniWin.FixtureApp.exe");
        Assert.True(File.Exists(path), $"Build the fixture first: {path}");
        return path;
    }

    private sealed class WorkerLifetime(UiaWorker value) : IDisposable
    {
        internal UiaWorker Value { get; } = value;
        public void Dispose() => Value.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private sealed class EventLifetime(UiaEvents value) : IDisposable
    {
        internal UiaEvents Value { get; } = value;
        public void Dispose() => Value.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private sealed record Sample(string Block, bool ActivityHooks, int Iteration, bool Warmup, string Kind,
        double? AtomicMs, double? MoveMs, double? ButtonsMs, int Sent, int Requested, bool Verified, int ExpectedCount);

    private sealed record Summary(int Count, double P50, double P95, double P99, double Min, double Max)
    {
        internal static Summary From(List<double> values)
        {
            values.Sort();
            double Rank(double p) => values[Math.Clamp((int)Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1)];
            return new Summary(values.Count, Rank(0.5), Rank(0.95), Rank(0.99), values[0], values[^1]);
        }
    }
}
