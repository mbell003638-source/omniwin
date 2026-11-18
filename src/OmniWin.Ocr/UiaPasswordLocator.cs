using OmniWin.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace OmniWin.Ocr;

/// <summary>Finds the password fields of a window, as screen rectangles. A failure throws: the caller then returns no image.</summary>
internal interface IPasswordLocator
{
    Task<IReadOnlyList<ScreenRect>> FindPasswordRectsAsync(long hwnd, CancellationToken ct);
}

/// <summary>
/// UI Automation lookup of every element whose <c>IsPassword</c> is true in a window (read-only; no pattern is invoked
/// and nothing is focused). It runs on its own thread because UI Automation calls cross process boundaries.
/// </summary>
internal sealed class UiaPasswordLocator : IPasswordLocator
{
    public Task<IReadOnlyList<ScreenRect>> FindPasswordRectsAsync(long hwnd, CancellationToken ct) =>
        Task.Factory.StartNew(() => Find(hwnd, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static IReadOnlyList<ScreenRect> Find(long hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var automation = (IUIAutomation2)new CUIAutomation8();
        var root = automation.ElementFromHandle(new HWND((nint)hwnd));
        var condition = automation.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_IsPasswordPropertyId, true);
        var found = root.FindAll(TreeScope.TreeScope_Descendants, condition);

        var rects = new List<ScreenRect>();
        var length = found.Length;
        for (var i = 0; i < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bounds = found.GetElement(i).CurrentBoundingRectangle;
            var width = bounds.right - bounds.left;
            var height = bounds.bottom - bounds.top;
            if (width > 0 && height > 0)
            {
                rects.Add(new ScreenRect(bounds.left, bounds.top, width, height));
            }
        }

        return rects;
    }
}
