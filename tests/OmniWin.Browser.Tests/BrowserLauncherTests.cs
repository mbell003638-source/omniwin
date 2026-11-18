using System.Diagnostics;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser.Tests;

public sealed class BrowserLauncherTests
{
    [Fact]
    public void Validates_profile_directory_and_rejects_personal_user_profiles()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var personalEdge = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        var personalChrome = Path.Combine(localAppData, "Google", "Chrome", "User Data");

        var ex1 = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(personalEdge));
        Assert.Contains("Security violation", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var ex2 = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(personalChrome));
        Assert.Contains("Security violation", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Microsoft", "Edge Beta")]
    [InlineData("Microsoft", "Edge Dev")]
    [InlineData("Microsoft", "Edge SxS")]
    [InlineData("Google", "Chrome Beta")]
    [InlineData("Google", "Chrome Dev")]
    [InlineData("Google", "Chrome SxS")]
    [InlineData("Chromium", "Chromium")]
    [InlineData("Vivaldi", "Vivaldi")]
    public void Real_browser_user_data_paths_are_refused(string vendor, string product)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, vendor, product, "User Data");
        var ex = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(path));
        Assert.Contains("Security violation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opera_profile_path_is_refused()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var opera = Path.Combine(appData, "Opera Software", "Opera Stable");
        var ex = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(opera));
        Assert.Contains("Security violation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Path_inside_data_dir_is_accepted()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataDir = Path.Combine(tempBase, "data");
            var profileDir = Path.Combine(dataDir, "browser-profile");
            Directory.CreateDirectory(dataDir);

            // Should not throw
            BrowserLauncher.ValidateProfileDirectory(profileDir, dataDir);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Path_with_parent_traversal_escaping_data_dir_is_refused()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataDir = Path.Combine(tempBase, "data");
            var profileDir = Path.Combine(dataDir, "..", "escaped-profile");
            Directory.CreateDirectory(dataDir);

            var ex = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(profileDir, dataDir));
            Assert.Contains("Security violation", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Junction_inside_data_dir_pointing_outside_is_refused()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid().ToString("N"));
        var junctionInside = string.Empty;
        try
        {
            var dataDir = Path.Combine(tempBase, "data");
            var fakeUserData = Path.Combine(tempBase, "fake-user-data");
            junctionInside = Path.Combine(dataDir, "linked-profile");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(fakeUserData);

            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionInside}\" \"{fakeUserData}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();

            var ex = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.ValidateProfileDirectory(junctionInside, dataDir));
            Assert.Contains("Security violation", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!string.IsNullOrEmpty(junctionInside) && Directory.Exists(junctionInside))
            {
                var rmdir = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{junctionInside}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(rmdir);
                p?.WaitForExit();
            }
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Rejects_non_dedicated_browser_modes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid());
        var paths = OmniWinPaths.FromOverride(tempDir);
        var config = new OmniWinConfig
        {
            Browser = new BrowserConfig { Mode = "attach" }
        };

        var launcher = new BrowserLauncher(config, paths);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(headless: true, TestContext.Current.CancellationToken));
        Assert.NotNull(ex);
    }

    [Fact]
    public void Finds_browser_executable_path()
    {
        var path = BrowserLauncher.FindBrowserExecutable("msedge");
        Assert.True(File.Exists(path));
        Assert.Contains(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_devtools_active_port_is_ignored_and_fresh_port_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempBase = Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempBase);
            var portFile = Path.Combine(tempBase, "DevToolsActivePort");

            // Write stale port file dated 5 minutes in the past
            await File.WriteAllLinesAsync(portFile, ["9999", "/devtools/browser/stale"], ct);
            File.SetLastWriteTimeUtc(portFile, DateTime.UtcNow.AddMinutes(-5));

            using var currentProc = Process.GetCurrentProcess();
            var processStartUtc = DateTime.UtcNow;

            // Background task to write fresh port file after 100ms
            _ = Task.Run(async () =>
            {
                await Task.Delay(100, ct);
                await File.WriteAllLinesAsync(portFile, ["9222", "/devtools/browser/fresh"], ct);
                File.SetLastWriteTimeUtc(portFile, DateTime.UtcNow.AddSeconds(1));
            }, ct);

            var (port, wsUrl) = await BrowserLauncher.WaitForActivePortAsync(portFile, currentProc, ct, processStartUtc);
            Assert.Equal(9222, port);
            Assert.Equal(new Uri("ws://127.0.0.1:9222/devtools/browser/fresh"), wsUrl);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    // ---- N6: a bad data directory must not let a real browser profile through. Fake folders under a temp dir only. ----

    private static string NewTempBase() =>
        Path.Combine(Path.GetTempPath(), "omniwin-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Data_dir_that_is_a_known_browser_folder_is_refused()
    {
        var tempBase = NewTempBase();
        try
        {
            var fakeBrowser = Path.Combine(tempBase, "Fake", "Chrome", "User Data");
            Directory.CreateDirectory(fakeBrowser);
            var profile = Path.Combine(fakeBrowser, "browser-profile");

            var ex = Assert.Throws<InvalidOperationException>(
                () => BrowserLauncher.ValidateProfileDirectory(profile, fakeBrowser, [fakeBrowser]));
            Assert.Contains("Security violation", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Data_dir_inside_a_known_browser_folder_is_refused()
    {
        var tempBase = NewTempBase();
        try
        {
            var fakeBrowser = Path.Combine(tempBase, "Fake", "Edge", "User Data");
            var dataDir = Path.Combine(fakeBrowser, "Default", "omniwin-data");
            Directory.CreateDirectory(dataDir);
            var profile = Path.Combine(dataDir, "browser-profile");

            Assert.Throws<InvalidOperationException>(
                () => BrowserLauncher.ValidateProfileDirectory(profile, dataDir, [fakeBrowser]));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Data_dir_that_contains_a_known_browser_folder_is_refused()
    {
        var tempBase = NewTempBase();
        try
        {
            // Like pointing --data-dir at %LOCALAPPDATA%: the real browser folders sit below it.
            var fakeLocal = Path.Combine(tempBase, "FakeLocal");
            var fakeBrowser = Path.Combine(fakeLocal, "Google", "Chrome", "User Data");
            Directory.CreateDirectory(fakeBrowser);

            Assert.Throws<InvalidOperationException>(
                () => BrowserLauncher.ValidateProfileDirectory(fakeBrowser, fakeLocal, [fakeBrowser]));
            Assert.Throws<InvalidOperationException>(
                () => BrowserLauncher.ValidateProfileDirectory(Path.Combine(fakeLocal, "OmniWin", "browser-profile"), fakeLocal, [fakeBrowser]));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Unrelated_data_dir_is_accepted_when_known_browser_folders_are_elsewhere()
    {
        var tempBase = NewTempBase();
        try
        {
            var dataDir = Path.Combine(tempBase, "data");
            var fakeBrowser = Path.Combine(tempBase, "Fake", "Chrome", "User Data");
            Directory.CreateDirectory(dataDir);
            BrowserLauncher.ValidateProfileDirectory(Path.Combine(dataDir, "browser-profile"), dataDir, [fakeBrowser]);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void Known_browser_folder_list_covers_chrome_edge_variants_chromium_vivaldi_and_opera()
    {
        var list = BrowserLauncher.KnownBrowserUserDataDirs(@"C:\FakeLocal", @"C:\FakeRoaming");
        foreach (var expected in new[]
        {
            @"C:\FakeLocal\Google\Chrome\User Data",
            @"C:\FakeLocal\Google\Chrome SxS\User Data",
            @"C:\FakeLocal\Microsoft\Edge\User Data",
            @"C:\FakeLocal\Microsoft\Edge Beta\User Data",
            @"C:\FakeLocal\Chromium\User Data",
            @"C:\FakeLocal\Vivaldi\User Data",
            @"C:\FakeRoaming\Opera Software\Opera Stable",
        })
        {
            Assert.Contains(expected, list);
        }
    }

    [Fact]
    public void Profile_folder_with_browser_data_and_no_omniwin_marker_is_refused()
    {
        var tempBase = NewTempBase();
        try
        {
            var profile = Path.Combine(tempBase, "data", "browser-profile");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "Local State"), "{}");

            var ex = Assert.Throws<InvalidOperationException>(() => BrowserLauncher.PrepareProfileDirectory(profile));
            Assert.Contains("marker", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(profile, BrowserLauncher.ProfileMarkerFileName)));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void New_profile_folder_gets_a_marker_and_is_accepted_again_with_browser_data()
    {
        var tempBase = NewTempBase();
        try
        {
            var profile = Path.Combine(tempBase, "data", "browser-profile");
            BrowserLauncher.PrepareProfileDirectory(profile);
            Assert.True(File.Exists(Path.Combine(profile, BrowserLauncher.ProfileMarkerFileName)));

            // Once the browser has run in it, it holds data but still has the marker, so it is accepted.
            File.WriteAllText(Path.Combine(profile, "Local State"), "{}");
            Directory.CreateDirectory(Path.Combine(profile, "Default"));
            BrowserLauncher.PrepareProfileDirectory(profile);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }
}
