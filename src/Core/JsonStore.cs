using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraryWarden.Core;

/// <summary>Lesen/Schreiben von JSON-Dateien; Schreiben atomar mit .bak-Kopie.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T LoadOrDefault<T>(string path) where T : new()
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Options) ?? new T();
            }
            catch (Exception ex)
            {
                Log.Error($"Datei {candidate} konnte nicht gelesen werden", ex);
            }
        }
        return new T();
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static void Save<T>(string path, T value) => WriteAtomic(path, Serialize(value));

    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try
        {
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
