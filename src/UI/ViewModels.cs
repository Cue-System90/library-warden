using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using LibraryWarden.Core;

namespace LibraryWarden.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record Choice(object Value, string Display);

public enum LibraryFilter { All, New, Missing, Outdated, Complete, Ignored, Hidden, Problems }

public static class Choices
{
    public static IReadOnlyList<Choice> Rules { get; } =
    [
        new(GameRule.Default, "Standard"),
        new(GameRule.AutoDownload, "Automatisch laden"),
        new(GameRule.NotifyOnly, "Nur melden"),
        new(GameRule.Ignore, "Ignorieren"),
    ];

    public static IReadOnlyList<Choice> PatchModes { get; } =
    [
        new(PatchMode.None, "Keine Patches"),
        new(PatchMode.LatestOnly, "Nur Patch zur aktuellen Version"),
        new(PatchMode.All, "Alle Patches"),
    ];

    public static IReadOnlyList<Choice> OldVersionModes { get; } =
    [
        new(OldVersionMode.Archive, "In den Ordner _Archiv verschieben"),
        new(OldVersionMode.Delete, "Löschen"),
        new(OldVersionMode.Keep, "Liegen lassen"),
    ];

    public static IReadOnlyList<Choice> DefaultActions { get; } =
    [
        new(DefaultAction.NotifyOnly, "Nur melden"),
        new(DefaultAction.AutoDownload, "Melden und automatisch herunterladen"),
    ];

    public static IReadOnlyList<Choice> Filters { get; } =
    [
        new(LibraryFilter.All, "Alle Spiele"),
        new(LibraryFilter.New, "Mit neuen Inhalten"),
        new(LibraryFilter.Missing, "Fehlt / unvollständig"),
        new(LibraryFilter.Outdated, "Update verfügbar"),
        new(LibraryFilter.Complete, "Vollständig"),
        new(LibraryFilter.Ignored, "Ignoriert"),
        new(LibraryFilter.Hidden, "Bei GOG ausgeblendet"),
        new(LibraryFilter.Problems, "Probleme"),
    ];

    public static IReadOnlyList<Choice> Parallel { get; } = [new(1, "1"), new(2, "2"), new(3, "3"), new(4, "4"), new(6, "6")];

    public static IReadOnlyList<Choice> QuickHours { get; } =
    [
        new(1, "jede Stunde"), new(2, "alle 2 Stunden"), new(3, "alle 3 Stunden"),
        new(6, "alle 6 Stunden"), new(12, "alle 12 Stunden"), new(24, "einmal am Tag"),
    ];

    public static IReadOnlyList<Choice> FullHours { get; } =
    [
        new(12, "alle 12 Stunden"), new(24, "täglich"), new(48, "alle 2 Tage"), new(168, "wöchentlich"),
    ];
}

public enum RowState { Complete, Missing, Partial, Outdated, Downloading, Ignored, Nothing, Unknown, Removed }

/// <summary>Zeile der Bibliotheksliste.</summary>
public sealed class GameRow : Observable
{
    /// <summary>Wird aufgerufen, wenn der Nutzer die Regel in der Tabelle ändert.</summary>
    public static Action<long, GameRule>? RuleEdited { get; set; }

    public long Id { get; init; }

    string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    RowState _state;
    public RowState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateOrder)); } }
    public int StateOrder => (int)_state;

    string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    long _wantedBytes;
    public long WantedBytes { get => _wantedBytes; set { if (Set(ref _wantedBytes, value)) Raise(nameof(WantedText)); } }
    public string WantedText => Fmt.Bytes(_wantedBytes);

    long _missingBytes;
    public long MissingBytes { get => _missingBytes; set { if (Set(ref _missingBytes, value)) Raise(nameof(MissingText)); } }
    public string MissingText => _missingBytes > 0 ? Fmt.Bytes(_missingBytes) : "";

    int _unread;
    public int Unread { get => _unread; set { if (Set(ref _unread, value)) Raise(nameof(HasNew)); } }
    public bool HasNew => _unread > 0;

    DateTimeOffset? _lastChange;
    public DateTimeOffset? LastChange { get => _lastChange; set { if (Set(ref _lastChange, value)) Raise(nameof(LastChangeText)); } }
    public string LastChangeText => _lastChange is { } t ? Fmt.Time(t) : "";

    bool _isHidden;
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    GameRule _rule;
    public GameRule Rule
    {
        get => _rule;
        set
        {
            if (Set(ref _rule, value)) RuleEdited?.Invoke(Id, value);
        }
    }

    /// <summary>Regel aus dem gespeicherten Stand übernehmen, ohne <see cref="RuleEdited"/> auszulösen.</summary>
    public void LoadRule(GameRule rule)
    {
        if (_rule == rule) return;
        _rule = rule;
        Raise(nameof(Rule));
    }
}

