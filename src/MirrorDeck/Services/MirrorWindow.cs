using System.Runtime.InteropServices;

namespace MirrorDeck.Services;

/// <summary>
/// The picture is drawn by the engine in a window of its own (GStreamer's Direct3D sink). It is an
/// ordinary top-level window: resizable, movable between monitors, and Alt+Enter switches it to full
/// screen. This class lets the control panel find it, title it, raise it and toggle full screen.
/// </summary>
/// <remarks>
/// EXTENSION POINT: to show the picture inside the app instead, the engine would have to be given a window
/// handle (GstVideoOverlay). UxPlay has no option for that today; it would be a small upstream patch.
/// </remarks>
internal static class MirrorWindow
{
    /// <summary>The engine's visible top-level window, or zero while nothing is being shown.</summary>
    public static IntPtr Find(int engineProcessId)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows(
            (window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var owner);
                if (owner != (uint)engineProcessId || !Native.IsWindowVisible(window)) return true;
                if (!Native.GetWindowRect(window, out var rect)) return true;

                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = window;
                }

                return true;
            },
            IntPtr.Zero);
        return best;
    }

    public static void SetTitle(IntPtr window, string title)
    {
        if (window != IntPtr.Zero) Native.SetWindowText(window, title);
    }

    public static bool BringToFront(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        Native.ShowWindow(window, Native.IsIconic(window) ? Native.SwRestore : Native.SwShow);
        return Native.SetForegroundWindow(window);
    }

    /// <summary>
    /// Presses Alt+Enter for the user. The sink reads the real keyboard state, so a posted window message
    /// is not enough; the keystroke has to go through the input queue of the foreground window.
    /// </summary>
    public static async Task<bool> ToggleFullscreenAsync(IntPtr window)
    {
        if (!BringToFront(window)) return false;

        // give the window manager a moment to complete the activation
        for (var i = 0; i < 10 && Native.GetForegroundWindow() != window; i++) await Task.Delay(30);
        if (Native.GetForegroundWindow() != window) return false;

        Native.INPUT Key(ushort virtualKey, bool up) => new()
        {
            Type = Native.InputKeyboard,
            Data = { Keyboard = new Native.KEYBDINPUT { VirtualKey = virtualKey, Flags = up ? Native.KeyEventKeyUp : 0 } },
        };

        Native.INPUT[] keys =
        [
            Key(Native.VkMenu, up: false),
            Key(Native.VkReturn, up: false),
            Key(Native.VkReturn, up: true),
            Key(Native.VkMenu, up: true),
        ];
        return Native.SendInput((uint)keys.Length, keys, Marshal.SizeOf<Native.INPUT>()) == keys.Length;
    }
}
