using Microsoft.Win32;

namespace LibraryWarden.Core;

/// <summary>Autostart über HKCU\...\Run – startet LibraryWarden unsichtbar im Infobereich.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LibraryWarden";

    public static void Apply(bool enabled)
    {
        if (AppPaths.IsIsolated) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