/// <summary>Zeile der Detailansicht eines Spiels.</summary>
public sealed class ItemRow
{
    public string WantedMark { get; init; } = "";
    public string KindText { get; init; } = "";
    public string Name { get; init; } = "";
    public string Product { get; init; } = "";
    public string OsText { get; init; } = "";
    public string LanguageText { get; init; } = "";
    public string Version { get; init; } = "";
    public string SizeText { get; init; } = "";
    public long Size { get; init; }
    public string StateText { get; init; } = "";
    public RowState State { get; init; }
    public int SortOrder { get; init; }
}

/// <summary>Zeile des Änderungsprotokolls.</summary>
public sealed class EventRow
{
    public Guid Id { get; init; }
    public long GameId { get; init; }
    public DateTimeOffset Time { get; init; }
    public string TimeText => Fmt.Time(Time);
    public string GameTitle { get; init; } = "";
    public ChangeKind Kind { get; init; }
    public string KindText => Fmt.Change(Kind);
    public string Text { get; init; } = "";
    public bool Unread { get; init; }
}

/// <summary>Bearbeitbare Kopie der Einstellungen für die Einstellungsseite.</summary>
public sealed class SettingsForm : Observable
{
    string _libraryRoot = "";
    public string LibraryRoot { get => _libraryRoot; set => Set(ref _libraryRoot, value); }

    public bool Windows { get; set; }
    public bool Mac { get; set; }
    public bool Linux { get; set; }
    public string Languages { get; set; } = "";
    public bool AllSelectedLanguages { get; set; }
    public bool IncludeInstallers { get; set; }
    public PatchMode Patches { get; set; }
    public bool IncludeLanguagePacks { get; set; }
    public bool IncludeExtras { get; set; }
    public bool IncludeDlc { get; set; }
    public int ParallelDownloads { get; set; }
    public string BandwidthText { get; set; } = "";
    public OldVersionMode OldVersions { get; set; }
    public bool VerifyChecksums { get; set; }
    public bool StagingEnabled { get; set; }

    string _stagingDir = "";
    public string StagingDir { get => _stagingDir; set { if (Set(ref _stagingDir, value)) Raise(nameof(StagingSpaceHint)); } }

    public string StagingSizeText { get; set; } = "";

