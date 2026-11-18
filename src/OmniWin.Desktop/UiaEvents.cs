using System.Runtime.InteropServices;
using System.Threading.Channels;
using Interop.UIAutomationClient;
using OmniWin.Safety;
using static Interop.UIAutomationClient.UIA_EventIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace OmniWin.Desktop;

internal sealed class UiaEvents : IAsyncDisposable
{
    private readonly UiaWorker _worker;
    private readonly UiaChangeTracker _changes = new();
    private readonly ChangeHandler _handler;
    private readonly WindowHandler _windowHandler;
    private readonly Task<bool> _registered;

    internal UiaEvents(TimeoutsConfig timeouts) : this(timeouts, registerEvents: true)
    {
    }

    /// <summary>Allows read-only diagnostics to exercise missing-provider-event fallback without global settings.</summary>
    internal UiaEvents(TimeoutsConfig timeouts, bool registerEvents, bool createAutomation = true)
    {
        _handler = new ChangeHandler(_changes);
        _windowHandler = new WindowHandler(_changes);
        _worker = new UiaWorker("uia-events", timeouts, uia => uia.RemoveAllEventHandlers(), createAutomation);
        _registered = registerEvents ? RegisterAsync() : Task.FromResult(false);
    }

    internal long Version => _changes.Version;
    internal long WindowVersion => _changes.WindowVersion;
    internal long ModalVersion => _changes.ModalVersion;
    internal bool Available => _registered.IsCompletedSuccessfully && _registered.GetAwaiter().GetResult();

    internal async Task WaitForChangeAsync(long version, CancellationToken ct)
    {
        // Some providers omit change events. Recheck at 25 ms only when no signal has arrived.
        var signal = _changes.After(version);
        await Task.WhenAny(signal, Task.Delay(25, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync() => _worker.DisposeAsync();

    private async Task<bool> RegisterAsync()
    {
        try
        {
            return await _worker.InvokeAsync((uia, _) =>
            {
                var root = uia.GetRootElement();
                uia.AddStructureChangedEventHandler(root, TreeScope.TreeScope_Subtree, null, _handler);
                uia.AddFocusChangedEventHandler(null, _handler);
                uia.AddPropertyChangedEventHandler(root, TreeScope.TreeScope_Subtree, null, _handler,
                    [UIA_NamePropertyId, UIA_BoundingRectanglePropertyId, UIA_IsEnabledPropertyId,
                     UIA_IsOffscreenPropertyId, UIA_ValueValuePropertyId, UIA_ToggleToggleStatePropertyId,
                     UIA_SelectionItemIsSelectedPropertyId, UIA_ExpandCollapseExpandCollapseStatePropertyId,
                     UIA_ScrollHorizontalScrollPercentPropertyId, UIA_ScrollVerticalScrollPercentPropertyId,
                     UIA_WindowIsModalPropertyId]);
                // Desktop-child changes invalidate discovery. Value, focus, and descendant layout events only
                // invalidate control observations, avoiding a desktop-wide UIA scan on every keystroke.
                uia.AddStructureChangedEventHandler(root, TreeScope.TreeScope_Element, null, _windowHandler);
                uia.AddPropertyChangedEventHandler(root, TreeScope.TreeScope_Children, null, _windowHandler,
                    [UIA_NamePropertyId, UIA_BoundingRectanglePropertyId, UIA_IsEnabledPropertyId,
                     UIA_IsOffscreenPropertyId, UIA_WindowIsModalPropertyId, UIA_WindowWindowVisualStatePropertyId]);
                uia.AddAutomationEventHandler(UIA_Window_WindowOpenedEventId, root, TreeScope.TreeScope_Subtree, null, _handler);
                uia.AddAutomationEventHandler(UIA_Window_WindowClosedEventId, root, TreeScope.TreeScope_Subtree, null, _handler);
                uia.AddAutomationEventHandler(UIA_Text_TextChangedEventId, root, TreeScope.TreeScope_Subtree, null, _handler);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ChannelClosedException)
        {
            return false;
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ChangeHandler(UiaChangeTracker changes) : IUIAutomationStructureChangedEventHandler, IUIAutomationFocusChangedEventHandler,
        IUIAutomationPropertyChangedEventHandler, IUIAutomationEventHandler
    {
        public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, int[] runtimeId) => changes.Notify();
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => changes.Notify();
        public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue) =>
            changes.Notify(modalChanged: propertyId == UIA_WindowIsModalPropertyId);
        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) =>
            changes.Notify(eventId is UIA_Window_WindowOpenedEventId or UIA_Window_WindowClosedEventId);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class WindowHandler(UiaChangeTracker changes) : IUIAutomationStructureChangedEventHandler,
        IUIAutomationPropertyChangedEventHandler
    {
        public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, int[] runtimeId) => changes.Notify(windowChanged: true);
        public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue) =>
            changes.Notify(windowChanged: true, modalChanged: propertyId == UIA_WindowIsModalPropertyId);
    }
}

/// <summary>Event callbacks update only these counters and signals. They never inspect a provider.</summary>
internal sealed class UiaChangeTracker
{
    private readonly Lock _sync = new();
    private TaskCompletionSource? _signal;
    private long _version;
    private long _windowVersion;
    private long _modalVersion;

    internal long Version => Interlocked.Read(ref _version);
    internal long WindowVersion => Interlocked.Read(ref _windowVersion);
    internal long ModalVersion => Interlocked.Read(ref _modalVersion);

    internal Task After(long version)
    {
        lock (_sync)
        {
            return _version != version
                ? Task.CompletedTask
                : (_signal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    internal void Notify(bool windowChanged = false, bool modalChanged = false)
    {
        lock (_sync)
        {
            Interlocked.Increment(ref _version);
            if (windowChanged || modalChanged) Interlocked.Increment(ref _windowVersion);
            if (modalChanged) Interlocked.Increment(ref _modalVersion);
            _signal?.TrySetResult();
            _signal = null;
        }
    }
}
