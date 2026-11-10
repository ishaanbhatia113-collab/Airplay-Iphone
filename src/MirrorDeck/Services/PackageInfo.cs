namespace MirrorDeck.Services;

internal static class PackageInfo
{
    /// <summary>
    /// True when the app runs with package identity (installed from an MSIX / the Microsoft Store).
    /// In that case the firewall rules come from the package manifest and are removed with the app.
    /// </summary>
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            var length = 0;
            return Native.GetCurrentPackageFullName(ref length, null) != Native.AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
