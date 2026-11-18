using System.Globalization;
using Microsoft.Extensions.Logging;
using OmniWin.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace OmniWin.Safety;

/// <summary>Owns the stop hotkey and real-input hooks on one message-loop thread.</summary>
public sealed class KillSwitch : IKillSwitch, IUserActivity, IComponentStatus, IDisposable
{
    private const int HotkeyId = 0x4F57;
    private readonly Lock _sync = new();
    private readonly ILogger<KillSwitch> _log;
    private readonly string _hotkey;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly List<Arm> _retired = [];
    private readonly HOOKPROC _keyboardCallback;
    private readonly HOOKPROC _mouseCallback;
    private readonly UserActivityTracker _activity;
    private readonly bool _installActivityHooks;
    private Arm _arm = new();
    private volatile bool _tripped;
    private volatile string _state = "starting";
    private volatile string _activityState = "starting";
    private uint _threadId;
    private int _disposed;

    public KillSwitch(OmniWinConfig config, ILogger<KillSwitch> log) : this(config, log, startThread: true)
    {
    }

    internal KillSwitch(OmniWinConfig config, ILogger<KillSwitch> log, bool startThread, bool installActivityHooks = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hotkey = config.KillHotkey;
        _activity = new UserActivityTracker(config.UserActivityCooldownMs);
        _installActivityHooks = installActivityHooks;
        _keyboardCallback = KeyboardInput;
        _mouseCallback = MouseInput;
        ActivityStatus = new ActivityComponent(this);
        if (startThread)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "omniwin-safety" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(2)))
            {
                _log.LogWarning("Safety thread startup is taking longer than expected.");
            }
        }
        else
        {
            _state = "disabled";
            _activityState = "disabled";
        }
    }

    public bool IsTripped => _tripped;
    public CancellationToken Token => Volatile.Read(ref _arm).Token;
    public bool UserActiveRecently => _activity.UserActiveRecently;
    public DateTimeOffset LastRealInput => _activity.LastRealInput;
    public string Component => "kill_switch";
    public string State => Volatile.Read(ref _disposed) != 0 ? "off" : _tripped ? "stopped" : _state;
    public string? Detail => _hotkey;
    public IComponentStatus ActivityStatus { get; }

    public void Toggle()
    {
        Arm? cancel = null;
        bool stopped;
        lock (_sync)
        {
            if (_disposed != 0) return;
            stopped = !_tripped;
            if (stopped)
            {
                _tripped = true;
                cancel = _arm;
            }
            else
            {
                // Keep issued tokens usable for the server session, including callers registering after resume.
                _retired.Add(_arm);
                Volatile.Write(ref _arm, new Arm());
                _tripped = false;
            }
        }

        if (cancel is not null) Cancel(cancel);
        _log.LogWarning(stopped ? "Stop key pressed. All input actions cancelled." : "Stop key pressed again. OmniWin resumed.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Arm current;
        lock (_sync)
        {
            _tripped = true;
            current = _arm;
        }

        Cancel(current);
        var threadId = Volatile.Read(ref _threadId);
        if (_thread is { IsAlive: true } thread && Thread.CurrentThread != thread)
        {
            if (threadId != 0) PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);
            thread.Join(TimeSpan.FromSeconds(2));
        }

        current.Source.Dispose();
        foreach (var arm in _retired) arm.Source.Dispose();
        if (_thread is not { IsAlive: true }) _started.Dispose();
    }

    private void Cancel(Arm arm)
    {
        try
        {
            arm.Source.Cancel();
        }
        catch (AggregateException ex)
        {
            _log.LogError(ex, "An input cancellation callback failed.");
        }
        catch (ObjectDisposedException) when (_disposed != 0)
        {
            // A concurrent shutdown already cancelled and retired this source.
        }
    }

    private unsafe void Run()
    {
        var registered = false;
        HHOOK keyboard = default;
        HHOOK mouse = default;
        try
        {
            // Posting WM_QUIT is valid only after this thread owns a message queue.
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            Volatile.Write(ref _threadId, PInvoke.GetCurrentThreadId());
            if (TryParseHotkey(_hotkey, out var modifiers, out var vk))
            {
                registered = PInvoke.RegisterHotKey(HWND.Null, HotkeyId, modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, vk);
            }

            _state = registered ? "armed" : "unavailable";
            if (!registered) _log.LogError("Could not register the stop key {Hotkey}.", _hotkey);

            if (_installActivityHooks)
            {
                var module = PInvoke.GetModuleHandle(default(PCWSTR));
                var instance = new HINSTANCE(module.Value);
                keyboard = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardCallback, instance, 0);
                mouse = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseCallback, instance, 0);
                _activityState = keyboard.IsNull || mouse.IsNull ? "unavailable" : "on";
                if (_activityState == "unavailable") _log.LogError("Could not install both real-input hooks.");
            }
            else
            {
                // Only the internal fixture constructor can isolate hook overhead. The public server
                // constructor always installs the hooks; the stop hotkey remains registered here.
                _activityState = "disabled_for_test";
            }
            _started.Set();

            while (_disposed == 0)
            {
                var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0).Value;
                if (result <= 0)
                {
                    if (result < 0) _state = "unavailable";
                    break;
                }

                if (message.message == PInvoke.WM_HOTKEY && (int)message.wParam.Value == HotkeyId) Toggle();
            }
        }
        catch (Exception ex)
        {
            _state = "unavailable";
            _activityState = "unavailable";
            _log.LogError(ex, "Safety message loop failed.");
        }
        finally
        {
            if (!keyboard.IsNull) PInvoke.UnhookWindowsHookEx(keyboard);
            if (!mouse.IsNull) PInvoke.UnhookWindowsHookEx(mouse);
            if (registered) PInvoke.UnregisterHotKey(HWND.Null, HotkeyId);
            _state = _disposed != 0 ? "off" : "unavailable";
            _activityState = _disposed != 0 ? "off" : "unavailable";
            _started.Set();
        }
    }

    private unsafe LRESULT KeyboardInput(int code, WPARAM message, LPARAM data)
    {
        if (code >= 0)
        {
            var input = (KBDLLHOOKSTRUCT*)data.Value;
            _activity.Record(((uint)input->flags & 0x10) != 0);
        }

        return PInvoke.CallNextHookEx(default(HHOOK), code, message, data);
    }

    private unsafe LRESULT MouseInput(int code, WPARAM message, LPARAM data)
    {
        if (code >= 0)
        {
            var input = (MSLLHOOKSTRUCT*)data.Value;
            _activity.Record((input->flags & 0x01) != 0);
        }

        return PInvoke.CallNextHookEx(default(HHOOK), code, message, data);
    }

    internal static bool TryParseHotkey(string text, out HOT_KEY_MODIFIERS modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = raw.ToLowerInvariant();
            var modifier = part switch
            {
                "ctrl" or "control" => HOT_KEY_MODIFIERS.MOD_CONTROL,
                "alt" => HOT_KEY_MODIFIERS.MOD_ALT,
                "shift" => HOT_KEY_MODIFIERS.MOD_SHIFT,
                "win" => HOT_KEY_MODIFIERS.MOD_WIN,
                _ => (HOT_KEY_MODIFIERS)0,
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }

            if (vk != 0) return false;
            vk = part switch
            {
                { Length: 1 } when char.IsAsciiLetterOrDigit(part[0]) => char.ToUpperInvariant(part[0]),
                ['f', .. var number] when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24
                    => (uint)(0x70 + f - 1),
                "delete" or "del" => 0x2E, "insert" or "ins" => 0x2D, "space" => 0x20,
                "enter" or "return" => 0x0D, "tab" => 0x09, "escape" or "esc" => 0x1B,
                _ => 0,
            };
            if (vk == 0) return false;
        }

        return vk != 0 && modifiers != 0;
    }

    private sealed class Arm
    {
        internal CancellationTokenSource Source { get; } = new();
        internal CancellationToken Token { get; }

        internal Arm() => Token = Source.Token;
    }

    private sealed class ActivityComponent(KillSwitch owner) : IComponentStatus
    {
        public string Component => "user_activity";
        public string State => Volatile.Read(ref owner._disposed) != 0 ? "off" : owner._activityState;
        public string? Detail => "non-injected keyboard and mouse input";
    }
}

internal sealed class UserActivityTracker(int cooldownMs, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastTimestamp;
    private long _lastUtcTicks;

    internal bool UserActiveRecently
    {
        get
        {
            var timestamp = Interlocked.Read(ref _lastTimestamp);
            return Interlocked.Read(ref _lastUtcTicks) != 0 && cooldownMs > 0 &&
                _time.GetElapsedTime(timestamp).TotalMilliseconds < cooldownMs;
        }
    }

    internal DateTimeOffset LastRealInput => new(Interlocked.Read(ref _lastUtcTicks), TimeSpan.Zero);

    internal void Record(bool injected)
    {
        if (injected) return;
        Interlocked.Exchange(ref _lastUtcTicks, _time.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _lastTimestamp, _time.GetTimestamp());
    }
}
