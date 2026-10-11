using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LibraryWarden.Core;

public sealed class TokenData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? UserId { get; set; }
}

/// <summary>Speichert den GOG-Anmeldetoken mit Windows-DPAPI verschlüsselt (nur für dieses Windows-Konto lesbar).</summary>
public static class TokenStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LibraryWarden.Token.v1");

    public static TokenData? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.TokenFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.TokenFile), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TokenData>(plain);
        }
        catch (Exception ex)
        {
            Log.Error("Anmeldetoken konnte nicht gelesen werden", ex);
            return null;
        }
    }

    public static void Save(TokenData token)
    {
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(AppPaths.RoamingDir);
        var tmp = AppPaths.TokenFile + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, AppPaths.TokenFile, overwrite: true);
    }

    public static void Delete()
    {
        try { File.Delete(AppPaths.TokenFile); } catch { }
    }
}
