using System.Collections.Concurrent;
using System.Text.Json;

namespace LibraryWarden.Core;

/// <summary>Zentrale Logik: Zustand, Wächter (periodische Prüfung), Änderungsprotokoll und Download-Planung.</summary>
public sealed class VaultService
{
    readonly object _saveLock = new();
    readonly Timer _saveTimer;
    Timer? _watchTimer;
    int _checking, _savePending;

    public VaultService()
    {
        Settings = JsonStore.LoadOrDefault<Settings>(AppPaths.SettingsFile);
        State = JsonStore.LoadOrDefault<AppState>(AppPaths.StateFile);
        Api = new GogApi();
        Downloads = new DownloadManager(this);
        _saveTimer = new Timer(_ => SaveStateNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Schützt <see cref="State"/>; nie gleichzeitig mit dem Listen-Lock der Downloads halten.</summary>
    public object Sync { get; } = new();
    public Settings Settings { get; private set; }
    public AppState State { get; }
    public GogApi Api { get; }
    public DownloadManager Downloads { get; }
    public string Status { get; private set; } = "Bereit.";
    public bool IsChecking => Volatile.Read(ref _checking) == 1;

    public event Action? LibraryChanged;
    public event Action<string>? StatusChanged;
    public event Action? CheckStateChanged;
    /// <summary>Neue Inhalte gefunden: Ereignisse + IDs der Spiele, deren Download automatisch gestartet wurde.</summary>
    public event Action<IReadOnlyList<ChangeEvent>, IReadOnlyList<long>>? NewContentFound;

    public void Start()
    {
        Downloads.RestoreQueue();
        _watchTimer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
    }

    public void Shutdown()
    {
        _watchTimer?.Dispose();
        Downloads.SuspendForExit();
        SaveStateNow();
        SaveSettings();
    }

    public DateTimeOffset? NextCheck
    {
        get
        {
            if (!Settings.WatcherEnabled || !Api.IsLoggedIn) return null;
            DateTimeOffset? quick, full;
            lock (Sync)
            {
                quick = State.LastQuickCheck?.AddHours(Settings.QuickCheckHours);
                full = State.LastFullCheck?.AddHours(Settings.FullCheckHours);
            }
            if (quick == null || full == null) return DateTimeOffset.Now;
            return quick < full ? quick : full;
        }
    }

    public void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    // ---------- Wächter ----------

    async Task TickAsync()
    {
        if (!Settings.WatcherEnabled || !Api.IsLoggedIn || IsChecking) return;
        var now = DateTimeOffset.Now;
        DateTimeOffset? lastQuick, lastFull;
        lock (Sync)
        {
            lastQuick = State.LastQuickCheck;
            lastFull = State.LastFullCheck;
        }
        bool fullDue = lastFull == null || now - lastFull >= TimeSpan.FromHours(Settings.FullCheckHours);
        bool quickDue = fullDue || lastQuick == null || now - lastQuick >= TimeSpan.FromHours(Settings.QuickCheckHours);
        if (quickDue) await RunCheckAsync(fullDue);
    }

    /// <summary>Schnellprüfung: Bibliotheksliste + Details nur für neue/markierte Spiele. Vollprüfung: Details aller Spiele.</summary>
    public async Task<bool> RunCheckAsync(bool full, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return false;
        CheckStateChanged?.Invoke();
        try
        {
            await CheckCoreAsync(full, ct);
            return true;
        }
        catch (NotLoggedInException)
        {
            SetStatus("Nicht angemeldet – bitte bei GOG anmelden.");
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Prüfung abgebrochen.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Prüfung fehlgeschlagen", ex);
            SetStatus($"Prüfung fehlgeschlagen: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
            CheckStateChanged?.Invoke();
        }
    }

    async Task CheckCoreAsync(bool full, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        Log.Info(full ? "Vollprüfung gestartet." : "Schnellprüfung gestartet.");
        SetStatus(full ? "Vollprüfung läuft …" : "Prüfe Bibliothek …");

        bool needUser;
        lock (Sync) needUser = State.Username == null;
        if (needUser)
        {
            try
            {
                var name = await Api.GetUsernameAsync(ct);
                lock (Sync) State.Username = name;
            }
            catch (GogApiException ex)
            {
                Log.Warn($"Benutzername nicht abrufbar: {ex.Message}");
            }
        }

        var library = await Api.GetLibraryAsync(SetStatus, ct);
        var owned = await Api.GetOwnedIdsAsync(ct);

        bool baseline;
        Dictionary<long, GameSnapshot> known;
        HashSet<long> previouslyOwned;
        lock (Sync)
        {
            baseline = !State.BaselineDone;
            known = new Dictionary<long, GameSnapshot>(State.Games);
            previouslyOwned = [.. State.Owned];
        }

        // Neu gekaufte DLCs tauchen nur in der Besitzliste auf → Spiele mit DLCs neu einlesen.
        var libraryIds = library.Select(e => e.Id).ToHashSet();
        bool newDlcOwned = !baseline && owned.Except(previouslyOwned).Any(id => !libraryIds.Contains(id));

        var toFetch = library.Where(e =>
        {
            if (full || !known.TryGetValue(e.Id, out var s)) return true;
            return s.LastDetails == null || s.DetailsError != null || s.Removed
                   || e.Updates > s.GogUpdates || (e.IsNew && !s.GogIsNew) || e.DlcCount != s.DlcCount
                   || (newDlcOwned && e.DlcCount > 0);
        }).ToList();

        var fetched = new ConcurrentDictionary<long, (List<RemoteItem>? Items, string? Error)>();
        int done = 0;
        await Parallel.ForEachAsync(toFetch, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (entry, token) =>
        {
            try
            {
                fetched[entry.Id] = (await Api.GetProductItemsAsync(entry.Id, owned, token), null);
            }
            catch (Exception ex) when (ex is GogApiException or JsonException)
            {
                Log.Warn($"Details für „{entry.Title}“ ({entry.Id}) nicht abrufbar: {ex.Message}");
                fetched[entry.Id] = (null, ex.Message);
            }
            var n = Interlocked.Increment(ref done);
            if (n % 5 == 0 || n == toFetch.Count) SetStatus($"Prüfe Spiele … {n}/{toFetch.Count}");
        });

        var events = new List<ChangeEvent>();
        lock (Sync)
        {
            foreach (var entry in library)
            {
                State.Games.TryGetValue(entry.Id, out var snap);
                bool isNewGame = snap == null || snap.Removed;
                bool wasRemoved = snap?.Removed == true;
                bool flagRaised = snap != null && entry.Updates > snap.GogUpdates;
                snap ??= new GameSnapshot { Id = entry.Id, FirstSeen = now };
                var oldItems = snap.Items;
                bool hadDetails = snap.LastDetails != null;
                bool ignored = Settings.RuleFor(entry.Id) == GameRule.Ignore;

                snap.Title = entry.Title;
                snap.Slug = entry.Slug;
                snap.IsHidden = entry.IsHidden;
                snap.DlcCount = entry.DlcCount;
                snap.GogUpdates = entry.Updates;
                snap.GogIsNew = entry.IsNew;
                snap.Removed = false;

                if (isNewGame && !baseline && !ignored)
                    events.Add(Planner.Make(snap, ChangeKind.NewGame, wasRemoved ? "Wieder in der Bibliothek" : "Neu in der Bibliothek"));

                if (fetched.TryGetValue(entry.Id, out var result))
                {
                    if (result.Items != null)
                    {
                        if (!baseline && hadDetails && !ignored)
                        {
                            var diff = Planner.Diff(snap, oldItems, result.Items, Settings);
                            events.AddRange(diff);
                            if (diff.Count == 0 && flagRaised && Settings.NotifyGogFlagOnly)
                                events.Add(Planner.Make(snap, ChangeKind.GogFlag,
                                    "GOG markiert ein Update, die Offline-Dateien sind aber unverändert (vermutlich nur ein Galaxy-Update)."));
                        }
                        snap.Items = result.Items;
                        snap.LastDetails = now;
                        snap.DetailsError = null;
                    }
                    else
                    {
                        snap.DetailsError = result.Error;
                    }
                }
                State.Games[entry.Id] = snap;
            }

            foreach (var game in State.Games.Values.Where(g => !g.Removed && !libraryIds.Contains(g.Id)))
            {
                game.Removed = true;
                if (!baseline) events.Add(Planner.Make(game, ChangeKind.GameRemoved, "Nicht mehr in der Bibliothek (Dateien bleiben erhalten)"));
            }

            State.Owned = owned;
            State.LastQuickCheck = now;
            if (full) State.LastFullCheck = now;
            if (baseline)
            {
                State.BaselineDone = true;
                events.Add(new ChangeEvent
                {
                    Kind = ChangeKind.Info,
                    GameTitle = "—",
                    Read = true,
                    Text = $"Erstabgleich abgeschlossen: {library.Count} Spiele erfasst. Ab jetzt werden neue Inhalte gemeldet.",
                });
            }
            State.Events.InsertRange(0, events);
            if (State.Events.Count > 3000) State.Events.RemoveRange(3000, State.Events.Count - 3000);
        }
        SaveStateNow();

        var notable = events.Where(e => e.Kind != ChangeKind.Info).ToList();
        var autoStarted = new List<long>();
        if (notable.Count > 0)
        {
            var candidates = notable.Where(e => e.Kind is ChangeKind.NewGame or ChangeKind.Added or ChangeKind.Updated)
                .Select(e => e.GameId).Distinct().Where(Settings.AutoDownloadFor).ToList();
            if (candidates.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(Settings.LibraryRoot))
                    Log.Warn("Automatischer Download übersprungen: kein Bibliotheksordner festgelegt.");
                else
                {
                    EnqueueMissing(candidates, explicitRequest: false);
                    autoStarted = candidates;
                }
            }
            NewContentFound?.Invoke(notable, autoStarted);
        }

        var summary = $"Prüfung abgeschlossen ({Fmt.Time(now)}): {library.Count} Spiele, " +
                      (notable.Count == 0 ? "keine neuen Inhalte." : $"{notable.Count} Änderungen.");
        Log.Info(summary + $" Details abgerufen: {toFetch.Count}.");
        SetStatus(summary);
        LibraryChanged?.Invoke();
    }

    // ---------- Downloads planen ----------

    /// <summary>Alle fehlenden/veralteten Dateien der gewünschten Inhalte. <paramref name="gameIds"/> = null → ganze Bibliothek.</summary>
    public List<DownloadRequest> PlanMissing(IEnumerable<long>? gameIds, bool explicitRequest)
    {
        var requests = new List<DownloadRequest>();
        lock (Sync)
        {
            var staged = State.PendingMoves.Select(m => m.Key).ToHashSet();   // liegen schon fertig im Zwischenspeicher
            var games = gameIds == null
                ? State.Games.Values.Where(g => !g.Removed)
                : gameIds.Distinct().Select(id => State.Games.GetValueOrDefault(id)).OfType<GameSnapshot>();
            foreach (var game in games.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!explicitRequest && Settings.RuleFor(game.Id) == GameRule.Ignore) continue;
                foreach (var item in Planner.SelectWanted(game, Settings))
                    foreach (var file in item.Files)
                        if (!staged.Contains(Planner.LocalKey(item, file)) && !Planner.FileDone(item, file, State, Settings.LibraryRoot))
                            requests.Add(new DownloadRequest(game.Id, game.Title, item, file, Planner.RelativeDir(game, item)));
            }
        }
        return requests;
    }

    public (int Count, long Bytes) EnqueueMissing(IEnumerable<long>? gameIds, bool explicitRequest) =>
        Downloads.Enqueue(PlanMissing(gameIds, explicitRequest));

    /// <summary>Fertige Teildatei an ihren Platz bringen; ältere Fassungen je nach Einstellung archivieren/löschen.</summary>
    internal void CompleteDownload(DownloadRequest request, string part, string final, long length, string? md5, bool verified)
    {
        var root = Settings.LibraryRoot;
        var key = Planner.LocalKey(request.Item, request.File);
        lock (Sync)
        {
            State.LocalFiles.TryGetValue(key, out var previous);
            var relative = Path.GetRelativePath(root, final);
            if (File.Exists(final))
            {
                bool sameVersion = previous != null && previous.Fingerprint == request.Item.Fingerprint
                                   && string.Equals(previous.RelativePath, relative, StringComparison.OrdinalIgnoreCase);
                if (sameVersion) File.Delete(final);
                else RetireFile(final, root, forceMove: true);
            }
            File.Move(part, final);

            if (previous != null && !string.Equals(previous.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
                RetireFile(Path.Combine(root, previous.RelativePath), root, forceMove: false);

            State.LocalFiles[key] = new LocalFile
            {
                GameId = request.GameId,
                ItemKey = request.Item.Key,
                FileId = request.File.Id,
                Fingerprint = request.Item.Fingerprint,
                Version = request.Item.Version,
                RelativePath = relative,
                Length = length,
                Md5 = md5,
                Verified = verified,
                DownloadedAt = DateTimeOffset.Now,
            };
            CleanupOrphans(request.Item, root);
        }
        RequestSave();
    }

    // ---------- Zwischenspeicher ----------

    internal void AddPendingMove(PendingMove move)
    {
        lock (Sync)
        {
            State.PendingMoves.RemoveAll(m => m.Key == move.Key);
            State.PendingMoves.Add(move);
        }
        RequestSave();
    }

    internal void RemovePendingMove(PendingMove move)
    {
        lock (Sync) State.PendingMoves.Remove(move);
        RequestSave();
    }

    /// <summary>Übertragene Datei (<paramref name="tmpOnTarget"/> liegt schon auf dem Ziellaufwerk) an ihren Platz bringen
    /// und vermerken – mit denselben Regeln für alte Versionen wie bei direkten Downloads.</summary>
    internal void CompleteMove(PendingMove move, string tmpOnTarget, string final)
    {
        DownloadRequest? request = null;
        lock (Sync)
        {
            if (State.Games.TryGetValue(move.GameId, out var game))
            {
                var item = game.Items.FirstOrDefault(i => i.Key == move.ItemKey);
                var file = item?.Files.FirstOrDefault(f => f.Id == move.FileId);
                if (item != null && file != null && item.Fingerprint == move.Fingerprint)
                    request = new DownloadRequest(game.Id, game.Title, item, file, Planner.RelativeDir(game, item));
            }
        }
        if (request != null)
        {
            CompleteDownload(request, tmpOnTarget, final, move.Length, move.Md5, move.Verified);
            return;
        }

        // GOG hat den Eintrag inzwischen geändert: Datei trotzdem ablegen (sie zählt dann als „veraltet“), ohne Aufräumen.
        var root = Settings.LibraryRoot;
        lock (Sync)
        {
            if (File.Exists(final)) RetireFile(final, root, forceMove: true);
            File.Move(tmpOnTarget, final);
            State.LocalFiles[move.Key] = new LocalFile
            {
                GameId = move.GameId,
                ItemKey = move.ItemKey,
                FileId = move.FileId,
                Fingerprint = move.Fingerprint,
                Version = move.Version,
                RelativePath = Path.GetRelativePath(root, final),
                Length = move.Length,
                Md5 = move.Md5,
                Verified = move.Verified,
                DownloadedAt = DateTimeOffset.Now,
            };
        }
        RequestSave();
    }

    internal void AdoptExisting(DownloadRequest request, string final, long length, string md5)
    {
        lock (Sync)
        {
            State.LocalFiles[Planner.LocalKey(request.Item, request.File)] = new LocalFile
            {
                GameId = request.GameId,
                ItemKey = request.Item.Key,
                FileId = request.File.Id,
                Fingerprint = request.Item.Fingerprint,
                Version = request.Item.Version,
                RelativePath = Path.GetRelativePath(Settings.LibraryRoot, final),
                Length = length,
                Md5 = md5,
                Verified = true,
                DownloadedAt = DateTimeOffset.Now,
            };
        }
        RequestSave();
    }

    /// <summary>Hat eine neue Version weniger Teile, liegen die überzähligen alten Teile noch herum.</summary>
    void CleanupOrphans(RemoteItem item, string root)
    {
        var current = item.Files.Select(f => Planner.LocalKey(item, f)).ToHashSet();
        bool complete = current.All(k => State.LocalFiles.TryGetValue(k, out var lf) && lf.Fingerprint == item.Fingerprint);
        if (!complete) return;
        foreach (var orphan in State.LocalFiles.Where(kv => kv.Value.ItemKey == item.Key && !current.Contains(kv.Key)).ToList())
        {
            RetireFile(Path.Combine(root, orphan.Value.RelativePath), root, forceMove: false);
            State.LocalFiles.Remove(orphan.Key);
        }
    }

    void RetireFile(string path, string root, bool forceMove)
    {
        try
        {
            if (!File.Exists(path)) return;
            var mode = Settings.OldVersions;
            if (mode == OldVersionMode.Keep && !forceMove) return;
            if (mode == OldVersionMode.Delete)
            {
                File.Delete(path);
                Log.Info($"Alte Version gelöscht: {path}");
                return;
            }
            var target = PathUtil.UniquePath(Path.Combine(root, "_Archiv", Path.GetRelativePath(root, path)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(path, target);
            Log.Info($"Alte Version archiviert: {target}");
        }
        catch (Exception ex)
        {
            Log.Error($"Alte Version konnte nicht verschoben werden: {path}", ex);
        }
    }

    // ---------- Einstellungen & Protokoll ----------

    public void UpdateSettings(Settings settings)
    {
        Settings = settings;
        SaveSettings();
        try { Autostart.Apply(settings.Autostart); }
        catch (Exception ex) { Log.Error("Autostart konnte nicht gesetzt werden", ex); }
        Downloads.ApplySettings();
        LibraryChanged?.Invoke();
    }

    /// <summary>Darstellungs-Einstellungen sofort übernehmen und speichern (ohne die Bibliothek neu zu bewerten).</summary>
    public void UpdateAppearance(Action<Settings> change)
    {
        var settings = Settings.Clone();
        change(settings);
        Settings = settings;
        SaveSettings();
    }

    public void SetRule(long gameId, GameRule rule)
    {
        // Kopie statt Änderung an Ort und Stelle: der Wächter liest die Regeln parallel.
        var rules = new Dictionary<long, GameRule>(Settings.GameRules);
        if (rule == GameRule.Default) rules.Remove(gameId);
        else rules[gameId] = rule;
        Settings.GameRules = rules;
        SaveSettings();
    }

    /// <summary>Gehört der Zielpfad schon einer anderen Datei (gleicher Name bei zwei Einträgen)?</summary>
    internal bool IsPathUsedByOther(string localKey, string fullPath)
    {
        var relative = Path.GetRelativePath(Settings.LibraryRoot, fullPath);
        lock (Sync)
            return State.LocalFiles.Any(kv => kv.Key != localKey
                                              && string.Equals(kv.Value.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
    }

    public void MarkRead(Func<ChangeEvent, bool> predicate)
    {
        bool changed = false;
        lock (Sync)
        {
            foreach (var e in State.Events.Where(e => !e.Read && predicate(e)))
            {
                e.Read = true;
                changed = true;
            }
        }
        if (!changed) return;
        RequestSave();
        LibraryChanged?.Invoke();
    }

    public int UnreadCount()
    {
        lock (Sync) return State.Events.Count(e => !e.Read && e.Kind != ChangeKind.Info);
    }

    public void SaveSettings()
    {
        try
        {
            string json;
            lock (Sync) json = JsonStore.Serialize(Settings);
            lock (_saveLock) JsonStore.WriteAtomic(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("Einstellungen konnten nicht gespeichert werden", ex);
        }
    }

    /// <summary>Speichert spätestens nach 3 s (gedrosselt, nicht verschoben – sonst würde bei Dauerbetrieb nie gespeichert).</summary>
    public void RequestSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 0) _saveTimer.Change(3000, Timeout.Infinite);
    }

    public void SaveStateNow()
    {
        Interlocked.Exchange(ref _savePending, 0);
        try
        {
            string json;
            lock (Sync) json = JsonStore.Serialize(State);
            lock (_saveLock) JsonStore.WriteAtomic(AppPaths.StateFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("Zustand konnte nicht gespeichert werden", ex);
        }
    }
}
