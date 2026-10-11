using System.Text.Json.Serialization;

namespace LibraryWarden.Core;

public enum ItemKind { Installer, Patch, LanguagePack, Extra }

/// <summary>Eine einzelne herunterladbare Datei (Installer können aus mehreren Teilen bestehen).</summary>
public sealed class RemoteFile
{
    public string Id { get; set; } = "";
    public long Size { get; set; }
    public string Downlink { get; set; } = "";
}

/// <summary>Ein Download-Eintrag eines Spiels oder DLCs, so wie GOG ihn anbietet.</summary>
public sealed class RemoteItem
{
    public string Key { get; set; } = "";
    public long ProductId { get; set; }
    public string ProductTitle { get; set; } = "";
    public bool IsDlc { get; set; }
    public ItemKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Os { get; set; }
    public string? Language { get; set; }
    public string? LanguageFull { get; set; }
    public string? Version { get; set; }
    public string? ExtraType { get; set; }
    public long TotalSize { get; set; }
    public List<RemoteFile> Files { get; set; } = [];

    /// <summary>Ändert sich, sobald GOG den Inhalt austauscht (neue Version, andere Größe, andere Teilezahl).</summary>
    [JsonIgnore]
    public string Fingerprint => $"{Version}|{TotalSize}|{Files.Count}";
}

/// <summary>Letzter bekannter Stand eines Spiels aus der Bibliothek.</summary>
public sealed class GameSnapshot
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public bool IsHidden { get; set; }
    public bool Removed { get; set; }
    public int GogUpdates { get; set; }
    public bool GogIsNew { get; set; }
    public int DlcCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset? LastDetails { get; set; }
    public string? DetailsError { get; set; }
    public List<RemoteItem> Items { get; set; } = [];
}

/// <summary>Eine lokal vorhandene, von LibraryWarden geladene Datei.</summary>
public sealed class LocalFile
{
    public long GameId { get; set; }
    public string ItemKey { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? Version { get; set; }
    public string RelativePath { get; set; } = "";
    public long Length { get; set; }
    public string? Md5 { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }
}

public enum ChangeKind { Info, NewGame, Added, Updated, Removed, GameRemoved, GogFlag }

public sealed class ChangeEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public long GameId { get; set; }
    public string GameTitle { get; set; } = "";
    public ChangeKind Kind { get; set; }
    public string Text { get; set; } = "";
    public bool Read { get; set; }
}

public sealed record QueuedFile(long GameId, string ItemKey, string FileId);

/// <summary>Fertig geladene und geprüfte Datei im Zwischenspeicher, die noch aufs Ziellaufwerk übertragen werden muss.</summary>
public sealed class PendingMove
{
    public long GameId { get; set; }
    public string GameTitle { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public string FileId { get; set; } = "";
    public string StagedPath { get; set; } = "";
    public string TargetRelativePath { get; set; } = "";
    public long Length { get; set; }
    public string? Md5 { get; set; }
    public bool Verified { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? Version { get; set; }

    [JsonIgnore]
    public string Key => $"{ItemKey}|{FileId}";
}

/// <summary>Gesamter persistenter Zustand (state.json).</summary>
public sealed class AppState
{
    public int Version { get; set; } = 1;
    public bool BaselineDone { get; set; }
    public DateTimeOffset? LastQuickCheck { get; set; }
    public DateTimeOffset? LastFullCheck { get; set; }
    public string? Username { get; set; }
    public bool TrayHintShown { get; set; }
    public bool DownloadsPaused { get; set; }
    public HashSet<long> Owned { get; set; } = [];
    public Dictionary<long, GameSnapshot> Games { get; set; } = new();
    public Dictionary<string, LocalFile> LocalFiles { get; set; } = new();
    public List<ChangeEvent> Events { get; set; } = [];
    public List<QueuedFile> Queue { get; set; } = [];
    public List<PendingMove> PendingMoves { get; set; } = [];
}