    /// <summary>Freier Platz auf dem Laufwerk des Zwischenspeichers (bzw. des Standardordners).</summary>
    public string StagingSpaceHint
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(_stagingDir) ? AppPaths.DefaultStagingDir : _stagingDir.Trim();
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return "";
                return $"Laufwerk {root.TrimEnd('\\')} hat {Fmt.Bytes(new DriveInfo(root).AvailableFreeSpace)} frei";
            }
            catch
            {
                return "";
            }
        }
    }

    public string StagingDefaultHint => $"Leer lassen für den Standardordner {AppPaths.DefaultStagingDir}";
    public bool WatcherEnabled { get; set; }
    public int QuickCheckHours { get; set; }
    public int FullCheckHours { get; set; }
    public DefaultAction OnNewContent { get; set; }
    public bool NotifyGogFlagOnly { get; set; }
    public bool Autostart { get; set; }
    public bool CloseToTray { get; set; }
    public string AvailableLanguages { get; set; } = "";

    public static SettingsForm From(Settings s, string availableLanguages) => new()
    {
        LibraryRoot = s.LibraryRoot,
        Windows = s.Platforms.Contains("windows"),
        Mac = s.Platforms.Contains("mac"),
        Linux = s.Platforms.Contains("linux"),
        Languages = string.Join(", ", s.Languages),
        AllSelectedLanguages = s.AllSelectedLanguages,
        IncludeInstallers = s.IncludeInstallers,
        Patches = s.Patches,
        IncludeLanguagePacks = s.IncludeLanguagePacks,
        IncludeExtras = s.IncludeExtras,
        IncludeDlc = s.IncludeDlc,
        ParallelDownloads = s.ParallelDownloads,
        BandwidthText = s.BandwidthLimitMBps > 0 ? s.BandwidthLimitMBps.ToString("0.##", CultureInfo.CurrentCulture) : "0",
        OldVersions = s.OldVersions,
        VerifyChecksums = s.VerifyChecksums,
        StagingEnabled = s.StagingEnabled,
        StagingDir = s.StagingDir,
        StagingSizeText = s.StagingMaxGB.ToString("0.##", CultureInfo.CurrentCulture),
        WatcherEnabled = s.WatcherEnabled,
        QuickCheckHours = s.QuickCheckHours,
        FullCheckHours = s.FullCheckHours,
        OnNewContent = s.OnNewContent,
        NotifyGogFlagOnly = s.NotifyGogFlagOnly,
        Autostart = s.Autostart,
        CloseToTray = s.CloseToTray,
        AvailableLanguages = availableLanguages,
    };

    public Settings? ToSettings(Settings current, out string? error)
    {
        error = null;
        var platforms = new List<string>();
        if (Windows) platforms.Add("windows");
        if (Mac) platforms.Add("mac");
        if (Linux) platforms.Add("linux");
        if (platforms.Count == 0 && IncludeInstallers)
        {
            error = "Bitte mindestens ein Betriebssystem wählen.";
            return null;
        }

        var languages = Languages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().ToLowerInvariant()).Distinct().ToList();
        if (languages.Count == 0) languages.Add("en");

        var text = BandwidthText.Trim().Replace(',', '.');
        if (!double.TryParse(text.Length == 0 ? "0" : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bandwidth) || bandwidth < 0)
        {
            error = "Die Bandbreite muss eine Zahl sein (MB/s, 0 = unbegrenzt).";
            return null;
        }

        var sizeText = StagingSizeText.Trim().Replace(',', '.');
        if (!double.TryParse(sizeText.Length == 0 ? "20" : sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var stagingGb) || stagingGb < 1)
        {
            error = "Die Größe des Zwischenspeichers muss eine Zahl ab 1 sein (in GB).";
            return null;
        }
        var stagingDir = StagingDir.Trim();
        if (stagingDir.Length > 0 && !Path.IsPathFullyQualified(stagingDir))
        {
            error = "Bitte für den Zwischenspeicher einen vollständigen Pfad angeben, z. B. C:\\Zwischenspeicher – oder das Feld leer lassen.";
            return null;
        }
        var library = LibraryRoot.Trim();
        if (StagingEnabled && library.Length > 0 && Path.IsPathFullyQualified(library))
        {
            var cache = Path.GetFullPath(stagingDir.Length > 0 ? stagingDir : AppPaths.DefaultStagingDir).TrimEnd('\\') + "\\";
            var lib = Path.GetFullPath(library).TrimEnd('\\') + "\\";
            if (cache.StartsWith(lib, StringComparison.OrdinalIgnoreCase) || lib.StartsWith(cache, StringComparison.OrdinalIgnoreCase))
            {
                error = "Zwischenspeicher und Bibliothek dürfen nicht ineinander liegen.";
                return null;
            }
        }

        var s = current.Clone();
        s.LibraryRoot = LibraryRoot.Trim();
        s.StagingEnabled = StagingEnabled;
        s.StagingDir = stagingDir;
        s.StagingMaxGB = stagingGb;
        s.Platforms = platforms;
        s.Languages = languages;
        s.AllSelectedLanguages = AllSelectedLanguages;
        s.IncludeInstallers = IncludeInstallers;
        s.Patches = Patches;
        s.IncludeLanguagePacks = IncludeLanguagePacks;
        s.IncludeExtras = IncludeExtras;
        s.IncludeDlc = IncludeDlc;
        s.ParallelDownloads = Math.Clamp(ParallelDownloads, 1, 8);
        s.BandwidthLimitMBps = bandwidth;
        s.OldVersions = OldVersions;
        s.VerifyChecksums = VerifyChecksums;
        s.WatcherEnabled = WatcherEnabled;
        s.QuickCheckHours = Math.Max(1, QuickCheckHours);
        s.FullCheckHours = Math.Max(s.QuickCheckHours, FullCheckHours);
        s.OnNewContent = OnNewContent;
        s.NotifyGogFlagOnly = NotifyGogFlagOnly;
        s.Autostart = Autostart;
        s.CloseToTray = CloseToTray;
        return s;
    }
}
