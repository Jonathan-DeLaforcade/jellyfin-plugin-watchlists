using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>Statut d'un titre dans Jellyseerr (MediaStatus de Seerr).</summary>
    public enum SeerrStatus
    {
        Unknown = 1,
        Pending = 2,
        Processing = 3,
        PartiallyAvailable = 4,
        Available = 5,
        Blocklisted = 6,
        Deleted = 7
    }

    public class SeerrMedia
    {
        /// <summary>movie | tv.</summary>
        public string MediaType { get; set; } = string.Empty;

        public string TmdbId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string? PosterPath { get; set; }

        public string? Overview { get; set; }

        public string? ImdbId { get; set; }

        public string? TvdbId { get; set; }

        /// <summary>Null : Jellyseerr ne connaît pas encore ce titre (jamais demandé).</summary>
        public SeerrStatus? Status { get; set; }
    }

    public class SeerrException : Exception
    {
        public SeerrException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }

    /// <summary>
    /// Client Jellyseerr / Seerr (API v1, clé d'API admin). Les demandes sont faites AU NOM de l'utilisateur
    /// (header X-API-User) : ses droits, quotas et l'auto-validation Jellyseerr s'appliquent.
    /// </summary>
    public class SeerrClient
    {
        private static readonly TimeSpan StatusTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UsersTtl = TimeSpan.FromMinutes(10);

        private readonly IHttpClientFactory _http;
        private readonly IWatchlistsSettings _settings;
        private readonly ILogger<SeerrClient> _logger;
        private readonly ConcurrentDictionary<string, (DateTime At, SeerrMedia Media)> _mediaCache = new();
        private readonly SemaphoreSlim _usersLock = new(1, 1);
        private (DateTime At, string Url, Dictionary<string, int> Map) _users = (DateTime.MinValue, string.Empty, new());

        public SeerrClient(IHttpClientFactory http, IWatchlistsSettings settings, ILogger<SeerrClient> logger)
        {
            _http = http;
            _settings = settings;
            _logger = logger;
        }

        public bool IsConfigured
            => !string.IsNullOrWhiteSpace(_settings.Current.SeerrUrl) && !string.IsNullOrWhiteSpace(_settings.Current.SeerrApiKey);

        public static string ToMediaType(string type) => type == "Series" ? "tv" : "movie";

        public static string ToItemType(string mediaType) => mediaType == "tv" ? "Series" : "Movie";

        // ------------------------------------------------------------------ API

        public async Task<List<SeerrMedia>> SearchAsync(string query, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, "search?page=1&language=fr&query=" + Uri.EscapeDataString(query), null, null, ct).ConfigureAwait(false);
            var list = new List<SeerrMedia>();
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var r in results.EnumerateArray())
            {
                var mediaType = Str(r, "mediaType");
                if (mediaType != "movie" && mediaType != "tv")
                {
                    continue;
                }

                var m = new SeerrMedia
                {
                    MediaType = mediaType,
                    TmdbId = Num(r, "id") ?? string.Empty,
                    Title = (mediaType == "movie" ? Str(r, "title") : Str(r, "name")) ?? string.Empty,
                    Year = YearOf(mediaType == "movie" ? Str(r, "releaseDate") : Str(r, "firstAirDate")),
                    PosterPath = Str(r, "posterPath"),
                    Overview = Str(r, "overview"),
                    Status = StatusOf(r)
                };
                if (m.TmdbId.Length > 0)
                {
                    list.Add(m);
                }
            }

            return list;
        }

        /// <summary>Fiche d'un titre (titre, année, affiche, ids externes, statut). Mise en cache 5 min.</summary>
        public async Task<SeerrMedia> GetMediaAsync(string mediaType, string tmdbId, CancellationToken ct, bool bypassCache = false)
        {
            if (mediaType != "movie" && mediaType != "tv")
            {
                throw new SeerrException(400, "mediaType doit valoir movie ou tv.");
            }

            if (!long.TryParse(tmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new SeerrException(400, "tmdbId invalide.");
            }

            var cacheKey = mediaType + ":" + tmdbId;
            if (!bypassCache && _mediaCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < StatusTtl)
            {
                return cached.Media;
            }

            using var doc = await SendAsync(HttpMethod.Get, mediaType + "/" + tmdbId + "?language=fr", null, null, ct).ConfigureAwait(false);
            var r = doc.RootElement;
            var ext = r.TryGetProperty("externalIds", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            var media = new SeerrMedia
            {
                MediaType = mediaType,
                TmdbId = tmdbId,
                Title = (mediaType == "movie" ? Str(r, "title") : Str(r, "name")) ?? string.Empty,
                Year = YearOf(mediaType == "movie" ? Str(r, "releaseDate") : Str(r, "firstAirDate")),
                PosterPath = Str(r, "posterPath"),
                Overview = Str(r, "overview"),
                ImdbId = Str(r, "imdbId") ?? (ext.ValueKind == JsonValueKind.Object ? Str(ext, "imdbId") : null),
                TvdbId = ext.ValueKind == JsonValueKind.Object ? Num(ext, "tvdbId") : null,
                Status = StatusOf(r)
            };
            _mediaCache[cacheKey] = (DateTime.UtcNow, media);
            return media;
        }

        /// <summary>Id Jellyseerr de l'utilisateur Jellyfin (champ jellyfinUserId), ou null s'il n'existe pas dans Jellyseerr.</summary>
        public async Task<int?> FindUserAsync(Guid jellyfinUserId, CancellationToken ct)
        {
            var key = jellyfinUserId.ToString("N");
            await _usersLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var url = _settings.Current.SeerrUrl;
                var fresh = DateTime.UtcNow - _users.At < UsersTtl && _users.Url == url;
                if (!fresh || !_users.Map.ContainsKey(key))
                {
                    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var skip = 0; skip < 10000; skip += 100)
                    {
                        using var doc = await SendAsync(HttpMethod.Get, "user?take=100&skip=" + skip.ToString(CultureInfo.InvariantCulture), null, null, ct).ConfigureAwait(false);
                        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                        {
                            break;
                        }

                        var count = 0;
                        foreach (var u in results.EnumerateArray())
                        {
                            count++;
                            var jf = Str(u, "jellyfinUserId");
                            var id = Num(u, "id");
                            if (!string.IsNullOrEmpty(jf) && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var seerrId))
                            {
                                map[jf.Replace("-", string.Empty, StringComparison.Ordinal)] = seerrId;
                            }
                        }

                        if (count < 100)
                        {
                            break;
                        }
                    }

                    _users = (DateTime.UtcNow, url, map);
                }

                return _users.Map.TryGetValue(key, out var found) ? found : null;
            }
            finally
            {
                _usersLock.Release();
            }
        }

        /// <summary>Crée une demande au nom de l'utilisateur Jellyseerr indiqué (série : toutes les saisons).</summary>
        public async Task RequestAsync(int seerrUserId, string mediaType, string tmdbId, string? tvdbId, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                ["mediaType"] = mediaType,
                ["mediaId"] = long.Parse(tmdbId, NumberStyles.None, CultureInfo.InvariantCulture)
            };
            if (mediaType == "tv")
            {
                body["seasons"] = "all";
                if (long.TryParse(tvdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdb))
                {
                    body["tvdbId"] = tvdb;
                }
            }

            using var doc = await SendAsync(HttpMethod.Post, "request", JsonSerializer.Serialize(body), seerrUserId, ct).ConfigureAwait(false);
            _mediaCache.TryRemove(mediaType + ":" + tmdbId, out _);
        }

        /// <summary>Vérifie l'URL et la clé (GET /settings/main, réservé aux admins Jellyseerr).</summary>
        public async Task<string> TestAsync(CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, "status", null, null, ct).ConfigureAwait(false);
            var version = Str(doc.RootElement, "version") ?? "?";
            using var _ = await SendAsync(HttpMethod.Get, "settings/main", null, null, ct).ConfigureAwait(false);
            return version;
        }

        // ------------------------------------------------------------------ plomberie

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? jsonBody, int? asUser, CancellationToken ct)
        {
            if (!IsConfigured)
            {
                throw new SeerrException(503, "Jellyseerr n'est pas configuré (Tableau de bord → Extensions → Watchlists).");
            }

            var baseUrl = _settings.Current.SeerrUrl.Trim().TrimEnd('/');
            if (!baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl += "/api/v1";
            }

            using var req = new HttpRequestMessage(method, baseUrl + "/" + path);
            req.Headers.Add("X-Api-Key", _settings.Current.SeerrApiKey.Trim());
            if (asUser.HasValue)
            {
                req.Headers.Add("X-API-User", asUser.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (jsonBody is not null)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            var client = _http.CreateClient("Watchlists.Seerr");
            client.Timeout = TimeSpan.FromSeconds(15);
            HttpResponseMessage res;
            try
            {
                res = await client.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Watchlists : Jellyseerr injoignable ({Url})", baseUrl);
                throw new SeerrException(502, "Jellyseerr injoignable : " + ex.Message);
            }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var message = TryMessage(text) ?? res.ReasonPhrase ?? "erreur";
                    var status = res.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when !asUser.HasValue => 502,
                        HttpStatusCode.Forbidden => 403,
                        HttpStatusCode.Conflict => 409,
                        HttpStatusCode.NotFound => 404,
                        HttpStatusCode.BadRequest => 400,
                        _ => 502
                    };
                    if (status == 502)
                    {
                        _logger.LogWarning("Watchlists : Jellyseerr a répondu {Code} sur {Path} : {Message}", (int)res.StatusCode, path, message);
                    }

                    throw new SeerrException(status, "Jellyseerr : " + message);
                }

                return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
            }
        }

        private static string? TryMessage(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error");
            }
            catch (JsonException)
            {
                return string.IsNullOrWhiteSpace(text) ? null : text.Length > 200 ? text[..200] : text;
            }
        }

        private static SeerrStatus? StatusOf(JsonElement r)
        {
            if (r.TryGetProperty("mediaInfo", out var mi) && mi.ValueKind == JsonValueKind.Object
                && mi.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number
                && st.TryGetInt32(out var v) && Enum.IsDefined(typeof(SeerrStatus), v))
            {
                return (SeerrStatus)v;
            }

            return null;
        }

        private static string? Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? Num(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.String => v.GetString(),
                _ => null
            };
        }

        private static int? YearOf(string? date)
            => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
    }
}
