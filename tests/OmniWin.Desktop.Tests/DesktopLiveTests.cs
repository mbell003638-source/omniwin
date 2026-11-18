using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Desktop.Tests;

[CollectionDefinition("Desktop input", DisableParallelization = true)]
public sealed class DesktopInputCollection;

[Collection("Desktop input")]
[Trait("Category", "UiInput")]
public sealed class DesktopLiveTests
{
    public static bool UiTestsEnabled => Environment.GetEnvironmentVariable("OMNIWIN_UI_TESTS") == "1";

    [Fact(Skip = "Set OMNIWIN_UI_TESTS=1 on an interactive desktop.", SkipUnless = nameof(UiTestsEnabled))]
    public Task Engine_validates_real_input_against_the_isolated_fixture()
    {
        var output = TestContext.Current.TestOutputHelper;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunScenario(line => output?.WriteLine(line));
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true, Name = "omniwin-ui-input-test" };
        // Mutex ownership is thread-affine. Keep the entire scenario on this thread while UIA runs on its own MTA worker.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunScenario(Action<string> output)
    {
        using var mutex = new Mutex(false, @"Local\OmniWin.UiInputTestLock");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            Assert.True(acquired, "Another test holds Local\\OmniWin.UiInputTestLock.");
            RunOwnedFixture(output);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void RunOwnedFixture(Action<string> output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        var title = "OmniWin WS1 " + Guid.NewGuid().ToString("N");
        var launch = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, RedirectStandardOutput = true };
        launch.ArgumentList.Add("--title");
        launch.ArgumentList.Add(title);
        using var fixture = Process.Start(launch) ?? throw new InvalidOperationException("The fixture process did not start.");
        DesktopEngine? engine = null;
        try
        {
            var ready = fixture.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult();
            Assert.Equal("OMNIWIN_FIXTURE_READY", ready);
            var config = new OmniWinConfig { UserActivityCooldownMs = 0 };
            var killSwitch = new IdleKillSwitch();
            var metrics = new RollingMetrics();
            var activity = new TestUserActivity();
            engine = new DesktopEngine(config, new SafetyGate(config, killSwitch), killSwitch,
                metrics, NullLogger<DesktopEngine>.Instance, activity);
            var window = AwaitWindow(engine, fixture.Id, title, ct);
            Assert.Null(window.Inaccessible);
            foreach (var candidate in engine.ListWindowsAsync(ct).GetAwaiter().GetResult().Where(item => item.Pid == fixture.Id))
            {
                output($"Fixture window {candidate.Id} hwnd={candidate.Hwnd} modal={candidate.IsModal} owner={candidate.OwnerId} title={candidate.Title}");
            }
            output($"Fixture foreground={NativeDesktop.Foreground == window.Hwnd} enabled={NativeDesktop.Enabled(window.Hwnd)} input_desktop={NativeDesktop.OnInputDesktop()}");
            if (!window.IsForeground)
            {
                // A checked click on our visible topmost fixture establishes foreground permission through
                // the same native input path under test.
                var startup = engine.ObserveAsync(new DesktopObserveRequest(window.Id), ct).GetAwaiter().GetResult();
                var activate = engine.ActAsync(new DesktopActRequest(Ref(startup, "edit", "Editor"),
                    startup.Generation, "click"), ct).GetAwaiter().GetResult();
                Assert.True(activate.InputSent, ResultFormatter.Format(activate));
                Assert.Equal(ActStatus.Unverified, activate.Status);
                Assert.Equal(Channels.PhysicalClick, activate.Channel);
            }

            Assert.True(engine.BringToForegroundAsync(window.Hwnd, ct).GetAwaiter().GetResult(),
                "Windows refused to foreground the owned fixture. Run these tests on an unlocked interactive desktop.");
            ExerciseEngine(engine, window, title, activity, ct);
            for (var i = 0; i < 30; i++)
            {
                var start = Stopwatch.GetTimestamp();
                engine.ObserveAsync(new DesktopObserveRequest(window.Id, IncludeText: true), ct).GetAwaiter().GetResult();
                metrics.Record("desktop_observe.warm", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            foreach (var (operation, timing) in metrics.Snapshot().Ops.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                output($"{operation} p50={timing.P50:F3}ms p95={timing.P95:F3}ms n={timing.Count}");
            }
        }
        finally
        {
            try
            {
                if (engine is not null)
                {
                    engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
            }
            finally
            {
                if (!fixture.HasExited)
                {
                    fixture.CloseMainWindow();
                    if (!fixture.WaitForExit(3000)) fixture.Kill(entireProcessTree: true);
                }
            }
        }
    }

    private static void ExerciseEngine(DesktopEngine engine, WindowInfo window, string title, TestUserActivity activity, CancellationToken ct)
    {
        Observation Observe() => engine.ObserveAsync(new DesktopObserveRequest(window.Id, IncludeText: true), ct).GetAwaiter().GetResult();

        ActResult Act(string role, string name, string action, string? value = null, ExpectSpec? expect = null)
        {
            var observed = Observe();
            var elementRef = Ref(observed, role, name);
            return engine.ActAsync(new DesktopActRequest(elementRef, observed.Generation, action, value, expect), ct).GetAwaiter().GetResult();
        }

        var initial = Observe();
        Assert.Equal(window.Id, initial.Target);
        Assert.Contains("value=<redacted>", initial.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-712", initial.Text, StringComparison.Ordinal);
        Assert.NotNull(engine.DescribeAsync(Ref(initial, "edit", "Editor"), ct).GetAwaiter().GetResult());

        AssertConfirmed(Act("edit", "Editor", "set_value", "initial"), Channels.SetValue);
        AssertConfirmed(Act("edit", "Editor", "clear"), Channels.SetValue);
        AssertConfirmed(Act("edit", "Editor", "type", "OmniWin café Ω 🚀"), Channels.UnicodeTyping);
        AssertConfirmed(Act("checkbox", "Toggle", "toggle"), Channels.Toggle);
        AssertConfirmed(Act("listitem", "Item 2", "select"), Channels.Select);
        AssertConfirmed(Act("edit", "Editor", "focus"), Channels.Api);

        var keys = engine.SendKeysAsync(new KeysRequest("ctrl+a", window.Id), ct).GetAwaiter().GetResult();
        Assert.True(keys.InputSent, ResultFormatter.Format(keys));
        Assert.Equal(ActStatus.Unverified, keys.Status);
        AssertConfirmed(Act("edit", "Editor", "type", "Replacement"), Channels.UnicodeTyping);

        AssertConfirmed(Act("edit", "Editor", "type", " tail"), Channels.UnicodeTyping);
        Assert.Contains("value=\"Replacement tail\"", Observe().Text, StringComparison.Ordinal);
        var selectPrefix = engine.SendKeysAsync(new KeysRequest("home shift+right shift+right", window.Id), ct)
            .GetAwaiter().GetResult();
        Assert.True(selectPrefix.InputSent, ResultFormatter.Format(selectPrefix));
        AssertConfirmed(Act("edit", "Editor", "type", "Ab"), Channels.UnicodeTyping);
        Assert.Contains("value=\"Abplacement tail\"", Observe().Text, StringComparison.Ordinal);
        var moveCaret = engine.SendKeysAsync(new KeysRequest("home right right right", window.Id), ct).GetAwaiter().GetResult();
        Assert.True(moveCaret.InputSent, ResultFormatter.Format(moveCaret));
        AssertConfirmed(Act("edit", "Editor", "type", "-"), Channels.UnicodeTyping);
        Assert.Contains("value=\"Abp-lacement tail\"", Observe().Text, StringComparison.Ordinal);

        var click = Act("button", "Apply", "click", expect: new ExpectSpec(new TextAppears("Applied 1", window.Id)));
        AssertConfirmed(click, Channels.PhysicalClick);
        var afterClick = Observe();
        Assert.Contains("text \"Applied 1\"", afterClick.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("text \"Applied 2\"", afterClick.Text, StringComparison.Ordinal);

        var denied = Act("button", "Delete", "click");
        Assert.Equal(ActStatus.Denied, denied.Status);
        Assert.False(denied.InputSent);
        Assert.Equal(ReasonCodes.PolicyForbidden, denied.Reason);
        Assert.Contains("text \"Delete count 0\"", Observe().Text, StringComparison.Ordinal);

        var disabled = Act("button", "Disabled", "click");
        Assert.Equal(ActStatus.Blocked, disabled.Status);
        Assert.Equal(ReasonCodes.Disabled, disabled.Reason);
        Assert.False(disabled.InputSent);

        var dialogTitle = title + " Dialog";
        AssertConfirmed(Act("button", "Open dialog", "invoke",
            expect: new ExpectSpec(new WindowOpened(dialogTitle))), Channels.Invoke);
        var modal = engine.ObserveAsync(new DesktopObserveRequest(window.Id, Scope: "dialog"), ct).GetAwaiter().GetResult();
        Assert.NotEqual(window.Id, modal.Target);
        var modalBlock = Act("edit", "Editor", "set_value", "blocked by modal");
        Assert.Equal(ActStatus.Blocked, modalBlock.Status);
        Assert.Equal(ReasonCodes.ModalBlocking, modalBlock.Reason);
        Assert.False(modalBlock.InputSent);
        var close = engine.ActAsync(new DesktopActRequest(Ref(modal, "button", "Close dialog"), modal.Generation,
            "invoke", Expect: new ExpectSpec(new WindowClosed(modal.Target))), ct).GetAwaiter().GetResult();
        AssertConfirmed(close, Channels.Invoke);

        var cover = Act("button", "Cover target", "invoke");
        Assert.True(cover.InputSent, ResultFormatter.Format(cover));
        activity.Active = true;
        try
        {
            var foreground = NativeDesktop.Foreground;
            Assert.NotEqual(window.Hwnd, foreground);
            AssertConfirmed(Act("edit", "Editor", "set_value", "Background update"), Channels.SetValue);
            AssertConfirmed(Act("button", "Apply", "invoke",
                expect: new ExpectSpec(new TextAppears("Applied 2", window.Id))), Channels.Invoke);
            Assert.Equal(foreground, NativeDesktop.Foreground);
            var typingWhileActive = Act("edit", "Editor", "type", "Shared input stays paused");
            Assert.Equal(ActStatus.Blocked, typingWhileActive.Status);
            Assert.Equal(ReasonCodes.UserActive, typingWhileActive.Reason);
            Assert.False(typingWhileActive.InputSent);
            var deleteWhileActive = Act("button", "Delete", "invoke");
            Assert.Equal(ActStatus.Denied, deleteWhileActive.Status);
            Assert.Equal(ReasonCodes.PolicyForbidden, deleteWhileActive.Reason);
            Assert.False(deleteWhileActive.InputSent);
        }
        finally
        {
            activity.Active = false;
        }

        var covered = Act("button", "Apply", "click");
        Assert.Equal(ActStatus.Blocked, covered.Status);
        Assert.Equal(ReasonCodes.OccludedByWindow, covered.Reason);
        Assert.False(covered.InputSent);
        var uncover = Act("button", "Uncover target", "invoke");
        Assert.True(uncover.InputSent, ResultFormatter.Format(uncover));
        AssertConfirmed(Act("edit", "Editor", "focus"), Channels.Api);
        activity.Active = true;
        try
        {
            var activeWindowEdit = Act("edit", "Editor", "set_value", "Concurrent active-window edit");
            Assert.Equal(ActStatus.Blocked, activeWindowEdit.Status);
            Assert.Equal(ReasonCodes.UserActive, activeWindowEdit.Reason);
            Assert.False(activeWindowEdit.InputSent);
            var activeKeys = engine.SendKeysAsync(new KeysRequest("ctrl+a", window.Id), ct).GetAwaiter().GetResult();
            Assert.Equal(ActStatus.Blocked, activeKeys.Status);
            Assert.Equal(ReasonCodes.UserActive, activeKeys.Reason);
            Assert.False(activeKeys.InputSent);
        }
        finally
        {
            activity.Active = false;
        }

        var scrollTarget = Observe();
        var listFocus = Ref(scrollTarget, "list", "Items");
        var selectedItem = Ref(scrollTarget, "listitem", "Item 2");
        var focusList = engine.ActAsync(new DesktopActRequest(listFocus, scrollTarget.Generation, "focus",
            Expect: new ExpectSpec(new AnyOf([new FocusOn(listFocus), new FocusOn(selectedItem)]))), ct).GetAwaiter().GetResult();
        AssertConfirmed(focusList, Channels.Api);
        var beforeScroll = Observe();
        var listRef = Ref(beforeScroll, "list", "Items");
        var scroll = engine.ScrollAsync(new ScrollRequest(listRef, window.Id, "down", 3), ct).GetAwaiter().GetResult();
        Assert.True(scroll.InputSent, ResultFormatter.Format(scroll));
        Assert.Equal(ActStatus.Unverified, scroll.Status);
        var afterScroll = Observe();
        Assert.True(afterScroll.Generation > beforeScroll.Generation, "The list did not expose a scroll change.");

        var beforeRename = Observe();
        var oldApply = Ref(beforeRename, "button", "Apply");
        var rename = Act("button", "Rename target", "invoke",
            expect: new ExpectSpec(new ElementAppears("button", "Renamed apply", window.Id)));
        AssertConfirmed(rename, Channels.Invoke);
        var stale = engine.ActAsync(new DesktopActRequest(oldApply, beforeRename.Generation, "click"), ct).GetAwaiter().GetResult();
        Assert.Equal(ActStatus.Stale, stale.Status);
        Assert.False(stale.InputSent);
        Assert.Contains("text \"Applied 2\"", Observe().Text, StringComparison.Ordinal);
    }

    private static WindowInfo AwaitWindow(DesktopEngine engine, int pid, string title, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(15))
        {
            ct.ThrowIfCancellationRequested();
            var windows = engine.ListWindowsAsync(ct).GetAwaiter().GetResult();
            var match = windows.FirstOrDefault(window => window.Pid == pid && window.Title == title);
            if (match is not null) return match;
            // Process creation and UIA provider publication do not share a readiness event.
            ct.WaitHandle.WaitOne(25);
        }

        throw new InvalidOperationException("The owned fixture window did not appear in UI Automation within 15 seconds.");
    }

    private static string Ref(Observation observation, string role, string name)
    {
        var pattern = @"^\s*(e\d+) " + Regex.Escape(role) + " " +
            Regex.Escape(ResultFormatter.Quote(name, ResultFormatter.MaxNameChars)) + @"(?:\s|$)";
        var match = Regex.Match(observation.Text, pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not find {role} {name}.\n{observation.Text}");
        return match.Groups[1].Value;
    }

    private static void AssertConfirmed(ActResult result, string channel)
    {
        Assert.True(result.InputSent, ResultFormatter.Format(result));
        Assert.True(result.Status == ActStatus.Confirmed, ResultFormatter.Format(result));
        Assert.Equal(channel, result.Channel);
    }

    private static string FixturePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = output.Name;
        var configuration = output.Parent?.Name ?? "Debug";
        var root = output;
        while (!File.Exists(Path.Combine(root.FullName, "OmniWin.sln")) && root.Parent is not null) root = root.Parent;
        var fixture = Path.Combine(root.FullName, "testapps", "OmniWin.FixtureApp", "bin", configuration, framework, "OmniWin.FixtureApp.exe");
        Assert.True(File.Exists(fixture), $"Build the fixture project first. Missing {fixture}");
        return fixture;
    }

    private sealed class IdleKillSwitch : IKillSwitch
    {
        public bool IsTripped => false;
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class TestUserActivity : IUserActivity
    {
        internal volatile bool Active;
        public bool UserActiveRecently => Active;
        public DateTimeOffset LastRealInput => Active ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
    }
}
