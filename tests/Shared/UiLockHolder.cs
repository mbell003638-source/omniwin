namespace OmniWin.Tests.Shared;

/// <summary>
/// Holds the shared UI test lock (a named mutex such as <c>Local\OmniWin.UiInputTestLock</c>) on one dedicated thread. A named
/// mutex must be released by the thread that took it, and async tests resume on other threads, so the lock lives on its own
/// thread until it is disposed. One copy, linked into every test project that needs it (Integration, Ocr.Tests, SystemTools.Tests).
/// Disposing twice is harmless.
/// </summary>
internal sealed class UiLockHolder : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _thread;
    private int _disposed;

    private UiLockHolder(string name, TaskCompletionSource<bool> acquired, TimeSpan timeout)
    {
        _thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            bool taken;
            try
            {
                taken = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                taken = true;   // a crashed earlier run left it; this thread owns it now
            }

            acquired.SetResult(taken);
            if (!taken) return;

            _release.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true, Name = "OmniWin UI lock holder" };
    }

    public static async Task<UiLockHolder> AcquireAsync(string name, TimeSpan timeout)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new UiLockHolder(name, acquired, timeout);
        holder._thread.Start();
        if (!await acquired.Task.ConfigureAwait(false))
        {
            holder.Dispose();   // the thread has already ended without the lock; this frees the event too
            throw new TimeoutException("Could not take " + name + " within " + timeout + ".");
        }

        return holder;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _release.Dispose();
    }
}
