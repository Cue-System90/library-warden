using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibraryWarden.Core;
using LibraryWarden.UI;

namespace LibraryWarden;

/// <summary>Interne Prüfmodi: "--selftest &lt;bericht.txt&gt;" und "--render-ui &lt;ordner&gt;".
/// Laufen immer in einem eigenen Datenordner (LIBRARYWARDEN_HOME) und berühren weder Konto noch Registry.</summary>
static class DiagnosticsRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable("LIBRARYWARDEN_HOME") == null)
            Environment.SetEnvironmentVariable("LIBRARYWARDEN_HOME", Path.Combine(Path.GetTempPath(), "LibraryWardenTest-" + Guid.NewGuid().ToString("N")[..8]));
        var output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "librarywarden-diagnose");
        try
        {
            AppPaths.Ensure();
            return args[0] == "--selftest" ? await SelfTest.RunAsync(output) : await UiRender.RunAsync(output);
        }
        catch (Exception ex)
        {
            File.WriteAllText(output.TrimEnd('\\') + ".error.txt", ex.ToString());
            return 2;
        }
    }
}

static class SelfTest
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder();
        int failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            report.AppendLine($"{(ok ? "OK  " : "FAIL")}  {name}{(detail != null ? "  –  " + detail : "")}");
            if (!ok) failed++;
        }

        // 1. Pfade und Namen
        var safe = PathUtil.SafeName("The Witcher: Enhanced Edition", "x");
        Check("Ordnername mit Doppelpunkt", safe == "The Witcher - Enhanced Edition", safe);
        safe = PathUtil.SafeName("Tom Clancy's™ <Test>?", "x");
        Check("Ordnername mit Sonderzeichen", safe == "Tom Clancy's Test", safe);
        Check("Reservierter Name", PathUtil.SafeName("CON", "x") == "_CON");
        var urlName = PathUtil.FileNameFromUrl("https://cdn.example.com/a/b/setup_game_1.2%20(64bit).exe?token=abc");
        Check("Dateiname aus URL", urlName == "setup_game_1.2 (64bit).exe", urlName);

        // 2. Echte, öffentliche Produktdaten von GOG
        var api = new GogApi(loadToken: false);
        var stardew = await api.GetProductItemsAsync(1453375253, null, CancellationToken.None, auth: false);
        Check("Stardew Valley: Einträge gelesen", stardew.Count > 0, $"{stardew.Count} Einträge");
        Check("Stardew Valley: Windows-Installer vorhanden", stardew.Any(i => i.Kind == ItemKind.Installer && i.Os == "windows"));
        Check("Stardew Valley: Schlüssel eindeutig", stardew.Select(i => i.Key).Distinct().Count() == stardew.Count);
        Check("Stardew Valley: Download-Links", stardew.SelectMany(i => i.Files).All(f => f.Downlink.StartsWith("https://api.gog.com/products/")));
        var witcher = await api.GetProductItemsAsync(1207658924, null, CancellationToken.None, auth: false);
        var multi = witcher.First(i => i.Kind == ItemKind.Installer && i.Os == "windows");
        Check("The Witcher: mehrteiliger Installer", multi.Files.Count > 1, $"{multi.Files.Count} Teile, {Fmt.Bytes(multi.TotalSize)}");
        Check("The Witcher: Extras gelesen", witcher.Count(i => i.Kind == ItemKind.Extra) > 5, $"{witcher.Count(i => i.Kind == ItemKind.Extra)} Extras");

        // 3. Auswahl
        var game = new GameSnapshot { Id = 1453375253, Title = "Stardew Valley", Items = stardew };
        var wanted = Planner.SelectWanted(game, new Settings());
        Check("Auswahl: nur Windows-Installer", wanted.Any(i => i.Kind == ItemKind.Installer) && wanted.Where(i => i.Kind == ItemKind.Installer).All(i => i.Os == "windows"));
        Check("Auswahl: Extras enthalten", wanted.Any(i => i.Kind == ItemKind.Extra) == stardew.Any(i => i.Kind == ItemKind.Extra));
        Check("Auswahl: standardmäßig keine Patches", !wanted.Any(i => i.Kind == ItemKind.Patch));
        report.AppendLine("      gewählt: " + string.Join(" | ", wanted.Select(Planner.Describe)));
        var withPatch = Planner.SelectWanted(game, new Settings { Patches = PatchMode.LatestOnly });
        var winVersion = wanted.First(i => i.Kind == ItemKind.Installer).Version;
        Check("Patches: nur zur aktuellen Version", withPatch.Where(i => i.Kind == ItemKind.Patch).All(p => p.Os == "windows" && p.Version == winVersion),
            $"{withPatch.Count(i => i.Kind == ItemKind.Patch)} Patch(es)");

        var synthetic = new GameSnapshot { Id = 1, Title = "Test", Items = [Installer("en"), Installer("de"), Installer("fr")] };
        Check("Sprache: Priorität „de, en“ → de", Planner.SelectWanted(synthetic, new Settings { Languages = ["de", "en"] }).Single().Language == "de");
        Check("Sprache: „it“ fehlt → Englisch", Planner.SelectWanted(synthetic, new Settings { Languages = ["it"] }).Single().Language == "en");
        Check("Sprache: alle gewählten", Planner.SelectWanted(synthetic, new Settings { Languages = ["de", "fr"], AllSelectedLanguages = true }).Count == 2);

        // 4. Änderungserkennung
        var oldItems = Clone(stardew);
        var newItems = Clone(stardew);
        var updated = newItems.First(i => i.Kind == ItemKind.Installer && i.Os == "windows");
        updated.Version = "9.9.9";
        updated.TotalSize += 1024 * 1024;
        newItems.Add(new RemoteItem
        {
            Key = "1453375253:extra:999999", ProductId = 1453375253, ProductTitle = "Stardew Valley", Kind = ItemKind.Extra,
            Name = "Soundtrack (FLAC)", TotalSize = 300_000_000,
            Files = [new RemoteFile { Id = "999999", Size = 300_000_000, Downlink = "https://api.gog.com/x" }],
        });
        var removedExtra = newItems.FirstOrDefault(i => i.Kind == ItemKind.Extra && i.Key != "1453375253:extra:999999");
        if (removedExtra != null) newItems.Remove(removedExtra);
        var events = Planner.Diff(game, oldItems, newItems, new Settings());
        foreach (var ev in events) report.AppendLine($"      {Fmt.Change(ev.Kind)}: {ev.Text}");
        Check("Änderung: Installer-Update erkannt", events.Any(e => e.Kind == ChangeKind.Updated && e.Text.Contains("9.9.9")));
        Check("Änderung: neues Extra erkannt", events.Any(e => e.Kind == ChangeKind.Added && e.Text.Contains("FLAC")));
        Check("Änderung: entferntes Extra erkannt", removedExtra == null || events.Any(e => e.Kind == ChangeKind.Removed));
        Check("Änderung: unverändert → keine Meldung", Planner.Diff(game, oldItems, Clone(stardew), new Settings()).Count == 0);
        var macChanged = Clone(stardew);
        var mac = macChanged.FirstOrDefault(i => i.Kind == ItemKind.Installer && i.Os == "mac");
        if (mac != null) mac.Version = "mac-neu";
        Check("Änderung: Mac-Update bei reiner Windows-Auswahl ignoriert", Planner.Diff(game, oldItems, macChanged, new Settings()).Count == 0);
        var withDlc = Clone(stardew);
        withDlc.Add(new RemoteItem
        {
            Key = "777:installer:installer_windows_en", ProductId = 777, ProductTitle = "Test-DLC", IsDlc = true, Kind = ItemKind.Installer,
            Os = "windows", Language = "en", Version = "1.0", TotalSize = 10, Files = [new RemoteFile { Id = "en1installer0", Size = 10, Downlink = "x" }],
        });
        Check("Änderung: neuer DLC", Planner.Diff(game, oldItems, withDlc, new Settings()).Count(e => e.Text.StartsWith("Neuer DLC")) == 1);

        // 5. Zustand speichern/laden
        var state = new AppState();
        state.Games[game.Id] = game;
        state.LocalFiles["k"] = new LocalFile { RelativePath = @"a\b.exe", Length = 5 };
        var back = JsonSerializer.Deserialize<AppState>(JsonStore.Serialize(state), JsonStore.Options)!;
        Check("Zustand: speichern und laden", back.Games[game.Id].Items.Count == stardew.Count && back.LocalFiles["k"].Length == 5
                                              && back.Games[game.Id].Items[0].Fingerprint == stardew[0].Fingerprint);

        // 6. Download-Engine gegen einen lokalen Testserver
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 12345);
        var md5 = Convert.ToHexStringLower(MD5.HashData(data));
        var testDir = Path.Combine(AppPaths.LocalDir, "dltest");
        Directory.CreateDirectory(testDir);
        using var http = new HttpClient();
        var limiter = new RateLimiter();
        using (var server = new MiniServer(data, supportRange: true))
        {
            var part = Path.Combine(testDir, "a.bin.part");
            using var cts = new CancellationTokenSource();
            try
            {
                await DownloadManager.TransferAsync(http, server.Url, part, data.Length, limiter,
                    (done, _, _) => { if (done >= 1024 * 1024) cts.Cancel(); }, cts.Token);
            }
            catch (OperationCanceledException) { }
            var partial = new FileInfo(part).Length;
            Check("Download: Abbruch hinterlässt Teildatei", partial > 0 && partial < data.Length, Fmt.Bytes(partial));
            await DownloadManager.TransferAsync(http, server.Url, part, data.Length, limiter, null, CancellationToken.None);
            Check("Download: Fortsetzen per HTTP-Range", server.RangeRequests > 0 && new FileInfo(part).Length == data.Length);
            Check("Download: MD5 stimmt", await DownloadManager.HashAsync(part, null, CancellationToken.None) == md5);
        }
        using (var server = new MiniServer(data, supportRange: false))
        {
            var part = Path.Combine(testDir, "b.bin.part");
            File.WriteAllBytes(part, new byte[500_000]);
            await DownloadManager.TransferAsync(http, server.Url, part, data.Length, limiter, null, CancellationToken.None);
            Check("Download: Server ohne Range → sauberer Neustart",
                new FileInfo(part).Length == data.Length && await DownloadManager.HashAsync(part, null, CancellationToken.None) == md5);
        }

        limiter.SetLimit(2 * 1024 * 1024);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 64; i++) await limiter.WaitAsync(64 * 1024, CancellationToken.None);
        Check("Bandbreite: 4 MB bei 2 MB/s ≈ 2 s", clock.Elapsed.TotalSeconds is > 1.6 and < 3.0, $"{clock.Elapsed.TotalSeconds:0.00} s");

        // 7. Versionswechsel, Archiv und Aufräumen
        var svc = new VaultService();
        var root = Path.Combine(AppPaths.LocalDir, "bibliothek");
        var settings = svc.Settings.Clone();
        settings.LibraryRoot = root;
        svc.UpdateSettings(settings);
        var v1 = new RemoteItem
        {
            Key = "5:installer:installer_windows_en", ProductId = 5, Kind = ItemKind.Installer, Os = "windows", Language = "en",
            Version = "1.0", TotalSize = 20,
            Files = [new RemoteFile { Id = "en1installer0", Size = 10 }, new RemoteFile { Id = "en1installer1", Size = 10 }],
        };
        var g5 = new GameSnapshot { Id = 5, Title = "Test: Spiel", Items = [v1] };
        lock (svc.Sync) svc.State.Games[5] = g5;
        string WritePart(RemoteItem item, string name, int length)
        {
            var dir = Path.Combine(root, Planner.RelativeDir(g5, item));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".part");
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }
        foreach (var f in v1.Files)
        {
            var part = WritePart(v1, $"setup_1.0_{f.Id}.bin", 10);
            svc.CompleteDownload(new DownloadRequest(5, g5.Title, v1, f, Planner.RelativeDir(g5, v1)), part, part[..^5], 10, null, false);
        }
        Check("Lokal: Version 1.0 vollständig", Planner.StateOf(v1, svc.State, root) == ItemState.Done);
        var v2 = new RemoteItem
        {
            Key = v1.Key, ProductId = 5, Kind = ItemKind.Installer, Os = "windows", Language = "en", Version = "2.0", TotalSize = 15,
            Files = [new RemoteFile { Id = "en1installer0", Size = 15 }],
        };
        g5.Items = [v2];
        Check("Lokal: Version 2.0 als Update erkannt", Planner.StateOf(v2, svc.State, root) == ItemState.Outdated);
        var part2 = WritePart(v2, "setup_2.0.bin", 15);
        svc.CompleteDownload(new DownloadRequest(5, g5.Title, v2, v2.Files[0], Planner.RelativeDir(g5, v2)), part2, part2[..^5], 15, null, false);
        Check("Lokal: Version 2.0 vollständig", Planner.StateOf(v2, svc.State, root) == ItemState.Done);
        var archiveDir = Path.Combine(root, "_Archiv");
        var archived = Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*", SearchOption.AllDirectories).Length : 0;
        Check("Lokal: beide alten Teile archiviert", archived == 2, $"{archived} Datei(en) in _Archiv");
        Check("Lokal: verwaiste Einträge entfernt", svc.State.LocalFiles.Values.Count(l => l.ItemKey == v2.Key) == 1);
        Check("Ordnerstruktur", Planner.RelativeDir(g5, v2) == Path.Combine("Test - Spiel", "Windows"), Planner.RelativeDir(g5, v2));

        // 8. Zwischenspeicher: Übertragung aufs Ziel und Platzverwaltung
        var svc2 = new VaultService();
        var hdd = Path.Combine(AppPaths.LocalDir, "hdd");
        var cache = Path.Combine(AppPaths.LocalDir, "cache");
        var st = svc2.Settings.Clone();
        st.LibraryRoot = hdd;
        st.StagingEnabled = true;
        st.StagingDir = cache;
        st.StagingMaxGB = 1;
        svc2.UpdateSettings(st);
        var cItem = new RemoteItem
        {
            Key = "9:installer:installer_windows_en", ProductId = 9, Kind = ItemKind.Installer, Os = "windows", Language = "en",
            Version = "1.0", TotalSize = data.Length, Files = [new RemoteFile { Id = "en1installer0", Size = data.Length }],
        };
        var g9 = new GameSnapshot { Id = 9, Title = "Cache-Test", Items = [cItem] };
        lock (svc2.Sync) svc2.State.Games[9] = g9;
        var cRel = Planner.RelativeDir(g9, cItem);
        var stagedFile = Path.Combine(cache, cRel, "setup_cache_test.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
        File.WriteAllBytes(stagedFile, data);
        var move = new PendingMove
        {
            GameId = 9, GameTitle = g9.Title, ItemKey = cItem.Key, FileId = "en1installer0", StagedPath = stagedFile,
            TargetRelativePath = Path.Combine(cRel, "setup_cache_test.bin"), Length = data.Length, Md5 = md5, Verified = true,
            Fingerprint = cItem.Fingerprint, Version = cItem.Version,
        };
        svc2.AddPendingMove(move);
        Check("Zwischenspeicher: Platz für weitere Datei wird angezeigt", svc2.Downloads.StagingInfo().UsedBytes == data.Length);
        await svc2.Downloads.MoveOneAsync(move, CancellationToken.None);
        var onHdd = Path.Combine(hdd, cRel, "setup_cache_test.bin");
        Check("Zwischenspeicher: Datei auf das Ziel übertragen",
            File.Exists(onHdd) && await DownloadManager.HashAsync(onHdd, null, CancellationToken.None) == md5);
        Check("Zwischenspeicher: keine Teildatei auf dem Ziel", !File.Exists(onHdd + ".part"));
        Check("Zwischenspeicher: Kopie im Zwischenspeicher entfernt", !File.Exists(stagedFile) && !Directory.Exists(Path.GetDirectoryName(stagedFile)));
        Check("Zwischenspeicher: Datei gilt als geladen", Planner.StateOf(cItem, svc2.State, hdd) == ItemState.Done);
        Check("Zwischenspeicher: Übertragungsliste leer", svc2.State.PendingMoves.Count == 0);

        Check("Zwischenspeicher: Datei größer als der Speicher → direkt aufs Ziel",
            !await svc2.Downloads.ReserveStagingAsync(null, 2L * 1024 * 1024 * 1024, CancellationToken.None));
        Check("Zwischenspeicher: Platz frei → Reservierung", await svc2.Downloads.ReserveStagingAsync(null, 100L * 1024 * 1024, CancellationToken.None));
        var blocker = new PendingMove { ItemKey = "x", FileId = "y", StagedPath = Path.Combine(cache, "gibt-es-nicht"), Length = 950L * 1024 * 1024 };
        svc2.AddPendingMove(blocker);
        bool waited = false;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try { await svc2.Downloads.ReserveStagingAsync(null, 100L * 1024 * 1024, timeout.Token); }
            catch (OperationCanceledException) { waited = true; }
        }
        Check("Zwischenspeicher: voll → neue Downloads warten", waited);
        svc2.RemovePendingMove(blocker);

        // 9. Shopseiten (echte Abfrage beim GOG-Shop)
        var store = await api.ResolveStorePageAsync("stardew_valley", "de", CancellationToken.None);
        Check("Shopseite: vorhandenes Spiel", store == "https://www.gog.com/de/game/stardew_valley", store ?? "null");
        var goodies = await api.ResolveStorePageAsync("cyberpunk_2077_goodies_collection", "de", CancellationToken.None);
        Check("Shopseite: Goodies-Paket ohne Shopseite erkannt", goodies == null, goodies ?? "null");
        var missing = await api.ResolveStorePageAsync("gibt_es_nicht_123456", "en", CancellationToken.None);
        Check("Shopseite: unbekannter Name erkannt", missing == null, missing ?? "null");
        Check("Shop-Sprache", GogApi.StoreLanguage(new System.Globalization.CultureInfo("de-DE")) == "de"
                              && GogApi.StoreLanguage(new System.Globalization.CultureInfo("it-IT")) == "en");

        Check("Anmelde-URL", GogApi.LoginUrl.StartsWith("https://auth.gog.com/auth?client_id=46899977096215655&redirect_uri=https%3A%2F%2Fembed.gog.com"));

        report.Insert(0, $"LibraryWarden Selbsttest {DateTime.Now:yyyy-MM-dd HH:mm:ss} – {(failed == 0 ? "alles OK" : $"{failed} Fehler")}{Environment.NewLine}{Environment.NewLine}");
        File.WriteAllText(reportPath, report.ToString());
        return failed == 0 ? 0 : 1;
    }

    static RemoteItem Installer(string language) => new()
    {
        Key = $"1:installer:installer_windows_{language}", ProductId = 1, Kind = ItemKind.Installer, Os = "windows",
        Language = language, Version = "1.0", TotalSize = 10, Files = [new RemoteFile { Id = language + "1installer0", Size = 10, Downlink = "x" }],
    };

    static List<RemoteItem> Clone(List<RemoteItem> items) =>
        JsonSerializer.Deserialize<List<RemoteItem>>(JsonSerializer.Serialize(items, JsonStore.Options), JsonStore.Options)!;
}

