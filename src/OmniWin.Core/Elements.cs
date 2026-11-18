namespace OmniWin.Core;

// ---------- element descriptions (used by Safety and Workflows, contract §6) ----------

public sealed record SelectorSegment(string Role, string? AutomationId, string? Name, string? ClassName, int? Index);

public sealed record ElementDescriptor(
    string Ref,
    string Engine,                    // "desktop" | "browser"
    string Process,                   // "notepad.exe" / "msedge.exe"
    string WindowTitle,
    string? Url,                      // browser only
    string Role,
    string Name,
    string? AutomationId,             // desktop AutomationId, or browser id / data-testid
    bool IsPassword,
    bool IsEnabled,
    IReadOnlyList<SelectorSegment> Path);   // from the top-level window / document root

// ---------- windows & tabs ----------

public sealed record WindowInfo(
    string Id, long Hwnd, int Pid, string Process, string Title,
    bool IsForeground, bool IsMinimized, bool IsModal, string? OwnerId,
    string? Inaccessible);            // null, "elevated", "secure_desktop", "blocked_by_policy"

public sealed record TabInfo(string Id, string Url, string Title, bool Active);
