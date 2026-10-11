using System.Text.Json;
using System.Text.RegularExpressions;

namespace LibraryWarden.Core;

/// <summary>Tolerantes Lesen von JSON-Feldern (GOG liefert IDs mal als Zahl, mal als Text).</summary>
static class J
{
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed)) return parsed;
        return 0;
    }

    public static bool Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true");
    }

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray();
        return Array.Empty<JsonElement>();
    }
}

/// <summary>Anzeigeformate (deutsch).</summary>
public static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "–";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Time(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "–";

    public static string Os(string? os) => os switch
    {
        "windows" => "Windows",
        "mac" or "osx" => "macOS",
        "linux" => "Linux",
        null or "" => "Sonstiges",
        _ => os,
    };

    public static string Kind(ItemKind kind) => kind switch
    {
        ItemKind.Installer => "Installer",
        ItemKind.Patch => "Patch",
        ItemKind.LanguagePack => "Sprachpaket",
        _ => "Extra",
    };

    public static string Language(RemoteItem item) => item.LanguageFull ?? item.Language ?? "?";

    public static string Change(ChangeKind kind) => kind switch
    {
        ChangeKind.NewGame => "Neues Spiel",
        ChangeKind.Added => "Neu",
        ChangeKind.Updated => "Update",
        ChangeKind.Removed => "Entfernt",
        ChangeKind.GameRemoved => "Nicht mehr da",
        ChangeKind.GogFlag => "GOG-Hinweis",
        _ => "Info",
    };
}

public static class PathUtil
{
    static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars()];
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Macht aus einem Spieltitel einen gültigen, gut lesbaren Ordnernamen.</summary>
    public static string SafeName(string? name, string fallback)
    {
        var s = (name ?? "").Replace("™", "").Replace("®", "").Replace("©", "")
            .Replace(": ", " - ").Replace(':', '-').Replace('/', '-').Replace('\\', '-');
        s = new string(s.Where(c => c >= 32 && !Invalid.Contains(c)).ToArray());
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        if (s.Length == 0) s = fallback;
        if (Reserved.Contains(s)) s = "_" + s;
        return s;
    }

    public static string SafeFileName(string name)
    {
        var s = new string(name.Where(c => c >= 32 && !Invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "download.bin" : s;
    }

    public static string? FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return null;
        var decoded = Uri.UnescapeDataString(last);
        return decoded.Contains('.') ? decoded : null;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({DateTime.Now:yyyyMMdd-HHmmss}{(i > 2 ? "-" + i : "")}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
