using System.Globalization;
using System.Drawing;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.UI.Input.KeyboardAndMouse.KEYBD_EVENT_FLAGS;
using static Windows.Win32.UI.Input.KeyboardAndMouse.MOUSE_EVENT_FLAGS;
using static Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY;

namespace OmniWin.Desktop;

[Flags]
internal enum KeyModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

internal sealed record KeyChord(KeyModifiers Modifiers, VIRTUAL_KEY Key, string Name)
{
    internal string Canonical => (Modifiers.HasFlag(KeyModifiers.Control) ? "ctrl+" : "") +
        (Modifiers.HasFlag(KeyModifiers.Alt) ? "alt+" : "") + (Modifiers.HasFlag(KeyModifiers.Shift) ? "shift+" : "") +
        (Modifiers.HasFlag(KeyModifiers.Windows) ? "win+" : "") + Name;
}

internal sealed record KeySequence(IReadOnlyList<KeyChord> Chords)
{
    internal string Canonical => string.Join(' ', Chords.Select(chord => chord.Canonical));

    internal static KeySequence? Parse(string? value)
    {
        var parts = value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: > 0 and <= 64 })
        {
            return null;
        }

        var chords = new List<KeyChord>(parts.Length);
        foreach (var part in parts)
        {
            var tokens = part.ToLowerInvariant().Split('+');
            var modifiers = KeyModifiers.None;
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                var modifier = tokens[i] switch
                {
                    "ctrl" or "control" => KeyModifiers.Control,
                    "alt" => KeyModifiers.Alt,
                    "shift" => KeyModifiers.Shift,
                    "win" => KeyModifiers.Windows,
                    _ => KeyModifiers.None,
                };
                if (modifier == KeyModifiers.None || modifiers.HasFlag(modifier))
                {
                    return null;
                }

                modifiers |= modifier;
            }

            var name = tokens[^1] switch
            {
                "return" => "enter", "esc" => "escape", "del" => "delete", "ins" => "insert",
                "pgup" => "pageup", "pgdn" => "pagedown", "bs" => "backspace", var keyName => keyName,
            };
            var key = ParseKey(name);
            if (key == 0)
            {
                return null;
            }

            chords.Add(new KeyChord(modifiers, key, name));
        }

        return new KeySequence(chords);
    }

    private static VIRTUAL_KEY ParseKey(string key) => key switch
    {
        "enter" => VK_RETURN, "escape" => VK_ESCAPE, "delete" => VK_DELETE, "backspace" => VK_BACK,
        "space" => VK_SPACE, "tab" => VK_TAB, "insert" => VK_INSERT, "home" => VK_HOME, "end" => VK_END,
        "pageup" => VK_PRIOR, "pagedown" => VK_NEXT, "up" => VK_UP, "down" => VK_DOWN, "left" => VK_LEFT, "right" => VK_RIGHT,
        { Length: 1 } when char.IsAsciiLetterOrDigit(key[0]) => (VIRTUAL_KEY)char.ToUpperInvariant(key[0]),
        ['f', .. var number] when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24
            => (VIRTUAL_KEY)((int)VK_F1 + n - 1),
        _ => 0,
    };
}

internal readonly record struct InputReceipt(int Sent, int Requested, double DispatchMs = 0)
{
    internal bool Complete => Sent == Requested;
}

internal static class NativeInput
{
    // Fixture-only diagnostic entry points. Production clicks retain a single atomic input batch.
    internal static InputReceipt DiagnosticMove(Point point) => Send([MoveTo(point)]);

