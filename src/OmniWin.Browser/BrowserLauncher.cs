using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Browser;

/// <summary>
/// Launches Edge or Chrome in dedicated profile mode (contract §5, §14).
/// Refuses default user profiles, external attaches, or non-dedicated modes.
/// </summary>
public sealed class BrowserLauncher : IAsyncDisposable
{
    private static readonly string[] BrowserExecutableCandidates =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ];

    private readonly OmniWinConfig _config;
    private readonly OmniWinPaths _paths;
    private Process? _browserProcess;
    private Uri? _webSocketUrl;
    private int _debuggingPort;
    private string? _profileDir;

    public BrowserLauncher(OmniWinConfig config, OmniWinPaths paths)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public bool IsRunning => _browserProcess is { HasExited: false };

    public Uri? WebSocketUrl => _webSocketUrl;

    public int DebuggingPort => _debuggingPort;

    public string? ProfileDir => _profileDir;

    public Process? Process => _browserProcess;

    public string BrowserProcessName =>
        _browserProcess is { HasExited: false }
            ? $"{_browserProcess.ProcessName}.exe"
            : Path.GetFileName(FindBrowserExecutable(_config.Browser.Channel));

    /// <summary>
    /// Launches the browser using the dedicated profile and discovers the CDP WebSocket endpoint.
    /// </summary>
    public async Task<Uri> LaunchAsync(bool? headless = null, CancellationToken ct = default)
    {
        if (_config.Browser.Mode != "dedicated")
        {
            throw new InvalidOperationException($"Browser mode must be 'dedicated', found: '{_config.Browser.Mode}'");
        }

        var profileDir = _paths.ResolveBrowserProfileDir(_config);
        ValidateProfileDirectory(profileDir, _paths.DataDir);
        PrepareProfileDirectory(profileDir);
        ValidateProfileDirectory(profileDir, _paths.DataDir);

        // Before this launch owns the profile (and so before anything could stop its browsers), make sure no other running
        // OmniWin server is using it, and clear a browser left on it by a server that is gone.
        ClearLeftoverBrowser(profileDir);
        _profileDir = profileDir;
        WriteOwner(profileDir);

        var exePath = FindBrowserExecutable(_config.Browser.Channel);
        var activePortFile = Path.Combine(profileDir, "DevToolsActivePort");

        var isHeadless = headless ?? false;
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = isHeadless,
            Arguments = BuildArguments(profileDir, isHeadless),
        };

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to launch browser executable at {exePath}");
            _browserProcess = process;

            // Poll for DevToolsActivePort written by Chromium on startup. The process started here may hand off to another
            // one and exit with code 0; that is not a failure as long as the port file appears.
            var endpoint = await WaitForActivePortAsync(activePortFile, process, ct).ConfigureAwait(false);
            _webSocketUrl = endpoint.WebSocketUrl;
            _debuggingPort = endpoint.Port;

            // Track the REAL browser (the process that carries this launch's unique profile folder), not the short-lived starter.
            await AdoptRealProcessAsync(profileDir, ct).ConfigureAwait(false);
            return _webSocketUrl;
        }
        catch
        {
            // A launch that fails or is cancelled partway must not leave a hidden browser behind.
            StopLaunchedBrowser();
            throw;
        }
    }

    /// <summary>File in the profile folder naming the OmniWin server that launched the browser on it: process id and start time.</summary>
    internal const string OwnerFileName = ".omniwin-owner";

    /// <summary>
    /// A server that ended without shutting down (force-closed by its MCP client, for example), or a connection that dropped
    /// while its browser kept running, leaves a browser on this profile. A new start would hand off to that old browser, whose
    /// <c>DevToolsActivePort</c> is older than the new launch, and wait for the full timeout. So: if the browser on this profile
    /// belongs to another OmniWin server that is still running, refuse (and touch nothing); otherwise (its server is gone, or it
    /// is this same server) stop it first. Only OmniWin uses this profile folder.
    /// </summary>
    internal static void ClearLeftoverBrowser(string profileDir)
    {
        if (BrowserProcesses.CountForProfile(profileDir) == 0) return;

        if (OwnedByAnotherLiveServer(profileDir, out var ownerPid))
        {
            throw new InvalidOperationException(
                $"Another running OmniWin server (process {ownerPid}) is using this browser profile ('{profileDir}'). " +
                "Close that server first, or start this one with its own --data-dir.");
        }

        var survivors = BrowserProcesses.StopForProfile(profileDir);
        if (survivors > 0)
        {
            throw new InvalidOperationException(
                $"A browser left over from an earlier OmniWin run is still using this profile ({survivors} process(es) could not be stopped).");
        }
    }

    /// <summary>True when the owner file names a running process other than this one, with the same start time.</summary>
    internal static bool OwnedByAnotherLiveServer(string profileDir, out int ownerPid)
    {
        ownerPid = 0;
        try
        {
            var parts = File.ReadAllText(Path.Combine(profileDir, OwnerFileName)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out ownerPid) || !long.TryParse(parts[1], out var startTicks)) return false;
            if (ownerPid == Environment.ProcessId) return false;

            using var owner = Process.GetProcessById(ownerPid);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                       InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No owner file, or the owner process is gone: the browser is a leftover.
            return false;
        }
    }

    internal static string OwnerStamp(Process process) => $"{process.Id} {process.StartTime.ToUniversalTime().Ticks}";

    private static void WriteOwner(string profileDir)
    {
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(profileDir, OwnerFileName), OwnerStamp(self));
    }

    /// <summary>Waits (bounded) for the real browser root to show up and makes it the tracked process.</summary>
    private async Task AdoptRealProcessAsync(string profileDir, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5))
        {
            ct.ThrowIfCancellationRequested();
            var root = BrowserProcesses.FindRoot(profileDir);
            if (root is not null)
            {
                if (!ReferenceEquals(_browserProcess, root) && _browserProcess is not null && _browserProcess.Id != root.Id)
                {
                    _browserProcess.Dispose();
                }

                _browserProcess = root;
                return;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Stops every process of this launch by its unique profile folder. Returns how many survived (0 = clean).</summary>
    internal int StopLaunchedBrowser()
    {
        var survivors = 0;
        if (_profileDir is not null)
        {
            survivors = BrowserProcesses.StopForProfile(_profileDir);
        }

        if (_browserProcess is not null)
        {
            try
            {
                if (!_browserProcess.HasExited) _browserProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            _browserProcess.Dispose();
            _browserProcess = null;
        }

        return survivors;
    }

    public ValueTask DisposeAsync()
    {
        // Always by profile folder: the starter process may be long gone while the real browser is still running.
        StopLaunchedBrowser();
        return ValueTask.CompletedTask;
    }

    private static string BuildArguments(string profileDir, bool headless)
    {
        var args = new List<string>
        {
            "--remote-debugging-port=0",
            $"--user-data-dir=\"{profileDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-networking",
            "--disable-features=Translate,OptimizationHints",
            "--disable-component-update",
            "--window-size=1280,800",
            // Parallel lanes: a tab that is not in front must keep running at full speed, otherwise input to it waits for a frame
            // that a hidden tab never produces. These stop the browser from throttling or backgrounding hidden tabs.
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "about:blank"
        };

        if (headless)
        {
            args.Add("--headless=new");
            args.Add("--disable-gpu");
        }

        return string.Join(" ", args);
    }

    internal static async Task<(int Port, Uri WebSocketUrl)> WaitForActivePortAsync(
        string activePortFile, Process process, CancellationToken ct, DateTime? processStartTimeUtc = null)
    {
        var startTimeUtc = processStartTimeUtc ?? GetProcessStartTimeUtc(process);
        var timeout = TimeSpan.FromSeconds(15);
        var start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            ct.ThrowIfCancellationRequested();

            // Exit code 0 is a hand-off to the real browser process: keep waiting for the port file (bounded by the timeout).
            if (process.HasExited && process.ExitCode != 0 && !File.Exists(activePortFile))
            {
                throw new InvalidOperationException($"Browser process exited unexpectedly with code {process.ExitCode}");
            }

            if (File.Exists(activePortFile))
            {
                var writeTimeUtc = File.GetLastWriteTimeUtc(activePortFile);
                if (writeTimeUtc > startTimeUtc)
                {
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(activePortFile, ct).ConfigureAwait(false);
                        if (lines.Length >= 2 &&
                            int.TryParse(lines[0], out var port) &&
                            !string.IsNullOrWhiteSpace(lines[1]))
                        {
                            var wsPath = lines[1].Trim();
                            var wsUri = new Uri($"ws://127.0.0.1:{port}{wsPath}");
                            return (port, wsUri);
                        }
                    }
                    catch (IOException)
                    {
                        // File might be being written
                    }
                }
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for DevToolsActivePort in {activePortFile}");
    }

    private static DateTime GetProcessStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint VOLUME_NAME_DOS = 0x0;
    private const uint FILE_NAME_NORMALIZED = 0x0;

    public static string ResolveCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Environment.ExpandEnvironmentVariables(path);
        path = Path.GetFullPath(path);

        if (Directory.Exists(path) || File.Exists(path))
        {
            return QueryFinalPath(path);
        }

        var segments = new Stack<string>();
        var current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current) && !File.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
            {
                break;
            }
            segments.Push(name);
            current = Path.GetDirectoryName(current);
        }

        var resolvedBase = !string.IsNullOrEmpty(current) && (Directory.Exists(current) || File.Exists(current))
            ? QueryFinalPath(current)
            : current ?? path;

        while (segments.Count > 0)
        {
            resolvedBase = Path.Combine(resolvedBase, segments.Pop());
        }

        return Path.GetFullPath(resolvedBase);
    }

    private static string QueryFinalPath(string existingPath)
    {
        try
        {
            using var handle = CreateFile(
                existingPath,
                FILE_READ_ATTRIBUTES,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                var sb = new StringBuilder(1024);
                var res = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, VOLUME_NAME_DOS | FILE_NAME_NORMALIZED);
                if (res > 0)
                {
                    var result = sb.ToString();
                    if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        result = @"\\" + result.Substring(8);
                    }
                    else if (result.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Substring(4);
                    }
                    return Path.GetFullPath(result);
                }
            }
        }
        catch
        {
            // Fall back
        }

        try
        {
            var di = new DirectoryInfo(existingPath);
            var target = di.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                return Path.GetFullPath(target.FullName);
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        return Path.GetFullPath(existingPath);
    }

    public static void ValidateProfileDirectory(string profileDir, OmniWinPaths paths) =>
        ValidateProfileDirectory(profileDir, paths?.DataDir);

    /// <summary>
    /// Name of the file OmniWin writes into a profile folder it created. A folder that already holds browser data
    /// but has no marker was made by something else (a real browser, for example) and is refused.
    /// </summary>
    internal const string ProfileMarkerFileName = ".omniwin-profile";

    /// <summary>Folders where real browsers keep user data. A data directory may not be, contain or sit inside any of them.</summary>
    internal static IReadOnlyList<string> KnownBrowserUserDataDirs(string? localAppData = null, string? roamingAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roamingAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string[][] localParts =
            [
                ["Google", "Chrome", "User Data"],
                ["Google", "Chrome Beta", "User Data"],
                ["Google", "Chrome Dev", "User Data"],
                ["Google", "Chrome SxS", "User Data"],
                ["Microsoft", "Edge", "User Data"],
                ["Microsoft", "Edge Beta", "User Data"],
                ["Microsoft", "Edge Dev", "User Data"],
                ["Microsoft", "Edge SxS", "User Data"],
                ["Chromium", "User Data"],
                ["Chromium", "Chromium", "User Data"],
                ["Vivaldi", "User Data"],
                ["Vivaldi", "Vivaldi", "User Data"],
                ["BraveSoftware", "Brave-Browser", "User Data"],
                ["Opera Software", "Opera Stable"],
                ["Opera Software", "Opera GX Stable"],
            ];
            foreach (var parts in localParts)
            {
                list.Add(Path.Combine([localAppData, .. parts]));
            }
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            list.Add(Path.Combine(roamingAppData, "Opera Software", "Opera Stable"));
            list.Add(Path.Combine(roamingAppData, "Opera Software", "Opera GX Stable"));
        }

        return list;
    }

    /// <summary>
    /// Creates the profile folder if needed and writes the OmniWin marker. Refuses a folder that already holds
    /// browser data (a <c>Local State</c> file, a <c>Default</c> folder or a <c>Preferences</c> file) and has no marker.
    /// </summary>
    internal static void PrepareProfileDirectory(string profileDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDir);
        var marker = Path.Combine(profileDir, ProfileMarkerFileName);

        if (Directory.Exists(profileDir) && !File.Exists(marker))
        {
            var holdsBrowserData =
                File.Exists(Path.Combine(profileDir, "Local State")) ||
                File.Exists(Path.Combine(profileDir, "Preferences")) ||
                Directory.Exists(Path.Combine(profileDir, "Default"));
            if (holdsBrowserData)
            {
                throw new InvalidOperationException(
                    $"Security violation: Profile directory '{profileDir}' already holds browser data but has no OmniWin marker file ('{ProfileMarkerFileName}'), so OmniWin did not create it and will not use it.");
            }
        }

        Directory.CreateDirectory(profileDir);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "Created by OmniWin. Dedicated automation browser profile.\n");
        }
    }

    public static void ValidateProfileDirectory(string profileDir, string? allowedDataDir = null) =>
        ValidateProfileDirectory(profileDir, allowedDataDir, null);

    internal static void ValidateProfileDirectory(string profileDir, string? allowedDataDir, IReadOnlyList<string>? knownBrowserDirs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDir);

        var dataDir = !string.IsNullOrWhiteSpace(allowedDataDir)
            ? allowedDataDir
            : OmniWinPaths.Default().DataDir;

        var canonicalProfileDir = ResolveCanonicalPath(profileDir);
        var canonicalDataDir = ResolveCanonicalPath(dataDir);

        var pDir = Path.TrimEndingDirectorySeparator(canonicalProfileDir);
        var aDir = Path.TrimEndingDirectorySeparator(canonicalDataDir);

        // The data directory itself must not be, contain, or sit inside a real browser's user-data folder.
        foreach (var known in knownBrowserDirs ?? KnownBrowserUserDataDirs())
        {
            var kDir = Path.TrimEndingDirectorySeparator(ResolveCanonicalPath(known));
            if (Overlaps(aDir, kDir) || Overlaps(pDir, kDir))
            {
                throw new InvalidOperationException(
                    $"Security violation: '{(Overlaps(aDir, kDir) ? canonicalDataDir : canonicalProfileDir)}' is, contains, or sits inside the real browser folder '{kDir}'. OmniWin will not use it.");
            }
        }

        var isInside = string.Equals(pDir, aDir, StringComparison.OrdinalIgnoreCase) ||
                       pDir.StartsWith(aDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!isInside)
        {
            throw new InvalidOperationException(
                $"Security violation: Profile directory '{profileDir}' resolves to '{canonicalProfileDir}', which is not inside OmniWin's data directory '{canonicalDataDir}'.");
        }
    }

    private static bool Overlaps(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string FindBrowserExecutable(string? preferredChannel)
    {
        if (preferredChannel is "chrome" or "google-chrome")
        {
            foreach (var path in BrowserExecutableCandidates.Where(p => p.Contains("Chrome", StringComparison.OrdinalIgnoreCase)))
            {
                if (File.Exists(path)) return path;
            }
        }

        foreach (var path in BrowserExecutableCandidates)
        {
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("Neither Microsoft Edge nor Google Chrome could be found at standard installation paths.");
    }
}