/// <summary>Minimaler HTTP-Server für den Download-Test (mit oder ohne Range-Unterstützung).</summary>
sealed class MiniServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly byte[] _data;
    readonly bool _supportRange;
    int _rangeRequests;

    public MiniServer(byte[] data, bool supportRange)
    {
        _data = data;
        _supportRange = supportRange;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file.bin";
        _ = AcceptLoopAsync();
    }

    public string Url { get; }
    public int RangeRequests => Volatile.Read(ref _rangeRequests);

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = HandleAsync(client);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                long from = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var m = Regex.Match(line, @"^Range:\s*bytes=(\d+)-", RegexOptions.IgnoreCase);
                    if (m.Success) from = long.Parse(m.Groups[1].Value);
                }
                bool partial = _supportRange && from > 0 && from < _data.Length;
                if (partial) Interlocked.Increment(ref _rangeRequests);
                else from = 0;
                var header = (partial
                                 ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{_data.Length - 1}/{_data.Length}\r\n"
                                 : "HTTP/1.1 200 OK\r\n")
                             + $"Content-Length: {_data.Length - from}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(_data.AsMemory((int)from));
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>Bildschirmfoto eines sichtbaren Fensters inkl. des von Windows gezeichneten Rahmens.</summary>
static class ScreenCapture
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);

    public static void Window(Window window, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

/// <summary>Rendert das Hauptfenster mit Beispieldaten in PNG-Dateien (zur Sichtprüfung der Oberfläche).</summary>
static class UiRender
{
    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var svc = new VaultService();
        App.Service = svc;
        var root = Path.Combine(AppPaths.LocalDir, "bibliothek");
        var settings = svc.Settings.Clone();
        settings.LibraryRoot = root;
        settings.StagingEnabled = true;
        svc.UpdateSettings(settings);

        var api = new GogApi(loadToken: false);
        var samples = new (long Id, string Title)[]
        {
            (1453375253, "Stardew Valley"),
            (1207658924, "The Witcher: Enhanced Edition"),
            (1495134320, "The Witcher 3: Wild Hunt — Remastered"),
        };
        var now = DateTimeOffset.Now;
        foreach (var (id, title) in samples)
        {
            var items = await api.GetProductItemsAsync(id, null, CancellationToken.None, auth: false);
            lock (svc.Sync)
                svc.State.Games[id] = new GameSnapshot { Id = id, Title = title, Slug = "x", FirstSeen = now, LastDetails = now, Items = items };
        }
        lock (svc.Sync)
        {
            svc.State.Games[42] = new GameSnapshot { Id = 42, Title = "Beispiel: ausgeblendetes Spiel", IsHidden = true, FirstSeen = now, DetailsError = "GOG antwortet mit 404" };
            svc.State.BaselineDone = true;
            svc.State.LastQuickCheck = now.AddMinutes(-20);
            svc.State.LastFullCheck = now.AddHours(-5);
            svc.State.Username = "Beispielkonto";

            // Stardew vollständig, The Witcher mit veraltetem Installer
            foreach (var (id, outdated) in new[] { (1453375253L, false), (1207658924L, true) })
            {
                var game = svc.State.Games[id];
                foreach (var item in Planner.SelectWanted(game, svc.Settings))
                    foreach (var file in item.Files)
                    {
                        var rel = Path.Combine(Planner.RelativeDir(game, item), file.Id + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!);
                        File.WriteAllBytes(Path.Combine(root, rel), [1]);
                        svc.State.LocalFiles[Planner.LocalKey(item, file)] = new LocalFile
                        {
                            GameId = id, ItemKey = item.Key, FileId = file.Id, RelativePath = rel, Length = 1,
                            Fingerprint = outdated && item.Kind == ItemKind.Installer ? "alt" : item.Fingerprint,
                        };
                    }
            }

            svc.State.Events.AddRange(
            [
                new ChangeEvent { Time = now.AddMinutes(-20), GameId = 1207658924, GameTitle = "The Witcher: Enhanced Edition", Kind = ChangeKind.Updated, Text = "Installer-Update Windows, English: 1.5 (A) → 1.5 (CS) GOG 0.2" },
                new ChangeEvent { Time = now.AddMinutes(-20), GameId = 1453375253, GameTitle = "Stardew Valley", Kind = ChangeKind.Added, Text = "Neues Extra „Soundtrack (FLAC)“ (1,2 GB)", Read = true },
                new ChangeEvent { Time = now.AddHours(-26), GameId = 1495134320, GameTitle = "The Witcher 3: Wild Hunt — Remastered", Kind = ChangeKind.NewGame, Text = "Neu in der Bibliothek" },
                new ChangeEvent { Time = now.AddDays(-3), GameTitle = "—", Kind = ChangeKind.Info, Text = "Erstabgleich abgeschlossen: 4 Spiele erfasst.", Read = true },
            ]);
        }
        svc.SetRule(42, GameRule.Ignore);

        svc.Downloads.SetPaused(true);
        svc.EnqueueMissing([1495134320], explicitRequest: true);
        DownloadJob? first;
        lock (svc.Downloads.JobsLock) first = svc.Downloads.Jobs.FirstOrDefault();
        if (first != null)
        {
            first.Status = JobStatus.Running;
            first.FileName = "setup_the_witcher_3_wild_hunt_5.01_(64bit).exe";
            first.ReportProgress(1_610_612_736, 4_293_918_720, 12_582_912, force: true);
        }
        // Beispiele für den Zwischenspeicher: eine Datei wird gerade übertragen, eine wartet darauf
        List<DownloadJob> sampleJobs;
        lock (svc.Downloads.JobsLock) sampleJobs = svc.Downloads.Jobs.Skip(1).Take(2).ToList();
        if (sampleJobs.Count == 2)
        {
            sampleJobs[0].Status = JobStatus.Moving;
            sampleJobs[0].FileName = "setup_the_witcher_3_wild_hunt_5.01_(64bit)-1.bin";
            sampleJobs[0].ReportProgress(2_576_980_377, 4_293_918_720, 96_468_992, force: true);
            sampleJobs[1].Status = JobStatus.Staged;
            sampleJobs[1].FileName = "setup_the_witcher_3_wild_hunt_5.01_(64bit)-2.bin";
            sampleJobs[1].ReportProgress(4_293_918_720, 4_293_918_720, 0, force: true);
            foreach (var job in sampleJobs)
                svc.AddPendingMove(new PendingMove
                {
                    GameId = job.Request.GameId, GameTitle = job.GameTitle, ItemKey = job.Request.Item.Key, FileId = job.Request.File.Id,
                    StagedPath = Path.Combine(AppPaths.DefaultStagingDir, job.FileName), TargetRelativePath = job.FileName, Length = 4_293_918_720,
                });
        }

        ThemeManager.Initialize(ThemeChoice.Light, 1.0);
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal,
            Width = 1280,
            Height = 820,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
        };
        window.Show();
        await Task.Delay(1500);
        window.GamesGrid.SelectedItem = window.GamesGrid.Items.Cast<GameRow>().FirstOrDefault(r => r.Id == 1207658924);

        foreach (var theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
        {
            ThemeManager.SetTheme(theme);
            var suffix = theme == ThemeChoice.Dark ? "-dark" : "";
            foreach (var tab in new[] { "library", "changes", "downloads", "settings", "view" })
            {
                if (tab == "view") window.SelectTab("library");
                window.SelectTab(tab);
                await Task.Delay(600);
                Render(window, Path.Combine(outDir, $"ui-{tab}{suffix}.png"));
                if (tab == "settings")
                {
                    // Zur Karte „Zwischenspeicher“ scrollen
                    var title = FindText(window.SettingsPanel, "Zwischenspeicher (");
                    ((title?.Parent as FrameworkElement)?.Parent as FrameworkElement)?.BringIntoView();
                    await Task.Delay(500);
                    Render(window, Path.Combine(outDir, $"ui-settings-cache{suffix}.png"));
                    window.SettingsPanel.ScrollToTop();
                }
            }
            var dialog = new DialogWindow("Es laufen noch Downloads. Sie werden beim nächsten Start fortgesetzt.\n\nLibraryWarden trotzdem beenden?",
                "LibraryWarden beenden", DialogKind.Question, ("yes", "Beenden", true), ("no", "Weiterladen", false))
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false,
            };
            dialog.Show();
            await Task.Delay(400);
            Render(dialog, Path.Combine(outDir, $"dialog{suffix}.png"));
            dialog.Close();
        }

        // Zoom 125 %
        ThemeManager.SetTheme(ThemeChoice.Light);
        ThemeManager.SetScale(1.25);
        window.SelectTab("library");
        await Task.Delay(700);
        Render(window, Path.Combine(outDir, "ui-library-125.png"));
        ThemeManager.SetScale(1.0);

        // Echte Bildschirmfotos (mit Fensterrahmen von Windows) – Fenster kurz sichtbar
        var work = SystemParameters.WorkArea;
        window.WindowState = WindowState.Normal;
        window.Left = work.Left + 20;
        window.Top = work.Top + 20;
        window.Width = Math.Min(1280, work.Width - 40);
        window.Height = Math.Min(820, work.Height - 40);
        window.Topmost = true;
        window.Activate();
        foreach (var theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
        {
            ThemeManager.SetTheme(theme);
            window.SelectTab("library");
            await Task.Delay(900);
            ScreenCapture.Window(window, Path.Combine(outDir, $"screen{(theme == ThemeChoice.Dark ? "-dark" : "")}.png"));
        }
        window.WindowState = WindowState.Maximized;
        await Task.Delay(900);
        ScreenCapture.Window(window, Path.Combine(outDir, "screen-dark-maximized.png"));
        window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Left = -20000;
        ThemeManager.SetTheme(ThemeChoice.Light);

        // Anmeldefenster: lädt WebView2 die GOG-Anmeldeseite? (nur Ansicht, keine Eingaben)
        var login = new LoginWindow(clearCookies: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
        };
        login.Show();
        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline && (login.Web.CoreWebView2 == null || login.LoadingText.Visibility == Visibility.Visible))
            await Task.Delay(250);
        var result = login.Web.CoreWebView2 == null ? "WebView2 nicht gestartet" : $"geladen: {login.Web.Source}";
        if (login.Web.CoreWebView2 != null)
        {
            await Task.Delay(2000);
            await using var png = File.Create(Path.Combine(outDir, "login.png"));
            await login.Web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, png);
        }
        File.WriteAllText(Path.Combine(outDir, "login.txt"), result);
        return 0;
    }

    static FrameworkElement? FindText(DependencyObject root, string prefix)
    {
        if (root is System.Windows.Controls.TextBlock tb && tb.Text.StartsWith(prefix)) return tb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), prefix) is { } found) return found;
        return null;
    }

    static void Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("Lw.WindowBg"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
