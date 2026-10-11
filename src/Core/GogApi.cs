using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;

namespace LibraryWarden.Core;

public class GogApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class GogAuthException(string message) : GogApiException(message);

public sealed class NotLoggedInException() : Exception("Nicht bei GOG angemeldet.");

public sealed record LibraryEntry(long Id, string Title, string Slug, int Updates, bool IsNew, bool IsHidden, int DlcCount);
public sealed record DownlinkInfo(string Url, string? ChecksumUrl);
public sealed record ChecksumInfo(string? Name, string? Md5, long TotalSize);

/// <summary>Zugriff auf die (inoffiziellen) Schnittstellen, die auch GOG Galaxy und gog.com verwenden.</summary>
public sealed class GogApi
{
    // Öffentlich bekannte Client-Daten von GOG Galaxy (genutzt u. a. von lgogdownloader und Heroic).
    public const string ClientId = "46899977096215655";
    const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    public const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    public static string LoginUrl =>
        $"https://auth.gog.com/auth?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&layout=client2";

    readonly HttpClient _http;
    readonly HttpClient _noRedirect;
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    readonly Dictionary<string, string?> _storePages = new();
    TokenData? _token;

    public HttpClient DownloadClient { get; }
    public event Action? LoginStateChanged;
    public bool IsLoggedIn => _token != null;

