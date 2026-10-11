using System.Text.Json.Serialization;

namespace LibraryWarden.Core;

public enum PatchMode { None, LatestOnly, All }
public enum OldVersionMode { Archive, Delete, Keep }
public enum GameRule { Default, AutoDownload, NotifyOnly, Ignore }
public enum DefaultAction { NotifyOnly, AutoDownload }
public enum ThemeChoice { System, Light, Dark }

/// <summary>Fensterposition in geräteunabhängigen Pixeln.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

public sealed class Settings
{
    public string LibraryRoot { get; set; } = "";

    // Auswahl
    public List<string> Platforms { get; set; } = ["windows"];
    public List<string> Languages { get; set; } = ["de", "en"];
    public bool AllSelectedLanguages { get; set; }
    public bool IncludeInstallers { get; set; } = true;
    public PatchMode Patches { get; set; } = PatchMode.None;
    public bool IncludeLanguagePacks { get; set; } = true;
    public bool IncludeExtras { get; set; } = true;
    public bool IncludeDlc { get; set; } = true;

    // Downloads
    public int ParallelDownloads { get; set; } = 2;
    public double BandwidthLimitMBps { get; set; }
    public OldVersionMode OldVersions { get; set; } = OldVersionMode.Archive;
    public bool VerifyChecksums { get; set; } = true;

    // Zwischenspeicher für langsame Ziellaufwerke (z. B. externe USB-Festplatte)
    public bool StagingEnabled { get; set; }
    /// <summary>Leer = Standardordner unter %LOCALAPPDATA%.</summary>
    public string StagingDir { get; set; } = "";
    public double StagingMaxGB { get; set; } = 20;

    [JsonIgnore]
    public string EffectiveStagingDir => string.IsNullOrWhiteSpace(StagingDir) ? AppPaths.DefaultStagingDir : StagingDir;

    [JsonIgnore]
    public long StagingLimitBytes => (long)(Math.Max(1, StagingMaxGB) * 1024 * 1024 * 1024);

    // Wächter
    public bool WatcherEnabled { get; set; } = true;
    public int QuickCheckHours { get; set; } = 3;
    public int FullCheckHours { get; set; } = 24;
    public DefaultAction OnNewContent { get; set; } = DefaultAction.NotifyOnly;
    public bool NotifyGogFlagOnly { get; set; } = true;
    public Dictionary<long, GameRule> GameRules { get; set; } = new();

    // Allgemein
    public bool Autostart { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    // Darstellung
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>0 = Windows-Textgröße übernehmen, sonst fester Faktor (1.0 = 100 %).</summary>
    public double UiScale { get; set; }
    public bool RibbonCollapsed { get; set; }
    public bool AlwaysOnTop { get; set; }
    public WindowPlacement? Window { get; set; }

    public GameRule RuleFor(long gameId) => GameRules.TryGetValue(gameId, out var rule) ? rule : GameRule.Default;

    public bool AutoDownloadFor(long gameId) => RuleFor(gameId) switch
    {
        GameRule.AutoDownload => true,
        GameRule.Default => OnNewContent == DefaultAction.AutoDownload,
        _ => false,
    };

    public Settings Clone()
    {
        var copy = (Settings)MemberwiseClone();
        copy.Platforms = [.. Platforms];
        copy.Languages = [.. Languages];
        copy.GameRules = new Dictionary<long, GameRule>(GameRules);
        return copy;
    }
}
