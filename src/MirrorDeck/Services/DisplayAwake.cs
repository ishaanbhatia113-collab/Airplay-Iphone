namespace MirrorDeck.Services;

/// <summary>
/// Keeps the screen on while something is being mirrored: nobody touches the PC's mouse or keyboard
/// during a presentation or a film, so Windows would otherwise blank the display.
/// </summary>
/// <remarks>The execution state belongs to the calling thread; always call from the UI thread.</remarks>
internal static class DisplayAwake
{
    private static bool _held;

    public static void Set(bool keepAwake)
    {
        if (keepAwake == _held) return;
        _held = keepAwake;
        Native.SetThreadExecutionState(keepAwake
            ? Native.EsContinuous | Native.EsDisplayRequired | Native.EsSystemRequired
            : Native.EsContinuous);
    }
}
