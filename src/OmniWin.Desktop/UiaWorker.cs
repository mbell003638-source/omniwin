using System.Runtime.InteropServices;
using System.Threading.Channels;
using Interop.UIAutomationClient;
using OmniWin.Safety;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace OmniWin.Desktop;

/// <summary>Runs synchronous UIA work on one MTA thread. No UIA object is returned to a caller.</summary>
internal sealed class UiaWorker : IAsyncDisposable
{
    private readonly Channel<IWork> _queue = Channel.CreateUnbounded<IWork>(new()
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string _state = "starting";

    internal string State => _state;

    /// <param name="createAutomation">False only in tests that give the engine a fake backend: no UI Automation object is created and the
    /// commands receive null (a fake never touches it).</param>
    internal UiaWorker(string name, TimeoutsConfig timeouts, Action<IUIAutomation>? shutdown = null, bool createAutomation = true)
    {
        var thread = new Thread(() => Run(timeouts, shutdown, createAutomation)) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal async Task<T> InvokeAsync<T>(Func<IUIAutomation, CancellationToken, T> command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var work = new Work<T>(command, ct);
        using var registration = ct.UnsafeRegister(static state => ((IWork)state!).CancelIfQueued(), work);
        await _queue.Writer.WriteAsync(work, ct).ConfigureAwait(false);
        return await work.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // Bounded: if the worker is stuck inside a UI Automation call (for example no desktop is attached because the
        // session is disconnected), shutdown must not wait forever. The thread is a background thread, so the process can exit.
        try
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Give up waiting; the background thread ends with the process.
        }
    }

    private void Run(TimeoutsConfig timeouts, Action<IUIAutomation>? shutdown, bool createAutomation)
    {
        IUIAutomation2? automation = null;
        try
        {
            PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (createAutomation)
            {
                automation = new CUIAutomation8();
                automation.ConnectionTimeout = (uint)timeouts.UiaConnectionMs;
                automation.TransactionTimeout = (uint)timeouts.UiaTransactionMs;
                automation.AutoSetFocus = 0;
            }

            _state = "ready";

            // Block only this dedicated thread. Awaiting here would move COM calls onto the thread pool.
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out var work))
                {
                    work.Execute(automation!);
                }
            }
        }
        catch (Exception ex)
        {
            _state = "unavailable";
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var work))
            {
                work.Fail(ex);
            }
        }
        finally
        {
            try
            {
                if (automation is not null)
                {
                    shutdown?.Invoke(automation);
                }
            }
            catch (COMException)
            {
                // The worker is exiting even if the desktop was disconnected during cleanup.
            }
            finally
            {
                _stopped.TrySetResult();
            }
        }
    }

    private interface IWork
    {
        void Execute(IUIAutomation automation);
        void Fail(Exception error);
        void CancelIfQueued();
    }

    private sealed class Work<T>(Func<IUIAutomation, CancellationToken, T> command, CancellationToken ct) : IWork
    {
        // Cancellation can finish queued work immediately. Once running, only the worker completes it,
        // so the caller cannot report input_sent=false while a native dispatch is still in progress.
        private int _claimed;
        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Execute(IUIAutomation automation)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                Completion.TrySetResult(command(automation, ct));
            }
            catch (OperationCanceledException ex)
            {
                Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public void Fail(Exception error) => Completion.TrySetException(error);

        public void CancelIfQueued()
        {
            if (Interlocked.Exchange(ref _claimed, 1) == 0)
            {
                Completion.TrySetCanceled(ct);
            }
        }
    }
}
