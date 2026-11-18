using System.Diagnostics;
using OmniWin.Core;

namespace OmniWin.Ocr.Tests;

/// <summary>
/// Phase 5 live-session tests for screen_view: they show real windows and capture real pixels, so they run only with the
/// owner present (set OMNIWIN_LIVE_SESSION=1) and hold <c>Local\OmniWin.UiInputTestLock</c>. They send no input. The
/// windows are this test's own fixture-app windows; nothing of the owner's is opened. Pixels stay in memory.
/// </summary>
public sealed class ScreenViewLiveTests
{
    public static bool Live => Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") == "1";

    private const string SkipReason = "Phase 5 live session: shows real windows. Set OMNIWIN_LIVE_SESSION=1 with the owner present.";

    private static string FixturePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var root = output;
        while (!File.Exists(Path.Combine(root.FullName, "OmniWin.sln")) && root.Parent is not null) root = root.Parent;
        var fixture = Path.Combine(root.FullName, "testapps", "OmniWin.FixtureApp", "bin", output.Parent?.Name ?? "Release", output.Name, "OmniWin.FixtureApp.exe");
        Assert.True(File.Exists(fixture), $"Build the fixture project first. Missing {fixture}");
        return fixture;
    }

    private static ViewSetupLive NewService(Process process)
    {
        var info = new WindowInfo("w1", process.MainWindowHandle, process.Id, process.ProcessName + ".exe", process.MainWindowTitle, true, false, false, null, null);
        var desktop = new FakeDesktop { Windows = [info] };
        var service = new ScreenViewService(new FakeGate(), new FakeKill(), new FakeMetrics(), desktop, new ScreenViewOptions());
        return new ViewSetupLive(service);
    }

    private sealed record ViewSetupLive(ScreenViewService Service);

    private static async Task<Process> WaitForWindowAsync(Process process, CancellationToken ct)
    {
        for (var i = 0; i < 100 && process.MainWindowHandle == IntPtr.Zero; i++)
        {
            await Task.Delay(100, ct);
            process.Refresh();
        }

        Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
        return process;
    }

    [Fact(Skip = SkipReason, SkipUnless = nameof(Live))]
    public async Task The_fixture_window_is_viewed_with_its_password_field_black_and_the_rest_visible()
    {
        var ct = TestContext.Current.CancellationToken;
        // Taken and released on one dedicated thread: a named mutex cannot be released from another thread after an await.
        using var uiLock = await UiLockHolder.AcquireAsync(@"Local\OmniWin.UiInputTestLock", TimeSpan.FromSeconds(15));
        try
        {
            var launch = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, RedirectStandardOutput = true };
            launch.ArgumentList.Add("--title");
            launch.ArgumentList.Add("OmniWin view test " + Guid.NewGuid().ToString("N"));
            using var fixture = Process.Start(launch)!;
            try
            {
                Assert.Equal("OMNIWIN_FIXTURE_READY", await fixture.StandardOutput.ReadLineAsync(ct));
                await WaitForWindowAsync(fixture, ct);

                var view = await NewService(fixture).Service.ViewAsync("w1", null, 1920, ct);

                Assert.True(view.Ok, $"{view.Failure?.Reason}/{view.Failure?.Detail}");
                var (width, height, bgra) = await ViewImaging.DecodeAsync(view.Image!.Data, ct);
                // The fixture's "Secret" box sits at about (24,112) 300x26 in client coordinates; the whole window is dark-free elsewhere.
                var dark = 0;
                var total = 0;
                for (var y = 0; y < height; y += 4)
                {
                    for (var x = 0; x < width; x += 4)
                    {
                        var o = ((y * width) + x) * 4;
                        total++;
                        if (bgra[o] <= 12 && bgra[o + 1] <= 12 && bgra[o + 2] <= 12) dark++;
                    }
                }

                Assert.True(dark > 0, "the password field must be black");
                Assert.True(dark < total / 2, "most of the window must stay visible");
            }
            finally
            {
                if (!fixture.HasExited) fixture.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            uiLock.Dispose();
        }
    }

    [Fact(Skip = SkipReason, SkipUnless = nameof(Live))]
    public async Task A_window_is_viewed_by_default_when_no_window_is_named_and_stays_inside_the_size_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        // Taken and released on one dedicated thread: a named mutex cannot be released from another thread after an await.
        using var uiLock = await UiLockHolder.AcquireAsync(@"Local\OmniWin.UiInputTestLock", TimeSpan.FromSeconds(15));
        try
        {
            // The test's own fixture window, closed at the end. (notepad.exe is not used: on Windows 11 it is a stub that hands
            // over to a packaged app, so the process the test starts is not the one that owns the window and cannot close it.)
            var launch = new ProcessStartInfo(FixturePath()) { UseShellExecute = false, RedirectStandardOutput = true };
            launch.ArgumentList.Add("--title");
            launch.ArgumentList.Add("OmniWin view test " + Guid.NewGuid().ToString("N"));
            using var fixture = Process.Start(launch)!;
            try
            {
                Assert.Equal("OMNIWIN_FIXTURE_READY", await fixture.StandardOutput.ReadLineAsync(ct));
                await WaitForWindowAsync(fixture, ct);
                var view = await NewService(fixture).Service.ViewAsync(null, null, null, ct);

                Assert.True(view.Ok, $"{view.Failure?.Reason}/{view.Failure?.Detail}");
                Assert.True(view.Image!.Width > 100 && view.Image.Height > 100);
                Assert.True(Math.Max(view.Image.Width, view.Image.Height) <= ViewLimits.DefaultMaxSide);
            }
            finally
            {
                if (!fixture.HasExited) fixture.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            uiLock.Dispose();
        }
    }

    /// <summary>
    /// The picture case: a known picture (a red disc on blue) is drawn by the test into a window of its own and viewed.
    /// The programmatic check is the dominant colours. Whether the MODEL describes it correctly ("a red circle on a blue
    /// background") is checked by hand in the live session by calling screen_view and reading the answer; there is no
    /// automatic stand-in for that.
    /// </summary>
    [Fact(Skip = SkipReason, SkipUnless = nameof(Live))]
    public async Task A_known_picture_comes_back_with_its_colours_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        var bmp = TestBmp.Solid(600, 400, 0, 0, 255);   // blue background; the disc is added through masking-free painting below
        var (w, h, pixels) = await ViewImaging.DecodeAsync(bmp, ct);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (((x - 300) * (x - 300)) + ((y - 200) * (y - 200)) > 120 * 120) continue;
                var o = ((y * w) + x) * 4;
                pixels[o] = 0;
                pixels[o + 1] = 0;
                pixels[o + 2] = 255;   // red
            }
        }

        // The pixels above are in BGRA; re-encode them as a BMP the pipeline can take.
        var stride = ((w * 3) + 3) & ~3;
        var raw = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var src = ((y * w) + x) * 4;
                var dst = ((h - 1 - y) * stride) + (x * 3);
                raw[dst] = pixels[src];
                raw[dst + 1] = pixels[src + 1];
                raw[dst + 2] = pixels[src + 2];
            }
        }

        var picture = TestBmp.Solid(1, 1, 0, 0, 0);
        var file = new byte[54 + raw.Length];
        Array.Copy(picture, file, 54);
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(18), w);
        BitConverter.TryWriteBytes(file.AsSpan(22), h);
        BitConverter.TryWriteBytes(file.AsSpan(34), raw.Length);
        raw.CopyTo(file.AsSpan(54));

        var rendered = await ViewImaging.RenderAsync(file, [], null, 1280, ct);
        var (rw, rh, rgba) = await ViewImaging.DecodeAsync(rendered.Data, ct);
        var centre = ((rh / 2) * rw + (rw / 2)) * 4;
        var corner = 4 * ((10 * rw) + 10);
        Assert.True(rgba[centre + 2] > 200 && rgba[centre] < 60, "the disc must stay red");
        Assert.True(rgba[corner] > 200 && rgba[corner + 2] < 60, "the background must stay blue");
    }
}
