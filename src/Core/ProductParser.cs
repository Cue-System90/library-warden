using System.Text.Json;

namespace LibraryWarden.Core;

/// <summary>Wandelt die Antwort von api.gog.com/products/{id}?expand=downloads,expanded_dlcs in Download-Einträge um.</summary>
public static class ProductParser
{
    public static List<RemoteItem> Parse(JsonElement root, ISet<long>? owned)
    {
        var items = new List<RemoteItem>();
        var keys = new HashSet<string>();
        AddProduct(items, keys, root, J.Long(root, "id"), J.Str(root, "title") ?? "", isDlc: false);

        foreach (var dlc in J.Arr(root, "expanded_dlcs"))
        {
            var dlcId = J.Long(dlc, "id");
            // Das Produkt listet alle DLCs – herunterladen darf man nur die eigenen.
            if (owned != null && !owned.Contains(dlcId)) continue;
            AddProduct(items, keys, dlc, dlcId, J.Str(dlc, "title") ?? $"DLC {dlcId}", isDlc: true);
        }
        return items;
    }

    static void AddProduct(List<RemoteItem> items, HashSet<string> keys, JsonElement product, long productId, string title, bool isDlc)
    {
        if (!product.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object) return;
        AddList(items, keys, downloads, "installers", ItemKind.Installer, productId, title, isDlc);
        AddList(items, keys, downloads, "patches", ItemKind.Patch, productId, title, isDlc);
        AddList(items, keys, downloads, "language_packs", ItemKind.LanguagePack, productId, title, isDlc);
        AddList(items, keys, downloads, "bonus_content", ItemKind.Extra, productId, title, isDlc);
    }

    static void AddList(List<RemoteItem> items, HashSet<string> keys, JsonElement downloads, string property,
        ItemKind kind, long productId, string title, bool isDlc)
    {
        foreach (var e in J.Arr(downloads, property))
        {
            var item = new RemoteItem
            {
                ProductId = productId,
                ProductTitle = title,
                IsDlc = isDlc,
                Kind = kind,
                Name = J.Str(e, "name") ?? "",
                Os = kind == ItemKind.Extra ? null : J.Str(e, "os"),
                Language = kind == ItemKind.Extra ? null : J.Str(e, "language"),
                LanguageFull = kind == ItemKind.Extra ? null : J.Str(e, "language_full"),
                Version = J.Str(e, "version"),
                ExtraType = kind == ItemKind.Extra ? J.Str(e, "type") : null,
                TotalSize = J.Long(e, "total_size"),
            };
            foreach (var f in J.Arr(e, "files"))
            {
                var downlink = J.Str(f, "downlink");
                if (string.IsNullOrEmpty(downlink)) continue;
                item.Files.Add(new RemoteFile { Id = J.Str(f, "id") ?? "", Size = J.Long(f, "size"), Downlink = downlink });
            }
            if (item.Files.Count == 0) continue;
            if (item.TotalSize <= 0) item.TotalSize = item.Files.Sum(f => f.Size);

            // Patch-IDs sind nur durchnummeriert (en1patch0 …) – der Name ("Patch (1.0 to 1.1)") ist stabiler.
            var key = kind == ItemKind.Patch
                ? $"{productId}:patch:{item.Os}:{item.Language}:{item.Name}"
                : $"{productId}:{Tag(kind)}:{J.Str(e, "id")}";
            var unique = key;
            for (int i = 2; !keys.Add(unique); i++) unique = $"{key}#{i}";
            item.Key = unique;
            items.Add(item);
        }
    }

    static string Tag(ItemKind kind) => kind switch
    {
        ItemKind.Installer => "installer",
        ItemKind.Patch => "patch",
        ItemKind.LanguagePack => "langpack",
        _ => "extra",
    };
}
