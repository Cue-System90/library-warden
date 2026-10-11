using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace LibraryWarden.Core;

public sealed record DownloadRequest(long GameId, string GameTitle, RemoteItem Item, RemoteFile File, string RelativeDir);

public enum JobStatus { Queued, Running, Verifying, Done, Failed, Canceled, Staged, Moving }

/// <summary>Ein Eintrag der Download-Warteschlange (bindbar für die Oberfläche).</summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    public DownloadJob(DownloadRequest request)
    {
        Request = request;
        Key = Planner.LocalKey(request.Item, request.File);
        _total = request.File.Size;
        _fileName = request.Item.Files.Count > 1 ? $"[{request.Item.Files.IndexOf(request.File) + 1}/{request.Item.Files.Count}] {request.Item.Name}" : request.Item.Name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadRequest Request { get; }
    public string Key { get; }
    public string GameTitle => Request.GameTitle;

    public string ItemText
    {
        get
        {
            var i = Request.Item;
            var dlc = i.IsDlc ? "DLC · " : "";
            return i.Kind == ItemKind.Extra
                ? $"{dlc}Extra"
                : $"{dlc}{Fmt.Kind(i.Kind)} · {Fmt.Os(i.Os)} · {Fmt.Language(i)}";
        }
    }

    string _fileName;
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    JobStatus _status;
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(IsActive));
                if (value is not (JobStatus.Running or JobStatus.Moving)) Speed = 0;
            }
        }
    }

    /// <summary>Noch nicht fertig – inklusive Dateien, die im Zwischenspeicher auf die Übertragung warten.</summary>
    public bool IsActive => _status is JobStatus.Queued or JobStatus.Running or JobStatus.Verifying or JobStatus.Staged or JobStatus.Moving;

    /// <summary>Lädt bzw. wartet aufs Laden (ohne die Übertragung aus dem Zwischenspeicher).</summary>
    public bool IsDownloading => _status is JobStatus.Queued or JobStatus.Running or JobStatus.Verifying;

    string? _detail;
    public string? Detail { get => _detail; set { if (Set(ref _detail, value)) Raise(nameof(StatusText)); } }

    string? _error;
    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(StatusText)); } }

    long _done;
    public long BytesDone => _done;

    long _total;
    public long TotalBytes { get => _total; set { if (Set(ref _total, value)) RaiseProgress(); } }

    double _speed;
    public double Speed { get => _speed; private set { if (Set(ref _speed, value)) Raise(nameof(SpeedText)); } }

    public double Percent => _total > 0 ? Math.Min(100, _done * 100.0 / _total) : 0;
    public string ProgressText => _done <= 0 ? Fmt.Bytes(_total) : _total > 0 ? $"{Fmt.Bytes(_done)} / {Fmt.Bytes(_total)}" : Fmt.Bytes(_done);
    public string SpeedText => _status is JobStatus.Running or JobStatus.Moving ? Fmt.Speed(_speed) : "";

    public string StatusText => _status switch
    {
        JobStatus.Queued => "Wartet",
        JobStatus.Running => _detail ?? "Lädt",
        JobStatus.Verifying => "Prüfe Prüfsumme …",
        JobStatus.Staged => _detail ?? "Im Zwischenspeicher",
        JobStatus.Moving => "Wird übertragen …",
        JobStatus.Done => "Fertig",
        JobStatus.Failed => $"Fehler: {_error}",
        _ => "Abgebrochen",
    };

    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }
    /// <summary>Download endete im Zwischenspeicher (Übertragung steht noch aus).</summary>
    internal bool EndedInStaging { get; set; }
    /// <summary>Im Zwischenspeicher reservierter Platz für diesen laufenden Download.</summary>
    internal long Reserved { get; set; }

    long _lastNotify;

    internal void ReportProgress(long done, long total, double speed, bool force = false)
    {
        _done = done;
        if (total > 0) _total = total;
        if (speed > 0) _speed = speed;
        var now = Environment.TickCount64;
        if (!force && now - _lastNotify < 300) return;
        _lastNotify = now;
        RaiseProgress();
        Raise(nameof(SpeedText));
    }

    void RaiseProgress()
    {
        Raise(nameof(BytesDone));
        Raise(nameof(TotalBytes));
        Raise(nameof(Percent));
        Raise(nameof(ProgressText));
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Warteschlange mit parallelen, fortsetzbaren Downloads, MD5-Prüfung und Bandbreitenbremse.
/// Optional laufen Downloads über einen Zwischenspeicher auf einem schnellen Laufwerk; ein einzelner Kopierer
/// überträgt die fertigen Dateien nacheinander und am Stück auf das (langsame) Bibliothekslaufwerk.</summary>
public sealed class DownloadManager
{
    const int ChunkSize = 128 * 1024;
    const int CopyBufferSize = 4 * 1024 * 1024;
    const long StagingFreeSpaceMargin = 1L * 1024 * 1024 * 1024;
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    readonly VaultService _svc;
    readonly object _lock = new();
    readonly object _stagingLock = new();
    readonly RateLimiter _limiter = new();
    readonly SemaphoreSlim _moveSignal = new(0);
    readonly CancellationTokenSource _moverCts = new();
    Task? _mover;
    long _stagingReserved;
    string _moverStatus = "";
    int _running;
    bool _paused, _exiting;
    int _finishedOk, _finishedFailed;

    public DownloadManager(VaultService svc)
    {
        _svc = svc;
        ApplySettings();
    }

    /// <summary>Für die Oberfläche: Änderungen an der Liste nur unter <see cref="JobsLock"/>.</summary>
    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public object JobsLock => _lock;
    public bool Paused => _paused;

    public event Action? FileCompleted;
    public event Action? QueueFinished;
    public event Action? StateChanged;

    public bool HasActive { get { lock (_lock) return Jobs.Any(j => j.IsActive); } }

    /// <summary>Offene Einträge (inkl. Zwischenspeicher), noch zu ladende Bytes und aktuelle Download-Geschwindigkeit.</summary>
    public (int Active, long RemainingBytes, double Speed) Totals()
    {
        lock (_lock)
        {
            var active = Jobs.Where(j => j.IsActive).ToList();
            return (active.Count,
                active.Where(j => j.IsDownloading).Sum(j => Math.Max(0, j.TotalBytes - j.BytesDone)),
                active.Sum(j => j.Status == JobStatus.Running ? j.Speed : 0));
        }
    }

    /// <summary>Zustand des Zwischenspeichers für die Anzeige.</summary>
    public (int Pending, long PendingBytes, long UsedBytes, long LimitBytes, string MoverStatus) StagingInfo()
    {
        int pending;
        long pendingBytes;
        lock (_svc.Sync)
        {
            pending = _svc.State.PendingMoves.Count;
            pendingBytes = _svc.State.PendingMoves.Sum(m => m.Length);
        }
        return (pending, pendingBytes, pendingBytes + Interlocked.Read(ref _stagingReserved), _svc.Settings.StagingLimitBytes, _moverStatus);
    }

    public HashSet<long> ActiveGameIds() { lock (_lock) return Jobs.Where(j => j.IsActive).Select(j => j.Request.GameId).ToHashSet(); }
    public HashSet<string> ActiveKeys() { lock (_lock) return Jobs.Where(j => j.IsActive).Select(j => j.Key).ToHashSet(); }

    public (int Ok, int Failed) TakeFinishedStats()
    {
        lock (_lock)
        {
            var stats = (_finishedOk, _finishedFailed);
            _finishedOk = _finishedFailed = 0;
            return stats;
        }
    }

    public void ApplySettings()
    {
        _limiter.SetLimit(_svc.Settings.BandwidthLimitMBps * 1024 * 1024);
        Pump();
    }

    public (int Count, long Bytes) Enqueue(IEnumerable<DownloadRequest> requests)
    {
        int count = 0;
        long bytes = 0;
        HashSet<string> staged;
        lock (_svc.Sync) staged = _svc.State.PendingMoves.Select(m => m.Key).ToHashSet();
        lock (_lock)
        {
            var existing = new Dictionary<string, DownloadJob>();
            foreach (var job in Jobs) existing.TryAdd(job.Key, job);
            foreach (var request in requests)
            {
                var key = Planner.LocalKey(request.Item, request.File);
                if (staged.Contains(key)) continue;   // liegt schon fertig im Zwischenspeicher
                if (existing.TryGetValue(key, out var old))
                {
                    if (old.IsActive) continue;   // läuft schon oder wartet bereits
                    Jobs.Remove(old);             // erledigter/fehlgeschlagener Eintrag wird ersetzt
                }
                var job = new DownloadJob(request);
                existing[key] = job;
                Jobs.Add(job);
                count++;
                bytes += request.File.Size;
            }
        }
        if (count > 0) Log.Info($"{count} Dateien in die Warteschlange gestellt ({Fmt.Bytes(bytes)}).");
        PersistQueue();
        StateChanged?.Invoke();
        Pump();
        return (count, bytes);
    }

    public void RestoreQueue()
    {
        var requests = new List<DownloadRequest>();
        var stagedJobs = new List<DownloadJob>();
        lock (_svc.Sync)
        {
            _paused = _svc.State.DownloadsPaused;

            // Dateien, die beim letzten Mal schon im Zwischenspeicher lagen: nur noch übertragen
            foreach (var move in _svc.State.PendingMoves)
            {
                _svc.State.Games.TryGetValue(move.GameId, out var game);
                var item = game?.Items.FirstOrDefault(i => i.Key == move.ItemKey);
                var file = item?.Files.FirstOrDefault(f => f.Id == move.FileId);
                if (item == null || file == null)
                {
                    file = new RemoteFile { Id = move.FileId, Size = move.Length };
                    item = new RemoteItem { Key = move.ItemKey, Name = Path.GetFileName(move.TargetRelativePath), Files = [file] };
                }
                var job = new DownloadJob(new DownloadRequest(move.GameId, move.GameTitle, item, file, Path.GetDirectoryName(move.TargetRelativePath) ?? ""))
                {
                    FileName = Path.GetFileName(move.TargetRelativePath),
                    Status = JobStatus.Staged,
                };
                job.ReportProgress(move.Length, move.Length, 0, force: true);
                stagedJobs.Add(job);
            }
            var stagedKeys = _svc.State.PendingMoves.Select(m => m.Key).ToHashSet();

            foreach (var q in _svc.State.Queue)
            {
                if (!_svc.State.Games.TryGetValue(q.GameId, out var game)) continue;
                var item = game.Items.FirstOrDefault(i => i.Key == q.ItemKey);
                var file = item?.Files.FirstOrDefault(f => f.Id == q.FileId);
                if (item == null || file == null || stagedKeys.Contains(Planner.LocalKey(item, file))
                    || Planner.FileDone(item, file, _svc.State, _svc.Settings.LibraryRoot)) continue;
                requests.Add(new DownloadRequest(game.Id, game.Title, item, file, Planner.RelativeDir(game, item)));
            }
        }
        if (stagedJobs.Count > 0)
        {
            lock (_lock)
                foreach (var job in stagedJobs) Jobs.Add(job);
            Log.Info($"{stagedJobs.Count} Datei(en) im Zwischenspeicher warten auf die Übertragung.");
        }
        if (requests.Count > 0) Enqueue(requests);
        StartMover();
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
            if (paused)
                foreach (var job in Jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Verifying))
                {
                    job.PauseRequested = true;
                    job.Cts?.Cancel();
                }
        }
        PersistQueue();
        StateChanged?.Invoke();
        if (!paused)
        {
            Pump();
            SignalMover();
        }
    }

    /// <summary>Beim Beenden: laufende Downloads anhalten (Teildateien bleiben, Fortsetzung beim nächsten Start).</summary>
    public void SuspendForExit()
    {
        PersistQueue();
        lock (_lock)
        {
            _exiting = true;   // nichts Neues starten; der gespeicherte Pause-Zustand bleibt, wie er war
            foreach (var job in Jobs.Where(j => j.Status is JobStatus.Running or JobStatus.Verifying))
            {
                job.PauseRequested = true;
                job.Cts?.Cancel();
            }
        }
        // Eine laufende Übertragung wird abgebrochen; die Datei bleibt im Zwischenspeicher und wird beim nächsten Start erneut kopiert.
        _moverCts.Cancel();
    }

    public void Cancel(IEnumerable<DownloadJob> jobs)
    {
        lock (_lock)
        {
            foreach (var job in jobs)
            {
                if (job.Status == JobStatus.Queued) job.Status = JobStatus.Canceled;
                else if (job.Status is JobStatus.Running or JobStatus.Verifying)
                {
                    job.PauseRequested = false;
                    job.Cts?.Cancel();
                }
            }
        }
        PersistQueue();
        StateChanged?.Invoke();
    }

    public void Retry(IEnumerable<DownloadJob> jobs)
    {
        lock (_lock)
        {
            foreach (var job in jobs.Where(j => j.Status is JobStatus.Failed or JobStatus.Canceled))
            {
                job.Error = null;
                job.Detail = null;
                job.Status = JobStatus.Queued;
            }
        }
        PersistQueue();
        StateChanged?.Invoke();
        Pump();
    }

    public void ClearFinished()
    {
        lock (_lock)
        {
            foreach (var job in Jobs.Where(j => j.Status is JobStatus.Done or JobStatus.Canceled).ToList())
                Jobs.Remove(job);
        }
        StateChanged?.Invoke();
    }

    void PersistQueue()
    {
        List<QueuedFile> queue;
        bool paused;
        lock (_lock)
        {
            // Dateien im Zwischenspeicher stehen in PendingMoves, nicht in der Download-Warteschlange
            queue = Jobs.Where(j => j.IsDownloading).Select(j => new QueuedFile(j.Request.GameId, j.Request.Item.Key, j.Request.File.Id)).ToList();
            paused = _paused;
        }
        lock (_svc.Sync)
        {
            _svc.State.Queue = queue;
            _svc.State.DownloadsPaused = paused;
        }
        _svc.RequestSave();
    }

    void Pump()
    {
        if (string.IsNullOrWhiteSpace(_svc.Settings.LibraryRoot)) return;
        lock (_lock)
        {
            if (_paused || _exiting) return;
            int max = Math.Clamp(_svc.Settings.ParallelDownloads, 1, 8);
            while (_running < max)
            {
                var next = Jobs.FirstOrDefault(j => j.Status == JobStatus.Queued);
                if (next == null) break;
                _running++;
                next.PauseRequested = false;
                next.Cts = new CancellationTokenSource();
                next.Status = JobStatus.Running;
                var job = next;
                _ = Task.Run(() => RunAsync(job));
            }
        }
    }

    async Task RunAsync(DownloadJob job)
    {
        var ct = job.Cts!.Token;
        try
        {
            job.EndedInStaging = false;
            await DownloadAsync(job, ct);
            job.Detail = null;
            if (job.EndedInStaging)
            {
                job.Status = JobStatus.Staged;   // fertig wird die Datei erst, wenn der Kopierer sie übertragen hat
                SignalMover();
            }
            else
            {
                job.Status = JobStatus.Done;
                lock (_lock) _finishedOk++;
                FileCompleted?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Detail = null;
            job.Status = job.PauseRequested ? JobStatus.Queued : JobStatus.Canceled;
        }
        catch (NotLoggedInException)
        {
            // Anmeldung abgelaufen: Warteschlange anhalten statt alles fehlschlagen zu lassen.
            Log.Warn("Downloads pausiert: Anmeldung bei GOG ist abgelaufen.");
            job.Detail = null;
            job.Status = JobStatus.Queued;
            lock (_lock) _paused = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Download fehlgeschlagen: {job.GameTitle} / {job.FileName}", ex);
            job.Error = ex.Message;
            job.Status = JobStatus.Failed;
            lock (_lock) _finishedFailed++;
        }
        finally
        {
            lock (_lock) _running--;
            PersistQueue();
            StateChanged?.Invoke();
            CheckDrained();
            Pump();
        }
    }

    /// <summary>Meldet „alles erledigt“, wenn nichts mehr lädt, wartet oder im Zwischenspeicher auf die Übertragung wartet.</summary>
    void CheckDrained()
    {
        bool drained;
        lock (_lock) drained = _running == 0 && !_paused && !_exiting && !Jobs.Any(j => j.IsActive);
        if (drained) QueueFinished?.Invoke();
    }

    async Task DownloadAsync(DownloadJob job, CancellationToken ct)
    {
        var settings = _svc.Settings;
        var request = job.Request;
        var dir = Path.Combine(settings.LibraryRoot, request.RelativeDir);
        bool staging = settings.StagingEnabled && await ReserveStagingAsync(job, request.File.Size, ct);
        try
        {
            var workDir = staging ? Path.Combine(settings.EffectiveStagingDir, request.RelativeDir) : dir;
            Directory.CreateDirectory(workDir);
            await DownloadCoreAsync(job, settings, request, dir, workDir, staging, ct);
        }
        finally
        {
            if (staging) ReleaseReservation(job);
        }
    }

    async Task DownloadCoreAsync(DownloadJob job, Settings settings, DownloadRequest request, string dir, string workDir, bool staging, CancellationToken ct)
    {
        for (int round = 1; ; round++)
        {
            job.Detail = "Fordere Download-Link an …";
            var link = await _svc.Api.ResolveDownlinkAsync(request.File.Downlink, ct);
            var checksum = link.ChecksumUrl != null ? await _svc.Api.GetChecksumAsync(link.ChecksumUrl, ct) : null;
            var fileName = PathUtil.SafeFileName(checksum?.Name ?? PathUtil.FileNameFromUrl(link.Url) ?? request.File.Id);
            var final = Path.Combine(dir, fileName);
            if (_svc.IsPathUsedByOther(job.Key, final))
                final = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)} ({PathUtil.SafeFileName(request.File.Id)}){Path.GetExtension(fileName)}");
            job.FileName = Path.GetFileName(final);
            // Mit Zwischenspeicher wird dort geladen und geprüft; das Ziel bekommt die Datei erst danach am Stück.
            var work = staging ? Path.Combine(workDir, Path.GetFileName(final)) : final;
            var part = work + ".part";
            long expected = checksum?.TotalSize > 0 ? checksum.TotalSize : 0;
            if (expected > 0) job.TotalBytes = expected;
            var expectedMd5 = checksum?.Md5 is { Length: 32 } m ? m : null;

            // Liegt die Datei schon vollständig da (z. B. nach verlorenem Zustand)? Dann nur prüfen und übernehmen.
            if (round == 1 && expectedMd5 != null && !File.Exists(part) && File.Exists(final) && new FileInfo(final).Length == expected)
            {
                job.Status = JobStatus.Verifying;
                var existing = await HashAsync(final, job, ct);
                if (existing.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    _svc.AdoptExisting(request, final, expected, existing);
                    return;
                }
                job.Status = JobStatus.Running;
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    job.Detail = null;
                    await TransferAsync(_svc.Api.DownloadClient, link.Url, part, expected, _limiter,
                        (done, total, speed) => job.ReportProgress(done, total, speed), ct);
                    job.ReportProgress(new FileInfo(part).Length, expected, 0, force: true);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && !ct.IsCancellationRequested && IsTransient(ex))
                {
                    Log.Warn($"Download unterbrochen ({job.FileName}, Versuch {attempt}): {ex.Message}");
                    job.Detail = $"Unterbrochen – neuer Versuch {attempt + 1}/5 …";
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt * attempt)), ct);
                    link = await _svc.Api.ResolveDownlinkAsync(request.File.Downlink, ct);   // Links laufen ab
                }
            }

            string? md5 = null;
            if (settings.VerifyChecksums && expectedMd5 != null)
            {
                job.Status = JobStatus.Verifying;
                md5 = await HashAsync(part, job, ct);
                if (!md5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    if (round < 2)
                    {
                        Log.Warn($"Prüfsumme falsch, lade neu: {fileName}");
                        job.Status = JobStatus.Running;
                        continue;
                    }
                    throw new IOException("Prüfsumme stimmt auch nach erneutem Laden nicht.");
                }
            }
            if (!staging)
            {
                _svc.CompleteDownload(request, part, final, new FileInfo(part).Length, md5, md5 != null);
                return;
            }

            if (File.Exists(work)) File.Delete(work);
            File.Move(part, work);
            _svc.AddPendingMove(new PendingMove
            {
                GameId = request.GameId,
                GameTitle = request.GameTitle,
                ItemKey = request.Item.Key,
                FileId = request.File.Id,
                StagedPath = work,
                TargetRelativePath = Path.GetRelativePath(settings.LibraryRoot, final),
                Length = new FileInfo(work).Length,
                Md5 = md5,
                Verified = md5 != null,
                Fingerprint = request.Item.Fingerprint,
                Version = request.Item.Version,
            });
            job.EndedInStaging = true;
            return;
        }
    }

    // ---------- Zwischenspeicher & Kopierer ----------

    /// <summary>Platz im Zwischenspeicher reservieren; wartet, solange er voll ist.
    /// false = Datei direkt aufs Ziel laden (größer als der ganze Zwischenspeicher oder Laufwerk zu voll).</summary>
    internal async Task<bool> ReserveStagingAsync(DownloadJob? job, long size, CancellationToken ct)
    {
        var settings = _svc.Settings;
        long limit = settings.StagingLimitBytes;
        if (size > limit) return false;
        bool waiting = false;
        while (true)
        {
            long pendingBytes;
            lock (_svc.Sync) pendingBytes = _svc.State.PendingMoves.Sum(m => m.Length);
            lock (_stagingLock)
            {
                bool fits = pendingBytes + _stagingReserved + size <= limit;
                long free = FreeSpace(settings.EffectiveStagingDir);
                bool diskOk = free < 0 || free > size + StagingFreeSpaceMargin;
                if (fits && diskOk)
                {
                    _stagingReserved += size;
                    if (job != null)
                    {
                        job.Reserved = size;
                        if (waiting) job.Detail = null;
                    }
                    return true;
                }
                // Nichts in Arbeit, worauf sich das Warten lohnt (z. B. Laufwerk des Zwischenspeichers zu voll) → direkt laden
                if (pendingBytes == 0 && _stagingReserved == 0) return false;
            }
            if (job != null) job.Detail = "Wartet auf Platz im Zwischenspeicher …";
            waiting = true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    void ReleaseReservation(DownloadJob job)
    {
        lock (_stagingLock)
        {
            _stagingReserved = Math.Max(0, _stagingReserved - job.Reserved);
            job.Reserved = 0;
        }
    }

    static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    void SignalMover()
    {
        if (_moveSignal.CurrentCount == 0) _moveSignal.Release();
    }

    void StartMover()
    {
        lock (_lock) _mover ??= Task.Run(() => MoverLoopAsync(_moverCts.Token));
    }

    void SetMoverStatus(string status)
    {
        if (_moverStatus == status) return;
        _moverStatus = status;
        StateChanged?.Invoke();
    }

    DownloadJob? FindJob(string key)
    {
        lock (_lock) return Jobs.FirstOrDefault(j => j.Key == key && j.Status is JobStatus.Staged or JobStatus.Moving);
    }

    /// <summary>Überträgt fertige Dateien nacheinander aus dem Zwischenspeicher auf das Bibliothekslaufwerk.</summary>
    async Task MoverLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PendingMove? next;
            lock (_svc.Sync) next = _svc.State.PendingMoves.FirstOrDefault();
            if (next == null || _paused || string.IsNullOrWhiteSpace(_svc.Settings.LibraryRoot))
            {
                SetMoverStatus(next == null ? "" : _paused ? "Übertragung pausiert" : "Kein Bibliotheksordner festgelegt");
                try { await _moveSignal.WaitAsync(TimeSpan.FromSeconds(next == null ? 30 : 5), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await MoveOneAsync(next, ct);
                SetMoverStatus("");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var job = FindJob(next.Key);
                if (!File.Exists(next.StagedPath))
                {
                    // Quelle verschwunden (z. B. Zwischenspeicher von Hand geleert) → Eintrag verwerfen, Datei neu laden lassen
                    Log.Warn($"Datei im Zwischenspeicher fehlt, wird neu geladen: {next.StagedPath}");
                    _svc.RemovePendingMove(next);
                    if (job != null)
                    {
                        job.Error = "Datei im Zwischenspeicher fehlt – bitte erneut laden";
                        job.Status = JobStatus.Failed;
                    }
                    CheckDrained();
                    continue;
                }
                Log.Warn($"Übertragung auf die Bibliothek fehlgeschlagen ({next.TargetRelativePath}): {ex.Message}");
                SetMoverStatus($"Bibliothekslaufwerk nicht beschreibbar ({ex.Message}) – neuer Versuch in 1 Minute");
                if (job != null)
                {
                    job.Status = JobStatus.Staged;
                    job.Detail = "Ziel nicht erreichbar – neuer Versuch in 1 Min.";
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    internal async Task MoveOneAsync(PendingMove move, CancellationToken ct)
    {
        var root = _svc.Settings.LibraryRoot;
        var final = Path.Combine(root, move.TargetRelativePath);
        var tmp = final + ".part";
        var job = FindJob(move.Key);
        if (job != null)
        {
            job.Detail = null;
            job.Status = JobStatus.Moving;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await CopyFileAsync(move.StagedPath, tmp, (done, total, speed) => job?.ReportProgress(done, total, speed), ct);
        var length = new FileInfo(tmp).Length;
        if (length != move.Length)
        {
            File.Delete(tmp);
            throw new IOException($"Größe auf dem Ziel stimmt nicht ({Fmt.Bytes(length)} statt {Fmt.Bytes(move.Length)}).");
        }

        _svc.CompleteMove(move, tmp, final);
        File.Delete(move.StagedPath);
        DeleteEmptyParents(Path.GetDirectoryName(move.StagedPath), _svc.Settings.EffectiveStagingDir);
        _svc.RemovePendingMove(move);
        Log.Info($"Übertragen: {move.TargetRelativePath} ({Fmt.Bytes(move.Length)})");

        if (job != null)
        {
            job.Detail = null;
            job.Status = JobStatus.Done;
        }
        lock (_lock) _finishedOk++;
        FileCompleted?.Invoke();
        StateChanged?.Invoke();
        CheckDrained();
    }

    /// <summary>Kopiert am Stück mit großem Puffer; die Zieldatei wird vorab auf volle Größe gesetzt (weniger Fragmentierung auf HDDs).</summary>
    static async Task CopyFileAsync(string source, string target, Action<long, long, double>? progress, CancellationToken ct)
    {
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long total = src.Length;
        dst.SetLength(total);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long done = 0;
            var clock = Stopwatch.StartNew();
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Invoke(done, total, clock.Elapsed.TotalSeconds > 0.5 ? done / clock.Elapsed.TotalSeconds : 0);
            }
            await dst.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static void DeleteEmptyParents(string? dir, string stopAt)
    {
        try
        {
            var stop = Path.GetFullPath(stopAt).TrimEnd('\\');
            while (!string.IsNullOrEmpty(dir) && Path.GetFullPath(dir).TrimEnd('\\').Length > stop.Length
                   && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
    }

    static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        TimeoutException => true,
        IOException io => (io.HResult & 0xFFFF) is not (112 or 39),   // 112/39 = Datenträger voll
        _ => false,
    };

    /// <summary>Lädt <paramref name="url"/> nach <paramref name="part"/>; eine vorhandene Teildatei wird per HTTP-Range fortgesetzt.</summary>
    internal static async Task TransferAsync(HttpClient client, string url, string part, long expected, RateLimiter limiter,
        Action<long, long, double>? progress, CancellationToken ct)
    {
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expected > 0 && existing > expected)
        {
            File.Delete(part);
            existing = 0;
        }
        if (expected > 0 && existing == expected) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Der Server antwortet nicht.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(part);
                throw new IOException("Teildatei passt nicht zum Server – Download startet neu.");
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Server antwortet mit {(int)response.StatusCode}.", null, response.StatusCode);

            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existing = 0;
            long total = response.Content.Headers.ContentLength is long length ? existing + length : expected;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                long done = existing;
                var clock = Stopwatch.StartNew();
                var windowStart = TimeSpan.Zero;
                long windowBytes = 0;
                double speed = 0;
                progress?.Invoke(done, total, 0);
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize), stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("Download hängt – seit 60 Sekunden keine Daten.");
                    }
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    windowBytes += read;
                    var window = clock.Elapsed - windowStart;
                    if (window.TotalSeconds >= 1)
                    {
                        speed = windowBytes / window.TotalSeconds;
                        windowBytes = 0;
                        windowStart = clock.Elapsed;
                    }
                    progress?.Invoke(done, total, speed);
                    await limiter.WaitAsync(read, ct);
                }
                await target.FlushAsync(ct);
                if (total > 0 && done != total)
                    throw new IOException($"Verbindung vorzeitig beendet ({Fmt.Bytes(done)} von {Fmt.Bytes(total)}).");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    internal static async Task<string> HashAsync(string path, DownloadJob? job, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 1 << 20), ct)) > 0)
            {
                md5.AppendData(buffer, 0, read);
                done += read;
                job?.ReportProgress(done, stream.Length, 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
