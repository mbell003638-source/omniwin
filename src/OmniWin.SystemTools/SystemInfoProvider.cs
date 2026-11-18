using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Power;
using Windows.Win32.UI.HiDpi;

namespace OmniWin.SystemTools;

internal interface ISystemInfoProvider
{
    /// <summary>One of network | battery | displays | time. Returns short text lines; the caller validates the kind.</summary>
    Task<string> GetAsync(string kind, CancellationToken ct);
}

internal sealed class WindowsSystemInfo : ISystemInfoProvider
{
    public static readonly IReadOnlyList<string> Kinds = ["network", "battery", "displays", "time"];

    public Task<string> GetAsync(string kind, CancellationToken ct) => Task.Run(() => kind switch
    {
        "network" => Network(),
        "battery" => Battery(),
        "displays" => Displays(),
        "time" => Time(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown system_info kind."),
    }, ct);

    private static string Network()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var any = NetworkInterface.GetIsNetworkAvailable();
        sb.Append("connected=").Append(any ? "true" : "false");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Adapter name, type and speed only: no addresses.
            sb.Append('\n').Append("adapter ").Append(nic.NetworkInterfaceType.ToString().ToLowerInvariant())
              .Append(" \"").Append(nic.Name.Replace('"', '\'')).Append("\" speed_mbps=")
              .Append((nic.Speed / 1_000_000).ToString(inv));
        }

        return sb.ToString();
    }

    private static string Battery()
    {
        var inv = CultureInfo.InvariantCulture;
        if (!PInvoke.GetSystemPowerStatus(out var status)) return "battery=unknown";
        if (status.BatteryFlag == 128) return "battery=none ac=" + (status.ACLineStatus == 1 ? "online" : "unknown");

        var percent = status.BatteryLifePercent == 255 ? "unknown" : status.BatteryLifePercent.ToString(inv);
        var charging = (status.BatteryFlag & 8) != 0;
        var sb = new StringBuilder("battery_percent=").Append(percent)
            .Append(" ac=").Append(status.ACLineStatus switch { 0 => "offline", 1 => "online", _ => "unknown" })
            .Append(" charging=").Append(charging ? "true" : "false");
        if (status.BatteryLifeTime != uint.MaxValue)
        {
            sb.Append(" minutes_remaining=").Append((status.BatteryLifeTime / 60).ToString(inv));
        }

        return sb.ToString();
    }

    private static unsafe string Displays()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        var index = 0;
        MONITORENUMPROC callback = (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo(monitor, ref info)) return true;
            uint dpiX = 96;
            if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dx, out _).Succeeded) dpiX = dx;
            index++;
            var primary = (info.dwFlags & 1) != 0 ? " primary" : string.Empty;
            lines.Add($"display {index}{primary} {info.rcMonitor.Width}x{info.rcMonitor.Height} at {info.rcMonitor.left},{info.rcMonitor.top} " +
                      $"scale={(dpiX * 100 / 96).ToString(inv)}%");
            return true;
        };
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, callback, 0);
        return lines.Count == 0 ? "displays=unknown" : string.Join('\n', lines);
    }

    private static string Time()
    {
        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.Local;
        return string.Create(CultureInfo.InvariantCulture,
            $"local={now:yyyy-MM-dd HH:mm:ss} utc_offset={now.Offset:hh\\:mm} timezone=\"{zone.Id}\" utc={DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
    }
}
