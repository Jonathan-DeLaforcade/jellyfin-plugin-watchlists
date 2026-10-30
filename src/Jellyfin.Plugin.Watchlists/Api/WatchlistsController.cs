using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Watchlists.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Watchlists.Api
{
    /// <summary>
    /// API Watchlists. Authentification Jellyfin standard (header Authorization: MediaBrowser Token=...).
    /// L'utilisateur est toujours celui du token, jamais un paramètre. Les clés d'API sont refusées.
    /// Réponses en JSON camelCase, toujours avec un corps (y compris les suppressions).
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Watchlists")]
    public class WatchlistsController : ControllerBase
    {
        private const string UserIdClaim = "Jellyfin-UserId";
        private const string IsApiKeyClaim = "Jellyfin-IsApiKey";
        private const string AdministratorRole = "Administrator";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(), new LenientGuidConverter() }
        };

        private readonly WatchlistService _service;
        private readonly TagSyncService _tags;
        private readonly SeerrClient _seerr;

        public WatchlistsController(WatchlistService service, TagSyncService tags, SeerrClient seerr)
        {
            _service = service;
            _tags = tags;
            _seerr = seerr;
        }

        // ------------------------------------------------------------------ lecture

        [HttpGet]
        public IActionResult GetMine() => Run(uid => _service.GetMine(uid));

        [HttpGet("Public")]
        public IActionResult GetPublic() => Run(uid => _service.GetPublic(uid));

        [HttpGet("Followed")]
        public IActionResult GetFollowed() => Run(uid => _service.GetFollowed(uid));

        [HttpGet("Invitations")]
        public IActionResult GetInvitations() => Run(uid => _service.GetInvitations(uid));

        [HttpGet("Users")]
        public IActionResult GetUsers() => Run(uid => _service.GetUsers(uid));

        [HttpGet("Containing/{itemId:guid}")]
        public IActionResult GetContaining([FromRoute] Guid itemId) => Run(uid => _service.GetContaining(uid, itemId));

        /// <summary>Listes (tous utilisateurs) contenant ces items : avertissement avant suppression depuis Jellyfin.</summary>
        [HttpGet("DeleteCheck")]
        public IActionResult GetDeleteCheck([FromQuery] string? ids)
            => Run(uid => _service.GetDeleteCheck(uid, ParseIds(ids), IsAdmin));

        /// <summary>Tirage au sort dans les listes communes à moi et aux membres cochés.</summary>
        [HttpGet("Pick")]
        public IActionResult GetPick([FromQuery] string? members, [FromQuery] bool includeSeen = false)
            => Run(uid => _service.GetPick(uid, ParseIds(members), includeSeen));

        [HttpGet("{id:guid}")]
        public IActionResult Get([FromRoute] Guid id) => Run(uid => _service.Get(uid, id, IsAdmin));

        // ------------------------------------------------------------------ listes

        [HttpPost]
        public Task<IActionResult> Create() => RunWithBody<WatchlistInput>((uid, body) => _service.Create(uid, body));

        [HttpPatch("{id:guid}")]
        public Task<IActionResult> Update([FromRoute] Guid id) => RunWithBody<WatchlistInput>((uid, body) => _service.Update(uid, id, body));

        [HttpDelete("{id:guid}")]
        public IActionResult Delete([FromRoute] Guid id) => Run(uid =>
        {
            _service.Delete(uid, id);
            return Done();
        });

        /// <summary>Liste privée commune avec un autre utilisateur. Corps : {userId, fromWatchlistId, entryId}.</summary>
        [HttpPost("Shared")]
        public Task<IActionResult> CreateShared() => RunWithBody<SharedInput>((uid, body) => _service.CreateShared(uid, body));

        // ------------------------------------------------------------------ items

        /// <summary>
        /// Corps : {itemId} (item du serveur), {tmdbId, mediaType: movie|tv} (présent ou non sur le serveur)
        /// ou {fromWatchlistId, fromEntryId} (copie). Réponse : la liste + addedEntryId + matches (autres utilisateurs qui l'ont aussi).
        /// </summary>
        [HttpPost("{id:guid}/Items")]
        public Task<IActionResult> AddItem([FromRoute] Guid id)
            => RunWithBodyAsync<ItemInput>((uid, body) => Box(_service.AddItemAsync(uid, id, body, HttpContext.RequestAborted)));

        [HttpPatch("{id:guid}/Items/{entryId:guid}")]
        public Task<IActionResult> UpdateItem([FromRoute] Guid id, [FromRoute] Guid entryId) => RunWithBody<ItemInput>((uid, body) => _service.UpdateItem(uid, id, entryId, body));

        [HttpDelete("{id:guid}/Items/{entryId:guid}")]
        public IActionResult RemoveItem([FromRoute] Guid id, [FromRoute] Guid entryId)
            => Run(uid => _service.RemoveItem(uid, id, entryId));

        [HttpPut("{id:guid}/Items/Order")]
        public Task<IActionResult> Reorder([FromRoute] Guid id) => RunWithBody<OrderInput>((uid, body) => _service.Reorder(uid, id, body));

        /// <summary>Statut Jellyseerr des titres de la liste absents du serveur.</summary>
        [HttpGet("{id:guid}/Availability")]
        public Task<IActionResult> GetAvailability([FromRoute] Guid id)
            => RunAsync(uid => Box(_service.GetAvailabilityAsync(uid, id, HttpContext.RequestAborted, IsAdmin)));

        // ------------------------------------------------------------------ Jellyseerr

        /// <summary>Jellyseerr est-il configuré (pour afficher la recherche TMDB et les boutons « Demander ») ?</summary>
        [HttpGet("Seerr")]
        public IActionResult GetSeerrState() => Run(_ => _service.GetSeerrState(User.IsInRole(AdministratorRole)));

        /// <summary>Recherche TMDB via Jellyseerr (films et séries), annotée de la présence sur le serveur.</summary>
        [HttpGet("Search")]
        public Task<IActionResult> Search([FromQuery] string? query)
            => RunAsync(uid => Box(_service.SearchAsync(uid, query, HttpContext.RequestAborted)));

        /// <summary>Demande dans Jellyseerr au nom de l'utilisateur. Corps : {tmdbId, mediaType: movie|tv}.</summary>
        [HttpPost("Request")]
        public Task<IActionResult> RequestMedia()
            => RunWithBodyAsync<RequestInput>((uid, body) => Box(_service.RequestAsync(uid, body, HttpContext.RequestAborted)));

        // ------------------------------------------------------------------ admin

        /// <summary>Toutes les watchlists de tous les utilisateurs (privées comprises).</summary>
        [HttpGet("Admin/All")]
        public IActionResult GetAll() => Run(uid => _service.GetAll(uid, IsAdmin));

        [HttpPost("Admin/SyncTags")]
        public Task<IActionResult> SyncTags() => RunAdminAsync(async () => (object)await _tags.SyncAsync(HttpContext.RequestAborted).ConfigureAwait(false));

        [HttpPost("Admin/TestSeerr")]
        public Task<IActionResult> TestSeerr() => RunAdminAsync(async () =>
        {
            try
            {
                var version = await _seerr.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                return new { ok = true, version };
            }
            catch (SeerrException ex)
            {
                throw new WatchlistException(ex.StatusCode == 503 ? 400 : 502, ex.Message);
            }
        });

        // ------------------------------------------------------------------ membres

        [HttpPost("{id:guid}/Members")]
        public Task<IActionResult> Invite([FromRoute] Guid id) => RunWithBody<MemberInput>((uid, body) => _service.Invite(uid, User.IsInRole(AdministratorRole), id, body));

        [HttpPatch("{id:guid}/Members/{memberId:guid}")]
        public Task<IActionResult> UpdateMember([FromRoute] Guid id, [FromRoute] Guid memberId) => RunWithBody<MemberInput>((uid, body) => _service.UpdateMember(uid, id, memberId, body));

        [HttpDelete("{id:guid}/Members/{memberId:guid}")]
        public IActionResult RemoveMember([FromRoute] Guid id, [FromRoute] Guid memberId) => Run(uid =>
        {
            _service.RemoveMember(uid, id, memberId);
            return Done();
        });

        [HttpPost("{id:guid}/Invitation/Accept")]
        public IActionResult Accept([FromRoute] Guid id) => Run(uid => _service.AcceptInvitation(uid, id));

        [HttpPost("{id:guid}/Invitation/Decline")]
        public IActionResult Decline([FromRoute] Guid id) => Run(uid =>
        {
            _service.DeclineInvitation(uid, id);
            return Done();
        });

        // ------------------------------------------------------------------ suivi

        [HttpPost("{id:guid}/Follow")]
        public IActionResult Follow([FromRoute] Guid id) => Run(uid =>
        {
            _service.Follow(uid, id, true);
            return Done();
        });

        [HttpDelete("{id:guid}/Follow")]
        public IActionResult Unfollow([FromRoute] Guid id) => Run(uid =>
        {
            _service.Follow(uid, id, false);
            return Done();
        });

        // ------------------------------------------------------------------ web

        /// <summary>Fragment HTML chargé par Plugin Pages (ApiClient.ajax, donc authentifié).</summary>
        [HttpGet("Web/watchlists.html")]
        [Produces("text/html")]
        public IActionResult GetPage()
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Watchlists.Web.watchlists.html");
            if (stream is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-cache";
            return File(stream, "text/html; charset=utf-8");
        }

        /// <summary>
        /// Script du bouton d'en-tête, injecté dans index.html via File Transformation.
        /// Anonyme car chargé par une balise &lt;script&gt; (sans header d'auth) ; il ne contient aucune donnée.
        /// </summary>
        [HttpGet("Web/header.js")]
        [AllowAnonymous]
        [Produces("application/javascript")]
        public IActionResult GetHeaderScript()
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Watchlists.Web.header.js");
            if (stream is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-cache";
            return File(stream, "application/javascript; charset=utf-8");
        }

        // ------------------------------------------------------------------ plomberie

        private static object Done() => new { ok = true };

        /// <summary>Liste d'ids séparés par des virgules ; les valeurs invalides sont ignorées.</summary>
        private static IEnumerable<Guid> ParseIds(string? csv)
            => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

        private IActionResult Run(Func<Guid, object> action)
        {
            if (!TryGetUserId(out var userId))
            {
                return Error(StatusCodes.Status403Forbidden, "Un compte utilisateur est requis (les clés d'API ne sont pas acceptées).");
            }

            try
            {
                return new JsonResult(action(userId), Json);
            }
            catch (WatchlistException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
        }

        private static async Task<object> Box<T>(Task<T> task) => (await task.ConfigureAwait(false))!;

        private async Task<IActionResult> RunAsync(Func<Guid, Task<object>> action)
        {
            if (!TryGetUserId(out var userId))
            {
                return Error(StatusCodes.Status403Forbidden, "Un compte utilisateur est requis (les clés d'API ne sont pas acceptées).");
            }

            try
            {
                return new JsonResult(await action(userId).ConfigureAwait(false), Json);
            }
            catch (WatchlistException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
        }

        private async Task<IActionResult> RunWithBodyAsync<T>(Func<Guid, T, Task<object>> action)
            where T : new()
        {
            T body;
            try
            {
                body = await ReadBody<T>().ConfigureAwait(false);
            }
            catch (WatchlistException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }

            return await RunAsync(uid => action(uid, body)).ConfigureAwait(false);
        }

        private bool IsAdmin => User.IsInRole(AdministratorRole);

        private async Task<IActionResult> RunAdminAsync(Func<Task<object>> action)
        {
            if (!User.IsInRole(AdministratorRole))
            {
                return Error(StatusCodes.Status403Forbidden, "Réservé aux administrateurs.");
            }

            try
            {
                return new JsonResult(await action().ConfigureAwait(false), Json);
            }
            catch (WatchlistException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
        }

        private bool TryGetUserId(out Guid userId)
        {
            userId = Guid.Empty;
            var isApiKey = User.Claims.FirstOrDefault(c => c.Type == IsApiKeyClaim)?.Value;
            if (string.Equals(isApiKey, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var raw = User.Claims.FirstOrDefault(c => c.Type == UserIdClaim)?.Value;
            return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
        }

        private static IActionResult Error(int status, string message)
            => new JsonResult(new { error = message }, Json) { StatusCode = status };

        private async Task<IActionResult> RunWithBody<T>(Func<Guid, T, object> action)
            where T : new()
        {
            T body;
            try
            {
                body = await ReadBody<T>().ConfigureAwait(false);
            }
            catch (WatchlistException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }

            return Run(uid => action(uid, body));
        }

        private async Task<T> ReadBody<T>()
            where T : new()
        {
            if (Request.ContentLength == 0)
            {
                return new T();
            }

            try
            {
                using var reader = new StreamReader(Request.Body);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new T();
                }

                return JsonSerializer.Deserialize<T>(text, Json) ?? new T();
            }
            catch (JsonException ex)
            {
                throw new WatchlistException(StatusCodes.Status400BadRequest, "JSON invalide : " + ex.Message);
            }
        }
    }
}
