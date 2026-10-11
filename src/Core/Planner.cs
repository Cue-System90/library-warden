namespace LibraryWarden.Core;

public enum ItemState { Done, Missing, Outdated, Partial }

public sealed class GameEval
{
    public int WantedCount { get; set; }
    public int MissingCount { get; set; }
    public int OutdatedCount { get; set; }
    public long WantedBytes { get; set; }
    public long MissingBytes { get; set; }
}

/// <summary>Auswahl der gewünschten Dateien, Abgleich mit dem lokalen Bestand und Erkennen von Änderungen.</summary>
public static class Planner
{
    public static string LocalKey(RemoteItem item, RemoteFile file) => $"{item.Key}|{file.Id}";

    // ---------- Auswahl ----------

    public static List<RemoteItem> SelectWanted(GameSnapshot game, Settings s)
    {
        var result = new List<RemoteItem>();
        var languages = s.Languages.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).ToList();

        foreach (var product in game.Items.GroupBy(i => i.ProductId))
        {
            var items = product.ToList();
            if (items[0].IsDlc && !s.IncludeDlc) continue;

            var chosenLanguages = new Dictionary<string, HashSet<string>>();   // OS → gewählte Installer-Sprachen
            var installerVersions = new Dictionary<string, string?>();          // OS|Sprache → Version
            foreach (var os in s.Platforms)
            {
                var installers = items.Where(i => i.Kind == ItemKind.Installer && Eq(i.Os, os)).ToList();
                if (installers.Count == 0) continue;
                var langs = PickLanguages(installers, languages, s.AllSelectedLanguages);
                chosenLanguages[os] = langs;
                foreach (var inst in installers.Where(i => langs.Contains(Lang(i))))
                {
                    if (s.IncludeInstallers) result.Add(inst);
                    installerVersions[$"{os}|{Lang(inst)}"] = inst.Version;
                }
            }

            if (s.Patches != PatchMode.None)
            {
                foreach (var patch in items.Where(i => i.Kind == ItemKind.Patch && i.Os != null))
                {
                    if (!chosenLanguages.TryGetValue(patch.Os!, out var langs) || !langs.Contains(Lang(patch))) continue;
                    if (s.Patches == PatchMode.All ||
                        (installerVersions.TryGetValue($"{patch.Os}|{Lang(patch)}", out var version) && version == patch.Version))
                        result.Add(patch);
                }
            }

            if (s.IncludeLanguagePacks)
                result.AddRange(items.Where(i => i.Kind == ItemKind.LanguagePack
                                                 && s.Platforms.Any(p => Eq(p, i.Os))
                                                 && languages.Contains(Lang(i))));

            if (s.IncludeExtras)
                result.AddRange(items.Where(i => i.Kind == ItemKind.Extra));
        }
        return result;
    }

    /// <summary>Sprachwahl je System: erste verfügbare Sprache aus der Prioritätsliste (oder alle gewählten),
    /// sonst Englisch, sonst die erste angebotene.</summary>
    static HashSet<string> PickLanguages(List<RemoteItem> installers, List<string> priority, bool all)
    {
        var available = installers.Select(Lang).ToHashSet();
        if (all)
        {
            var matches = priority.Where(available.Contains).ToHashSet();
            if (matches.Count > 0) return matches;
        }
        else
        {
            foreach (var lang in priority)
                if (available.Contains(lang)) return [lang];
        }
        return available.Contains("en") ? ["en"] : [Lang(installers[0])];
    }

    static string Lang(RemoteItem i) => (i.Language ?? "").ToLowerInvariant();

    static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------- Lokaler Bestand ----------

    public static bool FileDone(RemoteItem item, RemoteFile file, AppState state, string root) =>
        state.LocalFiles.TryGetValue(LocalKey(item, file), out var local)
        && local.Fingerprint == item.Fingerprint
        && LocalExists(local, root);

    public static bool LocalExists(LocalFile local, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var info = new FileInfo(Path.Combine(root, local.RelativePath));
            return info.Exists && info.Length == local.Length;
        }
        catch
        {
            return false;
        }
    }

    public static ItemState StateOf(RemoteItem item, AppState state, string root)
    {
        int done = 0;
        bool outdated = false;
        foreach (var file in item.Files)
        {
            if (!state.LocalFiles.TryGetValue(LocalKey(item, file), out var local) || !LocalExists(local, root)) continue;
            if (local.Fingerprint == item.Fingerprint) done++;
            else outdated = true;
        }
        if (done == item.Files.Count) return ItemState.Done;
        if (outdated) return ItemState.Outdated;
        return done > 0 ? ItemState.Partial : ItemState.Missing;
    }

    public static GameEval Evaluate(GameSnapshot game, Settings s, AppState state)
    {
        var eval = new GameEval();
        foreach (var item in SelectWanted(game, s))
        {
            eval.WantedCount++;
            eval.WantedBytes += item.TotalSize;
            var itemState = StateOf(item, state, s.LibraryRoot);
            if (itemState == ItemState.Done) continue;
            eval.MissingCount++;
            eval.MissingBytes += item.TotalSize;
            if (itemState == ItemState.Outdated) eval.OutdatedCount++;
        }
        return eval;
    }

    /// <summary>Zielordner relativ zum Bibliotheksordner, z. B. "Stardew Valley\Windows" oder "Spiel\DLC\Name\Extras".</summary>
    public static string RelativeDir(GameSnapshot game, RemoteItem item)
    {
        var parts = new List<string> { PathUtil.SafeName(game.Title, game.Slug.Length > 0 ? game.Slug : game.Id.ToString()) };
        if (item.IsDlc)
        {
            parts.Add("DLC");
            parts.Add(PathUtil.SafeName(item.ProductTitle, item.ProductId.ToString()));
        }
        switch (item.Kind)
        {
            case ItemKind.Installer:
                parts.Add(Fmt.Os(item.Os));
                break;
            case ItemKind.Patch:
                parts.Add(Fmt.Os(item.Os));
                parts.Add("Patches");
                break;
            case ItemKind.LanguagePack:
                parts.Add(Fmt.Os(item.Os));
                parts.Add("Sprachpakete");
                break;
            default:
                parts.Add("Extras");
                break;
        }
        return Path.Combine(parts.ToArray());
    }

    // ---------- Änderungserkennung ----------

    /// <summary>Vergleicht alten und neuen Stand eines Spiels. Gemeldet wird nur, was zur eigenen Auswahl
    /// (System, Sprache, Inhaltstypen) passt – plus neu hinzugekommene DLCs.</summary>
    public static List<ChangeEvent> Diff(GameSnapshot game, List<RemoteItem> oldItems, List<RemoteItem> newItems, Settings s)
    {
        var events = new List<ChangeEvent>();
        var wantedOld = SelectWanted(new GameSnapshot { Items = oldItems }, s).Select(i => i.Key).ToHashSet();
        var wantedNew = SelectWanted(new GameSnapshot { Items = newItems }, s).Select(i => i.Key).ToHashSet();
        var oldByKey = ByKey(oldItems);
        var newByKey = ByKey(newItems);
        var oldProducts = oldItems.Select(i => i.ProductId).ToHashSet();

        var newDlcs = newItems.Where(i => i.IsDlc && !oldProducts.Contains(i.ProductId))
            .GroupBy(i => i.ProductId).ToList();
        foreach (var dlc in newDlcs)
            events.Add(Make(game, ChangeKind.Added, $"Neuer DLC: {dlc.First().ProductTitle}"));
        var newDlcIds = newDlcs.Select(g => g.Key).ToHashSet();

        foreach (var (key, item) in newByKey)
        {
            if (newDlcIds.Contains(item.ProductId) || !wantedNew.Contains(key)) continue;
            if (!oldByKey.TryGetValue(key, out var previous))
                events.Add(Make(game, ChangeKind.Added, DescribeNew(item)));
            else if (previous.Fingerprint != item.Fingerprint)
                events.Add(Make(game, ChangeKind.Updated, DescribeUpdate(previous, item)));
        }

        foreach (var (key, previous) in oldByKey)
            if (!newByKey.ContainsKey(key) && wantedOld.Contains(key))
                events.Add(Make(game, ChangeKind.Removed, $"Von GOG entfernt: {Describe(previous)}"));

        return events;
    }

    static Dictionary<string, RemoteItem> ByKey(List<RemoteItem> items)
    {
        var map = new Dictionary<string, RemoteItem>();
        foreach (var item in items) map.TryAdd(item.Key, item);
        return map;
    }

    public static ChangeEvent Make(GameSnapshot game, ChangeKind kind, string text) =>
        new() { GameId = game.Id, GameTitle = game.Title, Kind = kind, Text = text };

    static string Prefix(RemoteItem i) => i.IsDlc ? $"[{i.ProductTitle}] " : "";

    public static string Describe(RemoteItem i) => i.Kind switch
    {
        ItemKind.Installer => $"{Prefix(i)}Installer {Fmt.Os(i.Os)}, {Fmt.Language(i)}{(i.Version != null ? $" – Version {i.Version}" : "")}",
        ItemKind.Patch => $"{Prefix(i)}{i.Name} ({Fmt.Os(i.Os)}, {Fmt.Language(i)})",
        ItemKind.LanguagePack => $"{Prefix(i)}Sprachpaket {Fmt.Language(i)} ({Fmt.Os(i.Os)})",
        _ => $"{Prefix(i)}Extra „{i.Name}“{(i.TotalSize > 0 ? $" ({Fmt.Bytes(i.TotalSize)})" : "")}",
    };

    static string DescribeNew(RemoteItem i) => i.Kind switch
    {
        ItemKind.Installer => $"Neuer {Describe(i)}",
        ItemKind.Patch => $"Neuer Patch: {Describe(i)}",
        ItemKind.LanguagePack => $"Neues {Describe(i)}",
        _ => $"Neues {Describe(i)}",
    };

    static string DescribeUpdate(RemoteItem old, RemoteItem now)
    {
        var sizes = $"{Fmt.Bytes(old.TotalSize)} → {Fmt.Bytes(now.TotalSize)}";
        var versions = old.Version != now.Version ? $"{old.Version ?? "?"} → {now.Version ?? "?"}" : $"Dateien geändert ({sizes})";
        return now.Kind switch
        {
            ItemKind.Installer => $"{Prefix(now)}Installer-Update {Fmt.Os(now.Os)}, {Fmt.Language(now)}: {versions}",
            ItemKind.LanguagePack => $"{Prefix(now)}Sprachpaket-Update {Fmt.Language(now)} ({Fmt.Os(now.Os)}): {versions}",
            ItemKind.Patch => $"{Prefix(now)}Patch geändert: {now.Name} ({sizes})",
            _ => $"{Prefix(now)}Extra aktualisiert: „{now.Name}“ ({sizes})",
        };
    }
}
