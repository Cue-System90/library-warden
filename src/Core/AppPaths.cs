namespace LibraryWarden.Core;

/// <summary>Speicherorte für Einstellungen, Zustand, Token und Logs.
/// Mit der Umgebungsvariable LIBRARYWARDEN_HOME lässt sich alles in einen eigenen Ordner umleiten (Tests).</summary>
public static class AppPaths
{
    static readonly string? Override = Environment.GetEnvironmentVariable("LIBRARYWARDEN_HOME");

    public static string RoamingDir { get; } = Override
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LibraryWarden");

    public static string LocalDir { get; } = Override is { } o
        ? Path.Combine(o, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibraryWarden");

    public static bool IsIsolated => Override != null;
    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string StateFile => Path.Combine(RoamingDir, "state.json");
    public static string TokenFile => Path.Combine(RoamingDir, "token.bin");
    public static string WebViewDir => Path.Combine(LocalDir, "WebView2");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string DefaultStagingDir => Path.Combine(LocalDir, "Zwischenspeicher");

    public static void Ensure()
    {
        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);
    }
}
