namespace LibraryWarden.Core;

/// <summary>Einfaches Tages-Logfile. Tokens und Passwörter werden nie geloggt.</summary>
public static class Log
{
    static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    public static void PruneOldFiles(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir)) return;
            foreach (var file in Directory.GetFiles(AppPaths.LogDir, "librarywarden-*.log"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays)) File.Delete(file);
        }
        catch { }
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(Path.Combine(AppPaths.LogDir, $"librarywarden-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
