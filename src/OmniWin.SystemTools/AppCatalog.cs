using System.Diagnostics;
using System.Runtime.InteropServices;
using OmniWin.Core;

namespace OmniWin.SystemTools;

internal interface IAppCatalog
{
    Task<IReadOnlyList<InstalledApp>> ListAsync(CancellationToken ct);
}

/// <summary>Lists the same apps as the Start menu by reading the shell's AppsFolder on a dedicated STA thread.</summary>
internal sealed class ShellAppCatalog : IAppCatalog
{
    public Task<IReadOnlyList<InstalledApp>> ListAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<InstalledApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Enumerate(ct));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "omniwin-apps-list",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.WaitAsync(ct);
    }

    private static List<InstalledApp> Enumerate(CancellationToken ct)
    {
        var apps = new List<InstalledApp>();
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("The Windows shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return apps;
            foreach (dynamic item in folder.Items())
            {
                ct.ThrowIfCancellationRequested();
                string? name = item.Name;
                string? id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                apps.Add(new InstalledApp(name.Trim(), id.Trim(), null));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return apps;
    }
}

internal interface IShellLauncher
{
    /// <summary>Starts a Start-menu app by its AppsFolder id.</summary>
    Task StartAppAsync(string launchId, CancellationToken ct);

    /// <summary>Starts an allow-listed executable directly (no shell, no command processor). False when Windows started nothing.</summary>
    Task<bool> StartExecutableAsync(string path, string? args, CancellationToken ct);
}

internal sealed class ShellLauncher : IShellLauncher
{
    public Task StartAppAsync(string launchId, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        // The shell hands the launch to the app; a null process is normal for apps that are already running.
        using var process = Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + launchId) { UseShellExecute = true });
    }, ct);

    public Task<bool> StartExecutableAsync(string path, string? args, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        if (!string.IsNullOrWhiteSpace(args)) info.Arguments = args;
        using var process = Process.Start(info);
        return process is not null;
    }, ct);
}
