namespace OmniWin.Desktop.Tests;

public sealed class DesktopActivationTests
{
    [Fact]
    public void Cancelled_activation_checks_its_boundary_before_any_native_window_input()
    {
        // This is not a window or a special HWND constant: a broken boundary cannot activate it.
        const long invalidHandle = -99_999;
        var windowInput = false;
        var stopped = new OperationCanceledException("stop before activation");

        var error = Record.Exception(() => NativeDesktop.TryForeground(invalidHandle, out windowInput, out _, () => throw stopped));

        Assert.Same(stopped, error);
        Assert.False(windowInput);
    }
}
