using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MirrorDeck.Services;

/// <summary>
/// The two Windows-specific things the engine process needs from us: it must die with the app, and it
/// should be asked to stop politely so that it can withdraw its network advertisement.
/// </summary>
internal static class EngineProcessSupport
{
    private static readonly Lock Gate = new();
    private static IntPtr _job;

    // Kept in a static field so the garbage collector cannot free a delegate that native code still calls.
    private static readonly Native.ConsoleCtrlHandler SwallowCtrlC =
        type => type is Native.CtrlCEvent or Native.CtrlBreakEvent;

    private static bool _handlerInstalled;

    /// <summary>
    /// Puts the engine into a job object that Windows closes when our process ends, however it ends
    /// (normal exit, crash, "End task"). Without it a crash of the app would leave an invisible uxplay.exe
    /// behind that keeps the AirPlay ports busy.
    /// </summary>
    public static void TieLifetimeToApp(Process engine)
    {
        lock (Gate)
        {
            if (_job == IntPtr.Zero)
            {
                var job = Native.CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
                var size = (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
                }

                _job = job; // intentionally never closed: the handle goes away with the process, which is the point
            }

            if (!Native.AssignProcessToJobObject(_job, engine.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "the engine could not be tied to the app's lifetime");
            }
        }
    }

    /// <summary>
    /// Sends the engine the equivalent of Ctrl+C. UxPlay handles it by leaving its main loop, which sends
    /// mDNS "goodbye" records; the PC then disappears from AirPlay lists at once instead of lingering
    /// until the records expire.
    /// </summary>
    /// <returns>false when the request could not be delivered; the caller then terminates the process.</returns>
    public static bool RequestStop(Process engine)
    {
        lock (Gate)
        {
            // Console control events go to every process attached to a console, and for a moment that
            // includes us. A handler (unlike the "ignore Ctrl+C" flag) is not inherited by child
            // processes, so it stays installed for good and the next engine still reacts to Ctrl+C.
            if (!_handlerInstalled)
            {
                if (!Native.SetConsoleCtrlHandler(SwallowCtrlC, true)) return false;
                _handlerInstalled = true;
            }

            // The engine was started without a window but still owns a (hidden) console. A GUI process has
            // none of its own, so it can borrow that one for the duration of the call.
            if (!Native.AttachConsole((uint)engine.Id)) return false;
            try
            {
                return Native.GenerateConsoleCtrlEvent(Native.CtrlCEvent, 0);
            }
            finally
            {
                Native.FreeConsole();
            }
        }
    }
}
