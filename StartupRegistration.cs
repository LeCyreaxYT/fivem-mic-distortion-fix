using Microsoft.Win32;

namespace ApolloMicBridge;

internal static class StartupRegistration
{
    internal const string ValueName = "SkySystemsApolloMicBridge";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, CommandFor(Application.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
            key.SetValue(ValueName, CommandFor(Application.ExecutablePath), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
    }

    internal static string CommandFor(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("Startup executable must be an absolute Windows path without quotes.", nameof(executable));
        return '"' + executable + "\" --autostart --background";
    }
}
