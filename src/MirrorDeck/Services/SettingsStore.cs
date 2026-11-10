using System.IO;
using System.Security.Cryptography;
using System.Text;
using MirrorDeck.Core;

namespace MirrorDeck.Services;

/// <summary>Loads and saves <see cref="AppSettings"/>. The password is encrypted with DPAPI for the current user.</summary>
internal static class SettingsStore
{
    // Not a secret: it only ties the encrypted blob to this purpose.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MirrorDeck.AirPlayPassword.v1");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? AppSettings.FromJson(File.ReadAllText(AppPaths.SettingsFile))
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        settings.Password = Unprotect(settings.ProtectedPassword);

        // A settings file edited by hand (or written by a future version) must not stop the app from starting.
        if (AppSettings.ValidateName(settings.ReceiverName) is not null)
        {
            var cleaned = AppSettings.Sanitize(settings.ReceiverName);
            settings.ReceiverName = AppSettings.ValidateName(cleaned) is null ? cleaned : AppSettings.DefaultReceiverName();
        }

        if (settings.MaxFps is < 10 or > 120) settings.MaxFps = 60;
        if (settings.AccessMode == AccessMode.Password && AppSettings.ValidatePassword(settings.Password) is not null)
        {
            // The password could not be decrypted (profile copied to another PC or user). Never fall back
            // to "open": ask for a code instead until a new password is set.
            settings.AccessMode = AccessMode.PinEveryTime;
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        settings.ProtectedPassword = Protect(settings.Password);
        Directory.CreateDirectory(AppPaths.DataDirectory);

        // write-then-rename: a crash or power cut can never leave half a settings file behind
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, settings.ToJson(), new UTF8Encoding(false));
        File.Move(temp, AppPaths.SettingsFile, overwrite: true);
    }

    private static string? Protect(string? clearText)
    {
        if (string.IsNullOrEmpty(clearText)) return null;
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(clearText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