    public GogApi(bool loadToken = true)
    {
        var userAgent = $"LibraryWarden/{typeof(GogApi).Assembly.GetName().Version?.ToString(3) ?? "0"}";
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        DownloadClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        _noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = TimeSpan.FromSeconds(20) };
        _noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        if (loadToken) _token = TokenStore.Load();
    }

    // ---------- Anmeldung ----------

    public async Task LoginWithCodeAsync(string code, CancellationToken ct = default)
    {
        var url = $"https://auth.gog.com/token?client_id={ClientId}&client_secret={ClientSecret}&grant_type=authorization_code" +
                  $"&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}";
        var token = await RequestTokenAsync(url, ct);
        await _tokenLock.WaitAsync(ct);
        try
        {
            _token = token;
            TokenStore.Save(token);
        }
        finally { _tokenLock.Release(); }
        Log.Info("Bei GOG angemeldet.");
        LoginStateChanged?.Invoke();
    }

    public void Logout()
    {
        _token = null;
        TokenStore.Delete();
        Log.Info("Abgemeldet.");
        LoginStateChanged?.Invoke();
    }

    async Task<TokenData> RequestTokenAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GogAuthException($"GOG hat die Anmeldung abgelehnt ({(int)resp.StatusCode}).");
        if (!resp.IsSuccessStatusCode)
            throw new GogApiException($"Anmeldeserver antwortet mit {(int)resp.StatusCode}.", resp.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var access = J.Str(r, "access_token");
        var refresh = J.Str(r, "refresh_token");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            throw new GogAuthException("Antwort des Anmeldeservers ist unvollständig.");
        var expires = J.Long(r, "expires_in");
        return new TokenData
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires > 0 ? expires : 3600),
            UserId = J.Str(r, "user_id"),
        };
    }

    async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var token = _token ?? throw new NotLoggedInException();
            if (!forceRefresh && token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.AccessToken;

            var url = $"https://auth.gog.com/token?client_id={ClientId}&client_secret={ClientSecret}" +
                      $"&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(token.RefreshToken)}";
            try
            {
                _token = await RequestTokenAsync(url, ct);
                TokenStore.Save(_token);
                return _token.AccessToken;
            }
            catch (GogAuthException ex)
            {
                Log.Warn($"Token-Erneuerung abgelehnt, Neuanmeldung nötig: {ex.Message}");
                _token = null;
                TokenStore.Delete();
                LoginStateChanged?.Invoke();
                throw new NotLoggedInException();
            }
        }
        finally { _tokenLock.Release(); }
    }

    // ---------- Allgemeiner Abruf ----------

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool auth = true)
    {
        bool forceRefresh = false, refreshed = false;
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (auth)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(forceRefresh, ct));
                forceRefresh = false;
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (attempt < 4 && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && auth && !refreshed)
                {
                    refreshed = forceRefresh = true;
                    continue;
                }
                if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 4)
                {
                    var delay = resp.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                    await Task.Delay(delay > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delay, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new GogApiException($"GOG antwortet mit {(int)resp.StatusCode} ({ShortUrl(url)}).", resp.StatusCode);

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
        }
    }

    static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 2 * attempt * attempt));

    static string ShortUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath : url;

    // ---------- Konto & Bibliothek ----------

    public async Task<string?> GetUsernameAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://embed.gog.com/userData.json", ct);
        return J.Str(doc.RootElement, "username");
    }

    public async Task<HashSet<long>> GetOwnedIdsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://embed.gog.com/user/data/games", ct);
        var owned = new HashSet<long>();
        foreach (var v in J.Arr(doc.RootElement, "owned"))
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var id)) owned.Add(id);
        return owned;
    }

    /// <summary>Alle Spiele der Bibliothek inkl. ausgeblendeter, mit GOGs eigener "Update/Neu"-Markierung.</summary>
    public async Task<List<LibraryEntry>> GetLibraryAsync(Action<string>? progress, CancellationToken ct)
    {
        var result = new Dictionary<long, LibraryEntry>();
        foreach (var hiddenFlag in new[] { 0, 1 })
        {
            int totalPages = 1;
            for (int page = 1; page <= totalPages; page++)
            {
                progress?.Invoke($"Lade Bibliotheksliste … Seite {page}/{totalPages}{(hiddenFlag == 1 ? " (ausgeblendete Spiele)" : "")}");
                using var doc = await GetJsonAsync(
                    $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&sortBy=title&hiddenFlag={hiddenFlag}&page={page}", ct);
                var root = doc.RootElement;
                totalPages = Math.Max(1, (int)J.Long(root, "totalPages"));
                foreach (var p in J.Arr(root, "products"))
                {
                    var id = J.Long(p, "id");
                    if (id <= 0 || (hiddenFlag == 1 && result.ContainsKey(id))) continue;
                    result[id] = new LibraryEntry(
                        id,
                        J.Str(p, "title") ?? $"Produkt {id}",
                        J.Str(p, "slug") ?? "",
                        (int)J.Long(p, "updates"),
                        J.Bool(p, "isNew"),
                        J.Bool(p, "isHidden") || hiddenFlag == 1,
                        (int)J.Long(p, "dlcCount"));
                }
            }
        }
        return [.. result.Values];
    }

    public async Task<List<RemoteItem>> GetProductItemsAsync(long productId, ISet<long>? owned, CancellationToken ct, bool auth = true)
    {
        using var doc = await GetJsonAsync($"https://api.gog.com/products/{productId}?expand=downloads,expanded_dlcs&locale=en-US", ct, auth);
        return ProductParser.Parse(doc.RootElement, owned);
    }

    // ---------- Shopseiten ----------

    /// <summary>Shopseite eines Spiels oder null, wenn es im Shop dieser Sprache/dieses Landes keine gibt.
    /// Nicht jedes Bibliotheksprodukt hat eine: Goodies-Pakete, aus dem Shop genommene Spiele und Titel, die GOG in
    /// manchen Ländern nicht listet (in Deutschland z. B. DOOM 3, Quake, BioShock), leiten auf die Spieleübersicht um.
    /// Die Produktschnittstelle meldet das nicht zuverlässig, daher wird die lokalisierte Seite selbst angefragt.
    /// Eine Ausweichsprache hilft nicht: Der Browser leitet auch die englische Seite für das Land um.</summary>
    public async Task<string?> ResolveStorePageAsync(string slug, string language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var cacheKey = $"{language}/{slug}";
        lock (_storePages)
            if (_storePages.TryGetValue(cacheKey, out var cached)) return cached;

        var url = new Uri($"https://www.gog.com/{language}/game/{Uri.EscapeDataString(slug)}");
        string? result = null;
        for (int hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _noRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)response.StatusCode;
            if (code is >= 200 and < 300)
            {
                result = url.ToString();
                break;
            }
            if (code is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!next.AbsolutePath.Contains("/game/", StringComparison.OrdinalIgnoreCase)) break;   // weg von der Spielseite
                url = next;
                continue;
            }
            break;
        }
        lock (_storePages) _storePages[cacheKey] = result;
        return result;
    }

    /// <summary>Sprachkürzel, das der GOG-Shop in Adressen verwendet (Fallback Englisch).</summary>
    public static string StoreLanguage(System.Globalization.CultureInfo culture) =>
        culture.TwoLetterISOLanguageName is "de" or "fr" or "pl" or "ru" or "zh" ? culture.TwoLetterISOLanguageName : "en";

    // ---------- Downloads ----------

    public async Task<DownlinkInfo> ResolveDownlinkAsync(string downlink, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(downlink, ct);
        var url = J.Str(doc.RootElement, "downlink");
        if (string.IsNullOrEmpty(url)) throw new GogApiException("GOG hat keinen Download-Link geliefert.");
        var checksum = J.Str(doc.RootElement, "checksum");
        return new DownlinkInfo(url, string.IsNullOrEmpty(checksum) ? null : checksum);
    }

    /// <summary>Liest die Prüfsummen-XML (Dateiname, MD5, exakte Größe). Fehlt sie, wird ohne Prüfung geladen.</summary>
    public async Task<ChecksumInfo?> GetChecksumAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Prüfsummen-Datei nicht abrufbar ({(int)resp.StatusCode}).");
                return null;
            }
            var root = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).Root;
            if (root == null || root.Name.LocalName != "file") return null;
            long.TryParse((string?)root.Attribute("total_size"), out var size);
            return new ChecksumInfo((string?)root.Attribute("name"), (string?)root.Attribute("md5"), size);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Prüfsummen-Datei nicht lesbar: {ex.Message}");
            return null;
        }
    }
}