    internal static InputReceipt DiagnosticButtons()
    {
        var swapped = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
        var up = Mouse(swapped ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
        var receipt = Send([Mouse(swapped ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN), up]);
        if (receipt.Sent == 1) Send([up]);
        return receipt;
    }

    internal static InputReceipt Click(Point point, bool right, bool doubleClick)
    {
        var move = MoveTo(point);
        var swapped = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
        var secondary = right != swapped;
        var down = Mouse(secondary ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
        var up = Mouse(secondary ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
        Span<INPUT> batch = [move, down, up, down, up];
        var receipt = Send(batch[..(doubleClick ? 5 : 3)]);
        if (receipt.Sent is 2 or 4)
        {
            Send([up]);
        }

        return receipt;
    }

    /// <summary>Moves the pointer onto a point and presses nothing (hover).</summary>
    internal static InputReceipt Hover(Point point) => Send([MoveTo(point)]);

    private const int DragSteps = 8;
    private const int DragStepMs = 12;

    /// <summary>
    /// Press the primary button at <paramref name="from"/>, move to <paramref name="to"/> in small timed steps (apps start a drag
    /// only after real movement), and release there. The release is ALWAYS sent once the press was accepted, also if a move fails
    /// or throws, so the button can never be left held down.
    /// </summary>
    internal static InputReceipt Drag(Point from, Point to)
    {
        var start = Stopwatch.GetTimestamp();
        var swapped = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
        var down = Mouse(swapped ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN);
        var up = Mouse(swapped ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP);
        var requested = 2 + DragSteps + 1 + 1;   // move+press, steps, final move, release
        var sent = 0;
        var pressed = false;
        try
        {
            var first = Send([MoveTo(from), down]);
            sent += first.Sent;
            pressed = first.Sent == 2;
            if (!pressed) return new InputReceipt(sent, requested, Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            for (var i = 1; i <= DragSteps; i++)
            {
                Thread.Sleep(DragStepMs);
                var point = new Point(from.X + (to.X - from.X) * i / (DragSteps + 1), from.Y + (to.Y - from.Y) * i / (DragSteps + 1));
                var step = Send([MoveTo(point)]);
                sent += step.Sent;
                if (step.Sent != 1) return new InputReceipt(sent, requested, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            Thread.Sleep(DragStepMs);
            var last = Send([MoveTo(to)]);
            sent += last.Sent;
        }
        finally
        {
            if (pressed)
            {
                var release = Send([up]);
                sent += release.Sent;
            }
        }

        return new InputReceipt(sent, requested, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    internal static InputReceipt Scroll(Point point, string direction, int amount)
    {
        var wheel = direction is "left" or "right" ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        var delta = amount * (direction is "up" or "right" ? 120 : -120);
        return Send([MoveTo(point), Mouse(wheel, unchecked((uint)delta))]);
    }

    internal static InputReceipt Type(ReadOnlySpan<char> text)
    {
        Span<INPUT> batch = stackalloc INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            batch[i * 2] = Keyboard(0, text[i], KEYEVENTF_UNICODE);
            batch[i * 2 + 1] = Keyboard(0, text[i], KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }

        var receipt = Send(batch);
        if (receipt.Sent % 2 != 0)
        {
            Send(batch.Slice(receipt.Sent, 1));
        }

        return receipt;
    }

    internal static InputReceipt Keys(KeyChord chord)
    {
        Span<VIRTUAL_KEY> keys = stackalloc VIRTUAL_KEY[5];
        var count = 0;
        if (chord.Modifiers.HasFlag(KeyModifiers.Control)) keys[count++] = VK_LCONTROL;
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) keys[count++] = VK_LMENU;
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) keys[count++] = VK_LSHIFT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Windows)) keys[count++] = VK_LWIN;
        keys[count++] = chord.Key;

        Span<INPUT> batch = stackalloc INPUT[count * 2];
        for (var i = 0; i < count; i++)
        {
            var key = keys[i];
            var extended = key is VK_LWIN or VK_INSERT or VK_DELETE or VK_HOME or VK_END or VK_PRIOR or VK_NEXT or
                VK_UP or VK_DOWN or VK_LEFT or VK_RIGHT ? KEYEVENTF_EXTENDEDKEY : 0;
            batch[i] = Keyboard(key, 0, extended);
            batch[batch.Length - i - 1] = Keyboard(key, 0, extended | KEYEVENTF_KEYUP);
        }

        var receipt = Send(batch);
        if (!receipt.Complete)
        {
            // SendInput inserts a prefix. Release only synthetic keys in that prefix that are still down.
            var firstRelease = Math.Max(receipt.Sent, batch.Length - receipt.Sent);
            Send(batch[firstRelease..]);
        }

        return receipt;
    }

    private static INPUT MoveTo(Point point)
    {
        var screen = NativeDesktop.VirtualScreen;
        if (!NativeDesktop.Contains(screen, point) || screen.Width > 65536 || screen.Height > 65536)
        {
            throw new InvalidOperationException("Cannot address that point on the virtual screen.");
        }

        var input = Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE_NOCOALESCE);
        input.Anonymous.mi.dx = Normalize(point.X - screen.X, screen.Width);
        input.Anonymous.mi.dy = Normalize(point.Y - screen.Y, screen.Height);
        return input;
    }

    internal static int Normalize(int offset, int extent) => (int)(((long)offset * 65536 + extent - 1) / extent);

    private static INPUT Mouse(MOUSE_EVENT_FLAGS flags, uint data = 0) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new() { mi = new() { dwFlags = flags, mouseData = data } },
    };

    private static INPUT Keyboard(VIRTUAL_KEY key, ushort scan, KEYBD_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new() { ki = new() { wVk = key, wScan = scan, dwFlags = flags } },
    };

    private static unsafe InputReceipt Send(ReadOnlySpan<INPUT> batch)
    {
        var start = Stopwatch.GetTimestamp();
        var sent = batch.IsEmpty ? 0 : (int)PInvoke.SendInput(batch, sizeof(INPUT));
        return new InputReceipt(sent, batch.Length, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
