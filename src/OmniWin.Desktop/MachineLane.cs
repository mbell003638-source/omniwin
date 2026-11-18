using System.Diagnostics;

namespace OmniWin.Desktop;

/// <summary>
/// One lane for real input shared by every OmniWin process in this Windows session (contract 2.6): the server an AI client started
/// and the one the OmniWin app started must never move the mouse or type at the same moment. It is an exclusively opened lock file,
/// so Windows frees it by itself when the holding process ends or crashes; nothing can be left locked. It has no thread affinity,
/// so an async act may release it on another thread.
/// </summary>
internal sealed class MachineLane
{
    internal const int RetryMs = 15;
    private const int BriefTries = 12;   // about 180 ms: long enough for another process's click, short enough to answer "busy" at once

    private readonly string _path;
    private readonly object _gate = new();
    private FileStream? _held;

    internal MachineLane(string path) => _path = path;

    /// <summary>The lock file for this Windows session, in the user's temp folder (one per session, shared by every data dir).</summary>
    internal static MachineLane ForThisSession()
    {
        var session = Process.GetCurrentProcess().SessionId;
        return new MachineLane(Path.Combine(Path.GetTempPath(), $"omniwin-input-lane-{session}.lock"));
    }

    internal string LockFile => _path;

    internal bool TryEnter()
    {
        lock (_gate)
        {
            if (_held is not null) return false;   // this process already holds it (the in-process lane should have stopped this)
            try
            {
                _held = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return true;
            }
            catch (IOException)
            {
                return false;   // another process holds it
            }
            catch (UnauthorizedAccessException)
            {
                return true;    // the temp folder cannot be written: run without the shared lane rather than refuse all input
            }
        }
    }

    /// <summary>A few quick tries, for callers that must answer at once when something long holds the lane.</summary>
    internal bool TryEnterBriefly()
    {
        for (var i = 0; i < BriefTries; i++)
        {
            if (TryEnter()) return true;
            Thread.Sleep(RetryMs);
        }

        return false;
    }

    internal void Exit()
    {
        lock (_gate)
        {
            _held?.Dispose();
            _held = null;
        }
    }
}
