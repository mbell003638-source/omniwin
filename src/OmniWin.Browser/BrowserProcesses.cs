using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmniWin.Browser;

/// <summary>
/// Finds and stops the browser processes of ONE launch, identified by that launch's unique <c>--user-data-dir</c>.
/// The <c>msedge.exe</c> OmniWin starts can hand off to another process and exit with code 0, so the process object from
/// <c>Process.Start</c> is not the real browser. Matching on the exact profile folder finds the real one (and its children,
/// which carry the same flag) without ever touching a browser that is not OmniWin's.
/// </summary>
internal static class BrowserProcesses
{
    private static readonly string[] BrowserNames = ["msedge", "chrome"];

    /// <summary>
    /// True when a <c>--user-data-dir</c> value in <paramref name="commandLine"/> is exactly <paramref name="profileDir"/>
    /// (quoted or not, ignoring case and a trailing separator). A plain substring test would also match a longer folder
    /// name, for example <c>X\profile</c> inside <c>X\profile2</c>.
    /// </summary>
    internal static bool NamesProfile(string commandLine, string profileDir)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(profileDir)) return false;
        const string Switch = "--user-data-dir=";
        var wanted = Path.TrimEndingDirectorySeparator(profileDir.Trim());
        var index = 0;
        while ((index = commandLine.IndexOf(Switch, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = index + Switch.Length;
            string value;
            if (start < commandLine.Length && commandLine[start] == '"')
            {
                var close = commandLine.IndexOf('"', start + 1);
                value = close < 0 ? commandLine[(start + 1)..] : commandLine[(start + 1)..close];
            }
            else
            {
                var end = start;
                while (end < commandLine.Length && !char.IsWhiteSpace(commandLine[end])) end++;
                value = commandLine[start..end];
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(value), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            index = start;
        }

        return false;
    }

    /// <summary>All browser processes (roots and children) whose command line names <paramref name="profileDir"/> exactly.</summary>
    internal static List<Process> FindForProfile(string profileDir)
    {
        var found = new List<Process>();
        if (string.IsNullOrWhiteSpace(profileDir)) return found;

        foreach (var name in BrowserNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                var command = ProcessCommandLine.TryRead(process.Id);
                if (command is not null && NamesProfile(command, profileDir))
                {
                    found.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
        }

        return found;
    }

    /// <summary>The browser's top-level process (the one without a <c>--type=</c> role flag), or null.</summary>
    internal static Process? FindRoot(string profileDir)
    {
        Process? root = null;
        foreach (var process in FindForProfile(profileDir))
        {
            var command = ProcessCommandLine.TryRead(process.Id) ?? string.Empty;
            if (root is null && !command.Contains("--type=", StringComparison.Ordinal))
            {
                root = process;
            }
            else
            {
                process.Dispose();
            }
        }

        return root;
    }

    internal static int CountForProfile(string profileDir)
    {
        var list = FindForProfile(profileDir);
        var count = list.Count;
        foreach (var p in list) p.Dispose();
        return count;
    }

    /// <summary>
    /// Stops every process of that launch: roots first with their trees, then any stragglers. Bounded waits only.
    /// Returns how many processes were still running afterwards (0 means clean).
    /// </summary>
    internal static int StopForProfile(string profileDir)
    {
        var processes = FindForProfile(profileDir);
        foreach (var p in processes)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone, or access denied: the recount below tells the truth.
            }
        }

        foreach (var p in processes)
        {
            try
            {
                p.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ignore.
            }

            p.Dispose();
        }

        return CountForProfile(profileDir);
    }
}

/// <summary>Reads another process's command line (same user) with NtQueryInformationProcess; no extra packages.</summary>
internal static class ProcessCommandLine
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string? TryRead(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1 << 20) return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;

                // UNICODE_STRING { ushort Length; ushort MaximumLength; (padding on 64-bit); PWSTR Buffer }
                var length = (ushort)Marshal.ReadInt16(buffer, 0);
                var pointer = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
                return pointer == IntPtr.Zero || length == 0 ? string.Empty : Marshal.PtrToStringUni(pointer, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
