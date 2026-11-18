using System.Diagnostics;

namespace OmniWin.Integration.Harness;

/// <summary>
/// Finds and stops the Edge processes that one harness session started, and only those: a process counts only when its command line
/// contains that session's own data folder (its browser profile lives under it). The owner's Edge and any other process are never
/// matched. No test-framework dependency, so the benchmark runner can link this file.
/// </summary>
public static class EdgeProcesses
{
    /// <summary>The marker must be a real throwaway folder path of ours. An empty or short marker would match everything.</summary>
    public static void ValidateMarker(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length < 20 || !Path.IsPathRooted(marker) ||
            marker.IndexOf("omniwin", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new ArgumentException("The marker must be the full path of a throwaway OmniWin data folder, not '" + marker + "'.", nameof(marker));
        }
    }

    /// <summary>Pure selection: the process ids whose command line contains <paramref name="marker"/> (case-insensitive).</summary>
    public static IReadOnlyList<int> SelectOwned(IEnumerable<(int Pid, string CommandLine)> processes, string marker)
    {
        ValidateMarker(marker);
        return processes
            .Where(p => p.CommandLine.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToList();
    }

    /// <summary>Parses the "pid|command line" lines the listing query prints.</summary>
    public static IReadOnlyList<(int Pid, string CommandLine)> ParseListing(string output)
    {
        var list = new List<(int, string)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var bar = line.IndexOf('|');
            if (bar <= 0 || !int.TryParse(line[..bar].Trim(), out var pid)) continue;
            list.Add((pid, line[(bar + 1)..].TrimEnd('\r')));
        }

        return list;
    }

    /// <summary>All msedge.exe processes with their command lines.</summary>
    public static IReadOnlyList<(int Pid, string CommandLine)> List()
    {
        const string command =
            "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | ForEach-Object { '{0}|{1}' -f $_.ProcessId, $_.CommandLine }";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start powershell.exe to list Edge processes.");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new TimeoutException("Listing Edge processes took longer than 60 s.");
        }

        return ParseListing(output);
    }

    /// <summary>How many Edge processes belong to the session whose data folder is <paramref name="marker"/>.</summary>
    public static int CountOwned(string marker) => SelectOwned(List(), marker).Count;

    /// <summary>
    /// Stops the session's own Edge processes (and their children). Returns how many were found. Touches nothing whose command line does
    /// not contain the marker.
    /// </summary>
    public static int StopOwned(string marker)
    {
        var owned = SelectOwned(List(), marker);
        foreach (var pid in owned)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit(15_000);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        }

        return owned.Count;
    }

    /// <summary>The leak check: after a session has been stopped, no Edge of that session may be left. Stops any leftovers first, then fails.</summary>
    public static void AssertNoneLeft(string marker, string when)
    {
        var found = StopOwned(marker);
        if (found > 0)
        {
            throw new InvalidOperationException($"{found} Edge process(es) started by this session were still running {when}. They were stopped, but the launch or the test left them behind.");
        }
    }
}
