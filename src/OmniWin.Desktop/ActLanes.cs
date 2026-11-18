using System.Collections.Concurrent;

namespace OmniWin.Desktop;

/// <summary>
/// Decides which acts may run at the same time. There are two kinds of lock:
/// <list type="bullet">
/// <item><b>The physical lane</b> (one lock). Real mouse and keyboard input shares one cursor and one keyboard focus, so
/// acts that use it run strictly one at a time, as before.</item>
/// <item><b>Window locks</b> (one per window). Two acts on the same window never overlap. Acts that use only UI Automation
/// patterns (invoke, set_value, toggle and so on) need no physical lane, so pattern acts on different windows run at the
/// same time, and with a physical act that is working on another window.</item>
/// </list>
/// Lock order is always physical lane, then window lock. A pattern act never waits for the physical lane, so the order has no
/// cycle and the locks cannot deadlock. Every wait takes the caller's cancellation token, which is how the stop key reaches
/// every lane at once.
/// </summary>
internal sealed class ActLanes : IDisposable
{
    private readonly SemaphoreSlim _physical = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _windows = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>What the act that holds the physical lane is doing, when it said so (a long <c>type</c>); null otherwise.</summary>
    internal const string TypeLabel = "type";

    /// <summary>A bridged engine call (Windows-MCP, agent-desktop) holds the lane.</summary>
    internal const string EngineLabel = "engine";

    /// <summary>The sentence an act gets when the lane is held, naming what holds it.</summary>
    internal static string BusyDetail(string? holder) => holder == EngineLabel
        ? "An engine call (another computer-use engine) holds the keyboard and mouse. Try again when it has finished."
        : holder == OtherProcessLabel
        ? "Another OmniWin process (for example the OmniWin app) is sending input right now. Try again in a moment."
        : "A type action is running and holds the keyboard and mouse. Try again when it has finished; reads (observe, wait) are still answered.";

    /// <summary>Another OmniWin process on this desktop holds the machine-wide lane.</summary>
    internal const string OtherProcessLabel = "other_process";

    private volatile string? _physicalHolder;

    /// <summary>
    /// The lane shared by every OmniWin process in this Windows session (contract 2.6). Null in tests and wherever a single process
    /// is all there is; the server sets it at start-up. Taken after the in-process lane and released before it.
    /// </summary>
    internal MachineLane? Machine { get; set; }

    internal string? PhysicalHolder => _physicalHolder;

    /// <summary>Waits for the physical lane. Add the window lock with <see cref="Lease.EnterWindowAsync"/> once the window is known.</summary>
    internal async Task<Lease> EnterPhysicalAsync(CancellationToken ct, string? label = null)
    {
        await _physical.WaitAsync(ct).ConfigureAwait(false);
        if (Machine is { } machine)
        {
            try
            {
                // Another process's act is short (a long type or an engine call is refused by TryEnterPhysical first), so wait for it.
                while (!machine.TryEnter()) await Task.Delay(MachineLane.RetryMs, ct).ConfigureAwait(false);
            }
            catch
            {
                _physical.Release();
                throw;
            }
        }

        _physicalHolder = label;
        return new Lease(this, physicalHeld: true);
    }

    /// <summary>Takes the physical lane only if it is free right now. Null when something holds it (see <see cref="PhysicalHolder"/>).</summary>
    internal Lease? TryEnterPhysical(string? label = null)
    {
        if (!_physical.Wait(0)) return null;
        if (Machine is { } machine && !machine.TryEnterBriefly())
        {
            // Held by another OmniWin process: say so at once instead of fighting over the pointer.
            _physicalHolder = OtherProcessLabel;
            _physical.Release();
            return null;
        }

        _physicalHolder = label;
        return new Lease(this, physicalHeld: true);
    }

    /// <summary>Waits for one window's lock only. For acts that do not use the shared mouse and keyboard.</summary>
    internal async Task<Lease> EnterWindowAsync(string windowKey, CancellationToken ct)
    {
        var lease = new Lease(this, physicalHeld: false);
        try
        {
            await lease.EnterWindowAsync(windowKey, ct).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal int PhysicalFree => _physical.CurrentCount;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Machine?.Exit();
        _physical.Dispose();
        foreach (var gate in _windows.Values) gate.Dispose();
    }

    private SemaphoreSlim GateFor(string windowKey) => _windows.GetOrAdd(windowKey, static _ => new SemaphoreSlim(1, 1));

    internal sealed class Lease(ActLanes owner, bool physicalHeld) : IDisposable
    {
        private SemaphoreSlim? _window;
        private bool _physical = physicalHeld;
        private bool _released;

        internal bool HoldsPhysical => _physical;

        internal string? WindowKey { get; private set; }

        /// <summary>Takes the window's lock. Calling it again for the same window is a no-op; a lease holds one window.</summary>
        internal async Task EnterWindowAsync(string windowKey, CancellationToken ct)
        {
            if (_window is not null)
            {
                if (WindowKey == windowKey) return;
                throw new InvalidOperationException("A lease holds one window lock.");
            }

            var gate = owner.GateFor(windowKey);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            _window = gate;
            WindowKey = windowKey;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try
            {
                _window?.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown already disposed the gate.
            }

            if (_physical)
            {
                _physical = false;
                owner._physicalHolder = null;
                owner.Machine?.Exit();
                try
                {
                    owner._physical.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown already disposed the lane.
                }
            }
        }
    }
}
