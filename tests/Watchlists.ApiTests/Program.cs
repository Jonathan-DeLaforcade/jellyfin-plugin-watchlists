using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchlists;
using Jellyfin.Plugin.Watchlists.Api;
using Jellyfin.Plugin.Watchlists.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

// ------------------------------------------------------------------ données de test
var dataDir = Path.Combine(Path.GetTempPath(), "wl-tests-" + Guid.NewGuid().ToString("N"));
var alice = new User { Id = Guid.NewGuid(), Username = "alice" };
var bob = new User { Id = Guid.NewGuid(), Username = "bob" };
var carol = new User { Id = Guid.NewGuid(), Username = "carol" }; // restreinte : ne voit pas "Restricted"
var admin = new User { Id = Guid.NewGuid(), Username = "admin" };
var users = new FakeUsers(alice, bob, carol, admin);
var lib = new FakeLibrary();
var userData = new FakeUserData();
Repo.Library = lib;
var dune = lib.Add(new Movie { Name = "Dune", ProductionYear = 2021, ProviderIds = { ["Tmdb"] = "438631", ["Imdb"] = "tt1160419" } });
var severance = lib.Add(new Series { Name = "Severance", ProductionYear = 2022, ProviderIds = { ["Tmdb"] = "95396" } });
var restricted = lib.Add(new Movie { Name = "Restricted", ProductionYear = 2020, HiddenFor = { carol.Id }, ProviderIds = { ["Tmdb"] = "1" } });
var episode = lib.Add(new Episode { Name = "S01E01" });
var gone = lib.Add(new Movie { Name = "Gone Movie", ProductionYear = 1999 });

// ------------------------------------------------------------------ faux Jellyseerr
var seerr = new FakeSeerr();
seerr.UserMap[alice.Id.ToString("N")] = 7;
var seerrApp = seerr.Build();
await seerrApp.StartAsync();
var settings = new FakeSettings();
settings.Current.SeerrUrl = seerrApp.Urls.First() + "/";
settings.Current.SeerrApiKey = FakeSeerr.ApiKey;

// ------------------------------------------------------------------ hôte
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
if (Environment.GetEnvironmentVariable("WL_TEST_LOG") == "1") builder.Logging.AddConsole();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton<IApplicationPaths>(new FakePaths(dataDir));
builder.Services.AddSingleton<ILibraryManager>(lib);
builder.Services.AddSingleton<IUserManager>(users);
builder.Services.AddSingleton<IUserDataManager>(userData);
builder.Services.AddSingleton<IWatchlistsSettings>(settings);
builder.Services.AddHttpClient();
new PluginServiceRegistrator().RegisterServices(builder.Services, null!);
builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", null);
builder.Services.AddAuthorization();
builder.Services.AddControllers().AddApplicationPart(typeof(WatchlistsController).Assembly);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// Tests UI : marque un titre comme vu par un utilisateur (données de lecture de Jellyfin).
app.MapPost("/Test/Played", (string user, string item) => { userData.Played.Add((Guid.Parse(user), Guid.Parse(item))); });
await app.StartAsync();
var baseUrl = app.Urls.First();

// Mode "serve" : garde le serveur ouvert pour les tests UI (tests/ui), et publie la config de test.
if (args.Contains("serve"))
{
    var info = new
    {
        baseUrl,
        users = new[] { alice, bob, carol, admin }.Select(u => new { id = u.Id.ToString("N"), name = u.Username }),
        items = new BaseItem[] { dune, severance, restricted, episode, gone }.Select(i => new { Id = i.Id.ToString("N"), i.Name, i.ProductionYear, Type = i.GetType().Name, i.ProviderIds })
    };
    Console.WriteLine("SERVE " + JsonSerializer.Serialize(info));
    await Task.Delay(Timeout.Infinite);
}

// ------------------------------------------------------------------ helpers
int pass = 0, failCount = 0;
void Check(bool ok, string label) { if (ok) pass++; else { failCount++; Console.WriteLine("FAIL: " + label); } }
HttpClient As(User u, bool isAdmin = false, bool apiKey = false)
{
    var c = new HttpClient { BaseAddress = new Uri(baseUrl) };
    c.DefaultRequestHeaders.Add("X-Test-User", u.Id.ToString("N"));
    if (isAdmin) c.DefaultRequestHeaders.Add("X-Test-Admin", "1");
    if (apiKey) c.DefaultRequestHeaders.Add("X-Test-ApiKey", "1");
    return c;
}
async Task<(HttpStatusCode Code, JsonElement Json)> Call(HttpClient c, string method, string url, object? body = null)
{
    var req = new HttpRequestMessage(new HttpMethod(method), url);
    if (body != null) req.Content = JsonContent.Create(body);
    var res = await c.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    var json = string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('[')
        ? default : JsonDocument.Parse(text).RootElement.Clone();
    return (res.StatusCode, json);
}
string N(Guid g) => g.ToString("N");

var A = As(alice); var B = As(bob); var C = As(carol); var ADM = As(admin, isAdmin: true);
var anon = new HttpClient { BaseAddress = new Uri(baseUrl) };

// ------------------------------------------------------------------ scénarios
Check((await Call(anon, "GET", "/Watchlists")).Code == HttpStatusCode.Unauthorized, "anonyme refusé");
Check((await Call(As(alice, apiKey: true), "GET", "/Watchlists")).Code == HttpStatusCode.Forbidden, "clé API refusée");

// création
var r = await Call(A, "POST", "/Watchlists", new { name = "  Films à voir  ", description = "desc" });
Check(r.Code == HttpStatusCode.OK, "création");
var priv = r.Json.GetProperty("id").GetString()!;
Check(r.Json.GetProperty("name").GetString() == "Films à voir", "trim du nom");
Check(r.Json.GetProperty("visibility").GetString() == "Private", "privée par défaut");
Check(r.Json.GetProperty("myRole").GetString() == "Owner", "créateur owner");
Check((await Call(A, "POST", "/Watchlists", new { name = "" })).Code == HttpStatusCode.BadRequest, "nom vide refusé");
Check((await Call(A, "POST", "/Watchlists", new { name = new string('x', 101) })).Code == HttpStatusCode.BadRequest, "nom trop long");
Check((await Call(A, "POST", "/Watchlists", new { name = "x", visibility = "Secret" })).Code == HttpStatusCode.BadRequest, "visibilité invalide");
var badJson = new HttpRequestMessage(HttpMethod.Post, "/Watchlists") { Content = new StringContent("{nope", System.Text.Encoding.UTF8, "application/json") };
Check((await A.SendAsync(badJson)).StatusCode == HttpStatusCode.BadRequest, "JSON invalide -> 400");

// items
r = await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = N(dune.Id), note = "avec Damien" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").GetArrayLength() == 1, "ajout film (GUID format N)");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = dune.Id })).Code == HttpStatusCode.Conflict, "doublon 409");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = severance.Id })).Code == HttpStatusCode.OK, "ajout série");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = episode.Id })).Code == HttpStatusCode.BadRequest, "épisode refusé");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = Guid.NewGuid() })).Code == HttpStatusCode.NotFound, "item inconnu 404");
Check((await Call(C, "POST", $"/Watchlists/{priv}/Items", new { itemId = restricted.Id })).Code == HttpStatusCode.NotFound, "carol ne peut pas éditer la liste privée d'alice (404)");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = restricted.Id })).Code == HttpStatusCode.OK, "ajout restricted");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Items", new { itemId = gone.Id })).Code == HttpStatusCode.OK, "ajout gone");

// réordonnancement
r = await Call(A, "PUT", $"/Watchlists/{priv}/Items/Order", new { itemIds = new[] { N(severance.Id) } });
Check(r.Json.GetProperty("items")[0].GetProperty("title").GetString() == "Severance" && r.Json.GetProperty("items").GetArrayLength() == 4, "réordonnancement partiel");
r = await Call(A, "PATCH", $"/Watchlists/{priv}/Items/{dune.Id}", new { note = "  maj  " });
Check(r.Json.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Dune").GetProperty("note").GetString() == "maj", "note modifiée");

// confidentialité
Check((await Call(B, "GET", $"/Watchlists/{priv}")).Code == HttpStatusCode.NotFound, "privée invisible pour bob");
Check((await Call(B, "GET", "/Watchlists/Public")).Json.GetArrayLength() == 0, "rien en public");

// passage en public
r = await Call(A, "PATCH", $"/Watchlists/{priv}", new { visibility = "public" });
Check(r.Json.GetProperty("visibility").GetString() == "Public", "passage public (insensible à la casse)");
Check((await Call(B, "GET", "/Watchlists/Public")).Json.GetArrayLength() == 1, "bob voit la liste publique");
r = await Call(C, "GET", $"/Watchlists/{priv}");
var carolTitles = r.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
Check(!carolTitles.Contains("Restricted"), "carol ne voit pas l'item restreint (pas de fuite)");
Check(r.Json.GetProperty("itemCount").GetInt32() == carolTitles.Count, "itemCount cohérent avec le filtrage");
Check(!r.Json.GetProperty("permissions").GetProperty("canEdit").GetBoolean() && r.Json.GetProperty("permissions").GetProperty("canFollow").GetBoolean(), "permissions non-membre");
Check((await Call(B, "POST", $"/Watchlists/{priv}/Items", new { itemId = dune.Id })).Code == HttpStatusCode.Forbidden, "bob ne peut pas éditer une liste publique");
Check((await Call(B, "PATCH", $"/Watchlists/{priv}", new { name = "hack" })).Code == HttpStatusCode.Forbidden, "bob ne peut pas renommer");

// item disparu
lib.Remove(gone.Id);
r = await Call(A, "GET", $"/Watchlists/{priv}");
var goneDto = r.Json.GetProperty("items").EnumerateArray().FirstOrDefault(i => i.GetProperty("title").GetString() == "Gone Movie");
Check(goneDto.ValueKind == JsonValueKind.Object && !goneDto.GetProperty("available").GetBoolean(), "item disparu visible des membres, indisponible");
r = await Call(B, "GET", $"/Watchlists/{priv}");
Check(r.Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString() == "Gone Movie" && !i.GetProperty("available").GetBoolean()), "item disparu reste affiché (indisponible) dans une liste publique");

// suivi
Check((await Call(B, "POST", $"/Watchlists/{priv}/Follow")).Code == HttpStatusCode.OK, "bob suit");
Check((await Call(B, "GET", "/Watchlists/Followed")).Json.GetArrayLength() == 1, "bob voit ses suivis");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Follow")).Code == HttpStatusCode.BadRequest, "membre ne peut pas suivre sa liste");

// invitation + liste commune
r = await Call(A, "POST", $"/Watchlists/{priv}/Members", new { userId = N(bob.Id), role = "Editor" });
Check(r.Code == HttpStatusCode.OK, "invitation bob");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Members", new { userId = bob.Id })).Code == HttpStatusCode.Conflict, "double invitation 409");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Members", new { userId = carol.Id, force = true })).Code == HttpStatusCode.Forbidden, "force réservé admin");
Check((await Call(A, "POST", $"/Watchlists/{priv}/Members", new { userId = carol.Id, role = "Owner" })).Code == HttpStatusCode.BadRequest, "invitation owner refusée");
Check((await Call(B, "GET", "/Watchlists/Invitations")).Json.GetArrayLength() == 1, "bob a une invitation");
Check((await Call(B, "GET", "/Watchlists")).Json.GetArrayLength() == 0, "invitation non acceptée absente de 'mes listes'");
Check((await Call(B, "POST", $"/Watchlists/{priv}/Items", new { itemId = dune.Id })).Code == HttpStatusCode.Forbidden, "invité non accepté ne peut pas éditer");
r = await Call(B, "POST", $"/Watchlists/{priv}/Invitation/Accept");
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("permissions").GetProperty("canEdit").GetBoolean(), "bob accepte et peut éditer");
Check((await Call(B, "GET", "/Watchlists/Followed")).Json.GetArrayLength() == 0, "suivi retiré à l'acceptation");
Check((await Call(B, "DELETE", $"/Watchlists/{priv}/Items/{severance.Id}")).Code == HttpStatusCode.OK, "bob editor retire un item");
Check((await Call(B, "DELETE", $"/Watchlists/{priv}/Members/{alice.Id}")).Code == HttpStatusCode.Forbidden, "editor ne retire pas l'owner");
Check((await Call(A, "DELETE", $"/Watchlists/{priv}/Members/{alice.Id}")).Code == HttpStatusCode.BadRequest, "owner ne peut pas quitter");

// containing
r = await Call(B, "GET", $"/Watchlists/Containing/{dune.Id}");
Check(r.Json.GetArrayLength() == 1 && r.Json[0].GetProperty("contains").GetBoolean(), "containing");
Check(r.Json[0].GetProperty("entryId").ValueKind == JsonValueKind.String, "containing : entryId renvoyé");
Check(r.Json[0].GetProperty("visibility").GetString() == "Public" && r.Json[0].GetProperty("shared").GetBoolean(), "containing : visibilité et liste commune");

// avant suppression depuis Jellyfin : toutes les listes, nom masqué pour une liste privée invisible
var carolDel = (await Call(C, "POST", "/Watchlists", new { name = "Carol secrète" })).Json.GetProperty("id").GetString()!;
Check((await Call(C, "POST", $"/Watchlists/{carolDel}/Items", new { itemId = dune.Id })).Code == HttpStatusCode.OK, "carol ajoute dune (privée)");
r = await Call(B, "GET", $"/Watchlists/DeleteCheck?ids={dune.Id},{severance.Id},pasunguid");
Check(r.Code == HttpStatusCode.OK && r.Json.GetArrayLength() == 1 && r.Json[0].GetProperty("name").GetString() == "Dune", "deletecheck : seul dune est dans une liste");
var delLists = r.Json[0].GetProperty("lists").EnumerateArray().ToList();
Check(delLists.Count == 2, "deletecheck : listes de tous les utilisateurs");
Check(delLists.Any(l => l.GetProperty("name").GetString() == "Films à voir"), "deletecheck : nom d'une liste visible");
Check(delLists.Any(l => l.GetProperty("name").ValueKind == JsonValueKind.Null && l.GetProperty("private").GetBoolean() && l.GetProperty("owner").GetProperty("name").GetString() == "carol"), "deletecheck : liste privée invisible -> nom masqué, propriétaire donné");
r = await Call(ADM, "GET", $"/Watchlists/DeleteCheck?ids={dune.Id}");
Check(r.Json[0].GetProperty("lists").EnumerateArray().Any(l => l.GetProperty("name").GetString() == "Carol secrète"), "deletecheck : l'admin voit le nom");
Check((await Call(B, "GET", "/Watchlists/DeleteCheck")).Json.GetArrayLength() == 0, "deletecheck sans ids");
Check((await Call(C, "DELETE", $"/Watchlists/{carolDel}")).Code == HttpStatusCode.OK, "carol supprime sa liste");

// rôles / transfert
Check((await Call(A, "PATCH", $"/Watchlists/{priv}/Members/{bob.Id}", new { role = "Viewer" })).Code == HttpStatusCode.OK, "bob -> viewer");
Check((await Call(B, "POST", $"/Watchlists/{priv}/Items", new { itemId = severance.Id })).Code == HttpStatusCode.Forbidden, "viewer ne peut pas éditer");
r = await Call(A, "PATCH", $"/Watchlists/{priv}/Members/{bob.Id}", new { role = "Owner" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("myRole").GetString() == "Editor", "transfert : alice devient editor");
Check((await Call(B, "GET", $"/Watchlists/{priv}")).Json.GetProperty("permissions").GetProperty("canManage").GetBoolean(), "bob owner");
Check((await Call(A, "DELETE", $"/Watchlists/{priv}/Members/{alice.Id}")).Code == HttpStatusCode.OK, "alice quitte");
Check((await Call(A, "GET", "/Watchlists")).Json.GetArrayLength() == 0, "alice n'a plus la liste");

// refus d'invitation + ajout forcé admin
var r2 = await Call(ADM, "POST", "/Watchlists", new { name = "Admin list" });
var adminList = r2.Json.GetProperty("id").GetString()!;
Check((await Call(ADM, "POST", $"/Watchlists/{adminList}/Members", new { userId = carol.Id, force = true })).Json.GetProperty("memberCount").GetInt32() == 2, "ajout forcé admin");
await Call(ADM, "POST", $"/Watchlists/{adminList}/Members", new { userId = alice.Id });
Check((await Call(A, "POST", $"/Watchlists/{adminList}/Invitation/Decline")).Code == HttpStatusCode.OK, "refus invitation");
Check((await Call(A, "GET", $"/Watchlists/{adminList}")).Code == HttpStatusCode.NotFound, "privée invisible après refus");

// vue admin : toutes les listes, privées comprises, en lecture seule
var bobPriv = (await Call(B, "POST", "/Watchlists", new { name = "Bob secret" })).Json.GetProperty("id").GetString()!;
await Call(B, "POST", $"/Watchlists/{bobPriv}/Items", new { itemId = dune.Id });
r = await Call(ADM, "GET", "/Watchlists/Admin/All");
var allIds = r.Json.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
Check(r.Code == HttpStatusCode.OK && allIds.Contains(bobPriv) && allIds.Contains(priv) && allIds.Contains(adminList), "admin : toutes les listes");
var privAdmin = r.Json.EnumerateArray().First(x => x.GetProperty("id").GetString() == bobPriv);
Check(privAdmin.GetProperty("owner").GetProperty("name").GetString() == "bob" && privAdmin.GetProperty("members").GetArrayLength() == 1
    && privAdmin.GetProperty("itemCount").GetInt32() == 1, "admin : propriétaire, membres, titres");
Check((await Call(A, "GET", "/Watchlists/Admin/All")).Code == HttpStatusCode.Forbidden, "admin/all refusé aux non-admins");
r = await Call(ADM, "GET", $"/Watchlists/{bobPriv}");
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").GetArrayLength() == 1 && !r.Json.GetProperty("permissions").GetProperty("canEdit").GetBoolean(), "admin lit une liste privée, sans droit d'édition");
Check((await Call(ADM, "GET", $"/Watchlists/{bobPriv}/Availability")).Code == HttpStatusCode.OK, "admin : disponibilité d'une liste privée");
Check((await Call(ADM, "POST", $"/Watchlists/{bobPriv}/Items", new { itemId = severance.Id })).Code == HttpStatusCode.NotFound, "admin ne modifie pas une liste privée dont il n'est pas membre");
Check((await Call(C, "GET", $"/Watchlists/{bobPriv}")).Code == HttpStatusCode.NotFound, "non-admin : liste privée toujours invisible");
Check((await Call(C, "GET", $"/Watchlists/{bobPriv}/Availability")).Code == HttpStatusCode.NotFound, "non-admin : disponibilité invisible");

// repassage privé : suivis non-membres purgés
await Call(B, "PATCH", $"/Watchlists/{priv}", new { visibility = "Public" });
await Call(C, "POST", $"/Watchlists/{priv}/Follow");
await Call(B, "PATCH", $"/Watchlists/{priv}", new { visibility = "Private" });
Check((await Call(C, "GET", "/Watchlists/Followed")).Json.GetArrayLength() == 0, "suivi purgé quand privée");
await Call(B, "PATCH", $"/Watchlists/{priv}", new { visibility = "Public" });
Check((await Call(C, "GET", "/Watchlists/Followed")).Json.GetArrayLength() == 0, "suivi non restauré");

// utilisateurs + page
r = await Call(A, "GET", "/Watchlists/Users");
Check(r.Json.GetArrayLength() == 3 && !r.Json.EnumerateArray().Any(u => u.GetProperty("name").GetString() == "alice"), "liste des utilisateurs (sans soi)");
var headerJs = await anon.GetAsync("/Watchlists/Web/header.js");
Check(headerJs.IsSuccessStatusCode && (await headerJs.Content.ReadAsStringAsync()).Contains("watchlistsHeaderButton"), "header.js servi sans authentification");
var html0 = "<html><head></head><body><div id=app></div></BODY></html>";
var html1 = Jellyfin.Plugin.Watchlists.Web.Transformations.IndexHtml(new() { Contents = html0 });
Check(html1.Contains("src=\"../Watchlists/Web/header.js?v=") && html1.IndexOf("header.js", StringComparison.Ordinal) < html1.IndexOf("</BODY>", StringComparison.Ordinal), "index.html : script injecté avant </body>");
Check(Jellyfin.Plugin.Watchlists.Web.Transformations.IndexHtml(new() { Contents = html1 }) == html1, "index.html : injection idempotente");
var page = await A.GetAsync("/Watchlists/Web/watchlists.html");
Check(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("wl-app"), "page HTML embarquée servie");

// ------------------------------------------------------------------ titres absents, Jellyseerr, tags
var tagSync = app.Services.GetRequiredService<TagSyncService>();
var idx = app.Services.GetRequiredService<LibraryIndex>();
var r5 = await Call(A, "POST", "/Watchlists", new { name = "À voir", visibility = "Public" });
var wl = r5.Json.GetProperty("id").GetString()!;
Check((await Call(A, "GET", "/Watchlists/Seerr")).Json.GetProperty("enabled").GetBoolean(), "Jellyseerr configuré");

// recherche
r = await Call(A, "GET", "/Watchlists/Search?query=du");
var res = r.Json.EnumerateArray().ToList();
var duneRes = res.FirstOrDefault(x => x.GetProperty("tmdbId").GetString() == "438631");
var dune2Res = res.FirstOrDefault(x => x.GetProperty("tmdbId").GetString() == "693134");
Check(duneRes.ValueKind == JsonValueKind.Object && duneRes.GetProperty("status").GetString() == "Available" && duneRes.GetProperty("itemId").ValueKind == JsonValueKind.String, "recherche : Dune sur le serveur");
Check(dune2Res.ValueKind == JsonValueKind.Object && dune2Res.GetProperty("status").GetString() == "NotRequested" && dune2Res.GetProperty("canRequest").GetBoolean(), "recherche : Dune 2 absent, demandable");
Check(!res.Any(x => x.GetProperty("mediaType").GetString() == "person"), "recherche : personnes exclues");
Check(seerr.LastApiKey == FakeSeerr.ApiKey, "clé d'API envoyée à Jellyseerr");

// ajout d'un titre absent
r = await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "693134", mediaType = "movie" });
Check(r.Code == HttpStatusCode.OK, "ajout d'un titre absent du serveur");
var absentItem = r.Json.GetProperty("items")[0];
Check(!absentItem.GetProperty("available").GetBoolean() && absentItem.GetProperty("title").GetString() == "Dune : Deuxième partie" && absentItem.GetProperty("year").GetInt32() == 2024 && absentItem.GetProperty("posterPath").GetString() == "/dune2.jpg", "titre absent : métadonnées Jellyseerr");
var absentEntry = absentItem.GetProperty("entryId").GetString()!;
Check((await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "693134", mediaType = "movie" })).Code == HttpStatusCode.Conflict, "doublon TMDB 409");
Check((await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "abc", mediaType = "movie" })).Code == HttpStatusCode.BadRequest, "tmdbId invalide 400");
Check((await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "1", mediaType = "book" })).Code == HttpStatusCode.BadRequest, "mediaType invalide 400");
// ajout par TMDB d'un titre présent : résolu vers l'item du serveur
r = await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "438631", mediaType = "movie" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString() == "Dune" && i.GetProperty("available").GetBoolean()), "ajout TMDB d'un titre présent -> item du serveur");
Check((await Call(A, "POST", $"/Watchlists/{wl}/Items", new { itemId = dune.Id })).Code == HttpStatusCode.Conflict, "même titre par itemId après TMDB : 409");
Check(r.Json.GetProperty("availableCount").GetInt32() == 1 && r.Json.GetProperty("itemCount").GetInt32() == 2, "compteurs sur le serveur / total");
Check(r.Json.GetProperty("posters").EnumerateArray().Any(p => p.GetProperty("posterPath").ValueKind == JsonValueKind.String), "affiche TMDB dans les posters de la carte");
// série absente
r = await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "1399", mediaType = "tv" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "Series" && i.GetProperty("mediaType").GetString() == "tv"), "ajout d'une série absente");

// disponibilité
r = await Call(B, "GET", $"/Watchlists/{wl}/Availability");
var av = r.Json.EnumerateArray().ToList();
Check(av.Count == 2 && av.All(a => a.GetProperty("status").GetString() == "NotRequested" && a.GetProperty("canRequest").GetBoolean()), "statut Jellyseerr des titres absents");
Check(!av.Any(a => a.GetProperty("entryId").GetString() == r.Json.ToString()), "pas de statut pour les titres présents");

// demande
Check((await Call(B, "POST", "/Watchlists/Request", new { tmdbId = "693134", mediaType = "movie" })).Code == HttpStatusCode.Forbidden, "demande refusée : bob absent de Jellyseerr");
r = await Call(A, "POST", "/Watchlists/Request", new { tmdbId = "693134", mediaType = "movie" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("status").GetString() == "Pending", "demande envoyée, statut Demandé");
Check(seerr.Requests.Count == 1 && seerr.Requests[0].AsUser == "7" && seerr.Requests[0].Body.Contains("\"mediaId\":693134"), "demande faite au nom de l'utilisateur Jellyseerr (X-API-User)");
Check((await Call(A, "POST", "/Watchlists/Request", new { tmdbId = "693134", mediaType = "movie" })).Code == HttpStatusCode.Conflict, "deuxième demande : 409");
r = await Call(A, "POST", "/Watchlists/Request", new { tmdbId = "1399", mediaType = "tv" });
Check(r.Code == HttpStatusCode.OK && seerr.Requests[1].Body.Contains("\"seasons\":\"all\"") && seerr.Requests[1].Body.Contains("\"tvdbId\":121361"), "demande série : toutes les saisons + tvdbId");
Check((await Call(A, "POST", "/Watchlists/Request", new { tmdbId = "438631", mediaType = "movie" })).Code == HttpStatusCode.Conflict, "demande d'un titre déjà sur le serveur : 409");
av = (await Call(A, "GET", $"/Watchlists/{wl}/Availability")).Json.EnumerateArray().ToList();
Check(av.Any(a => a.GetProperty("status").GetString() == "Pending" && !a.GetProperty("canRequest").GetBoolean()), "disponibilité : Demandé après la demande");

// confidentialité : carol voit les titres absents, pas l'item restreint présent
await Call(A, "POST", $"/Watchlists/{wl}/Items", new { itemId = restricted.Id });
r = await Call(C, "GET", $"/Watchlists/{wl}");
var ct = r.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
Check(!ct.Contains("Restricted") && ct.Contains("Dune : Deuxième partie"), "carol : absents visibles, restreint masqué");
Check((await Call(C, "GET", "/Watchlists/Search?query=restricted")).Json.EnumerateArray().All(x => x.GetProperty("itemId").ValueKind == JsonValueKind.Null), "recherche : item restreint non révélé à carol");

// ---- tags
var marked = lib.Add(new Movie { Name = "Tag manuel", ProductionYear = 2000, Tags = new[] { "Watchlist", "autre" } });
var sync = await Call(ADM, "POST", "/Watchlists/Admin/SyncTags");
Check(sync.Code == HttpStatusCode.OK, "synchro admin");
Check((await Call(A, "POST", "/Watchlists/Admin/SyncTags")).Code == HttpStatusCode.Forbidden, "synchro réservée aux admins");
bool HasTag(BaseItem i, string t = "watchlist") => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase);
Check(HasTag(dune) && HasTag(restricted), "tag posé sur les titres présents en watchlist");
Check(!HasTag(marked) && marked.Tags.Contains("autre"), "tag retiré d'un titre hors watchlist, autres tags conservés");
Check(!HasTag(severance), "pas de tag sur un titre présent dans aucune liste");
Check(sync.Json.GetProperty("tagged").GetInt32() >= 2 && sync.Json.GetProperty("untagged").GetInt32() == 1, "compteurs de synchro");
var resync = (await Call(ADM, "POST", "/Watchlists/Admin/SyncTags")).Json;
Check(resync.GetProperty("tagged").GetInt32() == 0 && resync.GetProperty("untagged").GetInt32() == 0, "synchro idempotente");

// Maintainerr supprime Dune : l'entrée reste, le tag disparaît avec l'item
lib.Remove(dune.Id);
idx.Invalidate();
r = await Call(A, "GET", $"/Watchlists/{wl}");
var duneEntry = r.Json.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Dune");
Check(!duneEntry.GetProperty("available").GetBoolean() && duneEntry.GetProperty("tmdbId").GetString() == "438631", "après suppression : titre conservé dans la watchlist, indisponible, id TMDB gardé");
await tagSync.SyncAsync(CancellationToken.None);
// Re-téléchargé : nouvel item, nouvel Id, même TMDB
var dune2 = lib.Add(new Movie { Name = "Dune", ProductionYear = 2021, ProviderIds = { ["Tmdb"] = "438631" } });
idx.Invalidate();
r = await Call(A, "GET", $"/Watchlists/{wl}");
duneEntry = r.Json.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Dune");
Check(duneEntry.GetProperty("available").GetBoolean() && duneEntry.GetProperty("itemId").GetString() == dune2.Id.ToString(), "re-téléchargé : retrouvé par id TMDB");
await tagSync.SyncAsync(CancellationToken.None);
Check(HasTag(dune2), "re-téléchargé : tag reposé");
Check(app.Services.GetRequiredService<Jellyfin.Plugin.Watchlists.Data.WatchlistStore>().Read(root => root.Watchlists.SelectMany(w => w.Items).Where(i => i.TmdbId == "438631").All(i => i.ItemId == dune2.Id)), "entrées mises à jour avec le nouvel ItemId");

// retrait de la liste -> détag
r = await Call(A, "GET", $"/Watchlists/{wl}");
var restrictedEntry = r.Json.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Restricted").GetProperty("entryId").GetString();
Check((await Call(A, "DELETE", $"/Watchlists/{wl}/Items/{restrictedEntry}")).Code == HttpStatusCode.OK, "retrait par entryId");
await tagSync.SyncAsync(CancellationToken.None);
Check(HasTag(restricted), "retiré d'une liste mais encore dans une autre : tag conservé");
var sevAdd = await Call(A, "POST", $"/Watchlists/{wl}/Items", new { itemId = severance.Id });
var sevEntry = sevAdd.Json.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Severance").GetProperty("entryId").GetString();
await tagSync.SyncAsync(CancellationToken.None);
Check(HasTag(severance), "série ajoutée : tag posé");
await Call(A, "DELETE", $"/Watchlists/{wl}/Items/{sevEntry}");
await tagSync.SyncAsync(CancellationToken.None);
Check(!HasTag(severance), "retirée de toutes les listes : tag retiré");
// retrait d'un titre absent (pas d'ItemId) + réordonnancement par entryId
var ids = (await Call(A, "GET", $"/Watchlists/{wl}")).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entryId").GetString()).ToList();
r = await Call(A, "PUT", $"/Watchlists/{wl}/Items/Order", new { itemIds = new[] { ids.Last() } });
Check(r.Json.GetProperty("items")[0].GetProperty("entryId").GetString() == ids.Last(), "réordonnancement par entryId");
Check((await Call(A, "PATCH", $"/Watchlists/{wl}/Items/{absentEntry}", new { note = "au ciné ?" })).Code == HttpStatusCode.OK, "note sur un titre absent");

// renommage du tag
settings.Current.TagName = "a-voir";
await tagSync.SyncAsync(CancellationToken.None);
Check(HasTag(dune2, "a-voir") && !HasTag(dune2), "renommage : nouveau tag posé, ancien retiré");
settings.Current.TagEnabled = false;
var skipped = await tagSync.SyncAsync(CancellationToken.None);
Check(skipped.Skipped is not null && HasTag(dune2, "a-voir"), "tag désactivé : rien n'est modifié");
settings.Current.TagEnabled = true;
settings.Current.TagName = "watchlist";
await tagSync.SyncAsync(CancellationToken.None);

// synchro automatique (anti-rebond) après ajout, sans boucle sur nos propres écritures
var auto = lib.Add(new Movie { Name = "Auto", ProductionYear = 2010, ProviderIds = { ["Tmdb"] = "777" } });
await Call(A, "POST", $"/Watchlists/{wl}/Items", new { itemId = auto.Id });
var t0 = DateTime.UtcNow;
while (!HasTag(auto) && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(40)) await Task.Delay(250);
Check(HasTag(auto), "synchro automatique après ajout (anti-rebond)");
var writes = Repo.Writes;
await Task.Delay(TimeSpan.FromSeconds(20));
Check(Repo.Writes == writes, "pas de boucle : nos écritures de tags ne relancent pas d'écriture");

// Jellyseerr injoignable / non configuré
var savedUrl = settings.Current.SeerrUrl;
settings.Current.SeerrUrl = "http://127.0.0.1:9/";
Check((await Call(A, "GET", "/Watchlists/Search?query=dune")).Code == HttpStatusCode.BadGateway, "Jellyseerr injoignable : 502");
Check((await Call(ADM, "POST", "/Watchlists/Admin/TestSeerr")).Code == HttpStatusCode.BadGateway, "test connexion : échec signalé");
settings.Current.SeerrUrl = "";
Check(!(await Call(A, "GET", "/Watchlists/Seerr")).Json.GetProperty("enabled").GetBoolean(), "Jellyseerr non configuré");
Check((await Call(A, "GET", "/Watchlists/Search?query=dune")).Json.GetArrayLength() == 0, "recherche sans Jellyseerr : vide");
Check((await Call(A, "POST", $"/Watchlists/{wl}/Items", new { tmdbId = "550", mediaType = "movie" })).Code == HttpStatusCode.ServiceUnavailable, "ajout d'un absent sans Jellyseerr : 503");
settings.Current.SeerrUrl = savedUrl;
r = await Call(ADM, "POST", "/Watchlists/Admin/TestSeerr");
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("version").GetString() == "2.7.3", "test connexion Jellyseerr");
settings.Current.SeerrApiKey = "mauvaise";
Check((await Call(ADM, "POST", "/Watchlists/Admin/TestSeerr")).Code == HttpStatusCode.BadGateway, "mauvaise clé d'API signalée");
settings.Current.SeerrApiKey = FakeSeerr.ApiKey;

// suppression
Check((await Call(C, "DELETE", $"/Watchlists/{priv}")).Code == HttpStatusCode.Forbidden || (await Call(C, "DELETE", $"/Watchlists/{priv}")).Code == HttpStatusCode.NotFound, "carol ne supprime pas");
Check((await Call(B, "DELETE", $"/Watchlists/{priv}")).Code == HttpStatusCode.OK, "owner supprime");
Check((await Call(B, "GET", $"/Watchlists/{priv}")).Code == HttpStatusCode.NotFound, "supprimée");

// persistance + nettoyage utilisateurs supprimés
var store2Path = Path.Combine(dataDir, "watchlists", "watchlists.json");
Check(File.Exists(store2Path), "fichier persisté");
var r3 = await Call(B, "POST", "/Watchlists", new { name = "Commune" });
var shared = r3.Json.GetProperty("id").GetString()!;
// ---- matchs : un autre utilisateur a aussi le titre -> liste commune
bool HasMatch(JsonElement j, User u) => j.GetProperty("matches").EnumerateArray().Any(m => m.GetProperty("user").GetProperty("id").GetString() == u.Id.ToString());
JsonElement MatchFor(JsonElement j, User u) => j.GetProperty("matches").EnumerateArray().First(m => m.GetProperty("user").GetProperty("id").GetString() == u.Id.ToString());
var arrival = lib.Add(new Movie { Name = "Arrival", ProductionYear = 2016, ProviderIds = { ["Tmdb"] = "329865" } });
var heat = lib.Add(new Movie { Name = "Heat", ProductionYear = 1995, ProviderIds = { ["Tmdb"] = "949" } });
var bobPerso = (await Call(B, "POST", "/Watchlists", new { name = "Bob perso" })).Json.GetProperty("id").GetString()!;
var carolPerso = (await Call(C, "POST", "/Watchlists", new { name = "Carol perso" })).Json.GetProperty("id").GetString()!;
var carol2 = (await Call(C, "POST", "/Watchlists", new { name = "Carol 2" })).Json.GetProperty("id").GetString()!;
r = await Call(B, "POST", $"/Watchlists/{bobPerso}/Items", new { itemId = arrival.Id });
Check(r.Code == HttpStatusCode.OK && !HasMatch(r.Json, carol) && r.Json.GetProperty("addedEntryId").GetString() != Guid.Empty.ToString(), "match : personne d'autre n'a le titre");
r = await Call(C, "POST", $"/Watchlists/{carolPerso}/Items", new { itemId = arrival.Id });
Check(r.Code == HttpStatusCode.OK && HasMatch(r.Json, bob), "match : bob a le titre dans une liste privée");
var carolEntry = r.Json.GetProperty("addedEntryId").GetString()!;
Check((await Call(C, "POST", "/Watchlists/Shared", new { userId = carol.Id, fromWatchlistId = carolPerso, entryId = carolEntry })).Code == HttpStatusCode.BadRequest, "liste commune avec soi-même refusée");
Check((await Call(C, "POST", "/Watchlists/Shared", new { userId = bob.Id, fromWatchlistId = bobPerso, entryId = carolEntry })).Code == HttpStatusCode.NotFound, "liste commune depuis une liste invisible refusée");
r = await Call(C, "POST", "/Watchlists/Shared", new { userId = bob.Id, fromWatchlistId = carolPerso, entryId = carolEntry });
var duo = r.Json.GetProperty("id").GetString()!;
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("name").GetString() == "carol & bob" && r.Json.GetProperty("visibility").GetString() == "Private"
    && r.Json.GetProperty("items").GetArrayLength() == 1 && r.Json.GetProperty("items")[0].GetProperty("title").GetString() == "Arrival", "liste commune créée avec le titre");
Check(r.Json.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("userId").GetString() == bob.Id.ToString() && m.GetProperty("status").GetString() == "Invited" && m.GetProperty("role").GetString() == "Editor"), "liste commune : bob invité (editor)");
r = await Call(C, "POST", $"/Watchlists/{carol2}/Items", new { itemId = arrival.Id });
Check(r.Code == HttpStatusCode.OK && !HasMatch(r.Json, bob), "match : plus proposé quand une liste commune a déjà le titre");
await Call(B, "POST", $"/Watchlists/{duo}/Invitation/Accept");
await Call(B, "POST", $"/Watchlists/{bobPerso}/Items", new { itemId = heat.Id });
r = await Call(C, "POST", $"/Watchlists/{carolPerso}/Items", new { itemId = heat.Id });
Check(r.Code == HttpStatusCode.OK && HasMatch(r.Json, bob) && MatchFor(r.Json, bob).GetProperty("lists").EnumerateArray().Any(l => l.GetProperty("id").GetString() == duo), "match : liste commune existante proposée");
var heatEntry = r.Json.GetProperty("addedEntryId").GetString()!;
Check((await Call(C, "POST", $"/Watchlists/{duo}/Items", new { fromWatchlistId = bobPerso, fromEntryId = heatEntry })).Code == HttpStatusCode.NotFound, "copie depuis une liste invisible refusée");
r = await Call(C, "POST", $"/Watchlists/{duo}/Items", new { fromWatchlistId = carolPerso, fromEntryId = heatEntry });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString() == "Heat" && i.GetProperty("addedBy").GetProperty("name").GetString() == "carol"), "copie vers la liste commune");
Check((await Call(C, "POST", $"/Watchlists/{duo}/Items", new { fromWatchlistId = carolPerso, fromEntryId = heatEntry })).Code == HttpStatusCode.Conflict, "copie en doublon 409");

// ---- titres vus : vu = par tous les membres acceptés ; griser (Dim) ou retirer automatiquement (Remove)
Check((await Call(C, "PATCH", $"/Watchlists/{duo}", new { seenMode = "Bof" })).Code == HttpStatusCode.BadRequest, "seenMode invalide 400");
Check((await Call(B, "PATCH", $"/Watchlists/{duo}", new { seenMode = "Dim" })).Code != HttpStatusCode.OK, "seenMode réservé au propriétaire");
r = await Call(C, "PATCH", $"/Watchlists/{duo}", new { seenMode = "dim" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("seenMode").GetString() == "Dim", "seenMode Dim enregistré");
bool SeenIn(JsonElement j, string title) => j.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == title).GetProperty("seen").GetBoolean();
userData.Played.Add((carol.Id, arrival.Id));
Check(!SeenIn((await Call(C, "GET", $"/Watchlists/{duo}")).Json, "Arrival"), "vu par carol seule : pas vu par tous");
userData.Played.Add((bob.Id, arrival.Id));
r = await Call(B, "GET", $"/Watchlists/{duo}");
Check(SeenIn(r.Json, "Arrival") && !SeenIn(r.Json, "Heat"), "vu par tous les membres -> seen");
Check(!SeenIn((await Call(C, "GET", $"/Watchlists/{carolPerso}")).Json, "Arrival"), "mode Keep : seen non calculé");

// ---- tirage au sort : listes communes à moi et aux cochés, déjà vus écartés sauf includeSeen
List<string> PickTitles(JsonElement j) => j.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToList();
r = await Call(C, "GET", "/Watchlists/Pick");
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("partners").EnumerateArray().Any(u => u.GetProperty("name").GetString() == "bob"), "tirage : bob proposé comme partenaire");
Check(PickTitles(r.Json).Contains("Heat") && !PickTitles(r.Json).Contains("Arrival"), "tirage seul : mes listes, sans ce que j'ai vu");
r = await Call(C, "GET", $"/Watchlists/Pick?members={N(bob.Id)}");
Check(r.Json.GetProperty("lists").EnumerateArray().Select(l => l.GetProperty("name").GetString()).SequenceEqual(new[] { "carol & bob" }), "tirage avec bob : seulement les listes communes avec bob");
var heatPick = r.Json.GetProperty("candidates").EnumerateArray().FirstOrDefault(c => c.GetProperty("title").GetString() == "Heat");
Check(PickTitles(r.Json).SequenceEqual(new[] { "Heat" }) && heatPick.GetProperty("lists")[0].GetString() == "carol & bob" && heatPick.GetProperty("type").GetString() == "Movie", "tirage avec bob : titres non vus de la liste commune");
r = await Call(C, "GET", $"/Watchlists/Pick?members={N(bob.Id)}&includeSeen=true");
Check(PickTitles(r.Json).Contains("Arrival") && PickTitles(r.Json).Contains("Heat"), "tirage : includeSeen garde les déjà vus");
userData.Played.Add((bob.Id, heat.Id));
Check(PickTitles((await Call(C, "GET", $"/Watchlists/Pick?members={N(bob.Id)}")).Json).Count == 0, "tirage : vu par un des cochés -> écarté");
Check(PickTitles((await Call(C, "GET", "/Watchlists/Pick")).Json).Contains("Heat"), "tirage seul : ce que bob a vu reste proposé");
Check(PickTitles((await Call(C, "GET", $"/Watchlists/Pick?members={N(alice.Id)}")).Json).Count == 0, "tirage : aucun titre avec quelqu'un sans liste commune");

// ---- retrait automatique
r = await Call(C, "PATCH", $"/Watchlists/{duo}", new { seenMode = "Remove" });
Check(r.Code == HttpStatusCode.OK && r.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).SequenceEqual(new[] { "Heat" }), "passage en Remove : titres déjà vus par tous retirés aussitôt");
Check((await Call(C, "POST", $"/Watchlists/{duo}/Items", new { itemId = severance.Id })).Code == HttpStatusCode.OK, "série ajoutée à la liste commune");
var ep2 = lib.Add(new Episode { Name = "S01E02", SeriesId = severance.Id });
userData.Played.Add((carol.Id, heat.Id));
userData.Played.Add((carol.Id, severance.Id));
userData.Played.Add((bob.Id, severance.Id));
userData.Raise(carol.Id, heat, UserDataSaveReason.PlaybackFinished);
userData.Raise(bob.Id, ep2, UserDataSaveReason.TogglePlayed);
var autoRemoved = false;
for (var i = 0; i < 40 && !autoRemoved; i++)
{
    await Task.Delay(250);
    autoRemoved = (await Call(C, "GET", $"/Watchlists/{duo}")).Json.GetProperty("items").GetArrayLength() == 0;
}
Check(autoRemoved, "lecture terminée / épisode marqué vu -> film et série retirés (vus par tous)");
Check((await Call(C, "GET", $"/Watchlists/{carolPerso}")).Json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("title").GetString() == "Heat"), "retrait automatique limité aux listes en mode Remove");

await Call(B, "POST", $"/Watchlists/{shared}/Members", new { userId = carol.Id });
await Call(C, "POST", $"/Watchlists/{shared}/Invitation/Accept");
await app.StopAsync();

users.Delete(bob.Id);
var store = new Jellyfin.Plugin.Watchlists.Data.WatchlistStore(new FakePaths(dataDir), Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.Watchlists.Data.WatchlistStore>.Instance);
var idx2 = new LibraryIndex(lib);
var tags2 = new TagSyncService(store, idx2, lib, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<TagSyncService>.Instance);
var svc = new WatchlistService(store, lib, users, userData, idx2, new SeerrClient(app.Services.GetRequiredService<IHttpClientFactory>(), settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<SeerrClient>.Instance), tags2);
var changes = svc.CleanupDeletedUsers();
var carolLists = svc.GetMine(carol.Id);
Check(changes > 0 && carolLists.Any(l => l.Name == "Commune" && l.MyRole == "Owner"), "owner supprimé : carol promue (rechargement depuis le disque)");

// migration d'un fichier 1.x (entrées sans entryId ni ids TMDB)
var v1Dir = Path.Combine(Path.GetTempPath(), "wl-v1-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(v1Dir, "watchlists"));
File.WriteAllText(Path.Combine(v1Dir, "watchlists", "watchlists.json"),
    "{\"schemaVersion\":1,\"watchlists\":[{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"v1\",\"members\":[{\"userId\":\"" + carol.Id + "\",\"role\":\"Owner\",\"status\":\"Accepted\"}],"
    + "\"items\":[{\"itemId\":\"" + dune2.Id + "\",\"title\":\"Dune\",\"type\":\"Movie\"}]}],\"follows\":{}}");
var v1Store = new Jellyfin.Plugin.Watchlists.Data.WatchlistStore(new FakePaths(v1Dir), Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.Watchlists.Data.WatchlistStore>.Instance);
Check(v1Store.Read(root => root.Watchlists[0].Items[0].EntryId != Guid.Empty), "migration 1.x : entryId attribué");
var v1Sync = new TagSyncService(v1Store, new LibraryIndex(lib), lib, new FakeSettings(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TagSyncService>.Instance);
await v1Sync.SyncAsync(CancellationToken.None);
Check(v1Store.Read(root => root.Watchlists[0].Items[0].TmdbId == "438631"), "migration 1.x : id TMDB récupéré depuis le serveur à la première synchro");
Directory.Delete(v1Dir, true);

await seerrApp.StopAsync();
Console.WriteLine($"{pass} OK, {failCount} échec(s)");
Directory.Delete(dataDir, true);
return failCount == 0 ? 0 : 1;

// ------------------------------------------------------------------ fakes
static class Repo
{
    public static FakeLibrary? Library;
    public static int Writes;
    public static Task Save(BaseItem item) { Interlocked.Increment(ref Writes); Library?.RaiseUpdated(item); return Task.CompletedTask; }
}
class Movie : BaseItem
{
    public HashSet<Guid> HiddenFor { get; } = new();
    public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => !HiddenFor.Contains(user.Id);
    public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken) => Repo.Save(this);
}
class Series : BaseItem
{
    public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken) => Repo.Save(this);
}
class Episode : MediaBrowser.Controller.Entities.TV.Episode { }

class FakeLibrary : ILibraryManager
{
    private readonly Dictionary<Guid, BaseItem> _items = new();
    public event EventHandler<ItemChangeEventArgs>? ItemAdded;
    public event EventHandler<ItemChangeEventArgs>? ItemUpdated;
    public event EventHandler<ItemChangeEventArgs>? ItemRemoved;
    public T Add<T>(T item) where T : BaseItem { item.Id = Guid.NewGuid(); _items[item.Id] = item; ItemAdded?.Invoke(this, new ItemChangeEventArgs { Item = item }); return item; }
    public void Remove(Guid id) { if (_items.Remove(id, out var item)) ItemRemoved?.Invoke(this, new ItemChangeEventArgs { Item = item }); }
    public BaseItem? GetItemById(Guid id) => _items.TryGetValue(id, out var i) ? i : null;
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
        => _items.Values.Where(i => i is Movie or Series).ToList();
    public void RaiseUpdated(BaseItem item) => ItemUpdated?.Invoke(this, new ItemChangeEventArgs { Item = item });
}

class FakeUserData : IUserDataManager
{
    public HashSet<(Guid User, Guid Item)> Played { get; } = new();
    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved;
    public UserItemData GetUserData(User user, BaseItem item) => new() { Played = Played.Contains((user.Id, item.Id)) };
    public void Raise(Guid userId, BaseItem item, UserDataSaveReason reason)
        => UserDataSaved?.Invoke(this, new UserDataSaveEventArgs { UserId = userId, Item = item, SaveReason = reason });
}

class FakeSettings : IWatchlistsSettings
{
    public PluginConfiguration Current { get; } = new();
    public int Saves { get; private set; }
    public void Save() => Saves++;
}

/// <summary>Faux Jellyseerr : sous-ensemble de l'API v1 utilisé par le plugin.</summary>
class FakeSeerr
{
    public const string ApiKey = "seerr-key";
    public Dictionary<string, int> UserMap { get; } = new();
    public List<(string? AsUser, string Body)> Requests { get; } = new();
    public string? LastApiKey { get; private set; }
    private readonly Dictionary<string, int> _status = new();

    private static readonly object[] Catalog =
    {
        new { mediaType = "movie", id = 438631, title = "Dune", releaseDate = "2021-09-15", posterPath = "/dune.jpg", imdbId = "tt1160419" },
        new { mediaType = "movie", id = 693134, title = "Dune : Deuxième partie", releaseDate = "2024-02-27", posterPath = "/dune2.jpg", imdbId = "tt15239678" },
        new { mediaType = "tv", id = 1399, name = "Game of Thrones", firstAirDate = "2011-04-17", posterPath = "/got.jpg", tvdb = 121361 },
        new { mediaType = "person", id = 9, name = "Denis Villeneuve" },
        new { mediaType = "movie", id = 1, title = "Restricted", releaseDate = "2020-01-01", posterPath = "/r.jpg", imdbId = "tt1" }
    };

    private static Dictionary<string, object?> AsDict(object o) => JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!;

    private object? MediaInfo(string type, string id) => _status.TryGetValue(type + ":" + id, out var st) ? new { status = st } : null;

    public WebApplication Build()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        app.Use(async (ctx, next) =>
        {
            LastApiKey = ctx.Request.Headers["X-Api-Key"];
            if (ctx.Request.Path.StartsWithSegments("/api/v1/status")) { await next(); return; }
            if (LastApiKey != ApiKey) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { message = "Unauthorized" }); return; }
            await next();
        });
        app.MapGet("/api/v1/status", () => Results.Json(new { version = "2.7.3" }));
        app.MapGet("/api/v1/settings/main", () => Results.Json(new { applicationTitle = "Jellyseerr" }));
        app.MapGet("/api/v1/search", (string query) => Results.Json(new
        {
            page = 1,
            results = Catalog.Select(AsDict)
                .Where(d => ((d.GetValueOrDefault("title") ?? d.GetValueOrDefault("name"))?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(d => { d["mediaInfo"] = MediaInfo(d["mediaType"]!.ToString()!, d["id"]!.ToString()!); return d; })
        }));
        app.MapGet("/api/v1/movie/{id}", (string id) =>
        {
            var d = Catalog.Select(AsDict).FirstOrDefault(x => x["mediaType"]!.ToString() == "movie" && x["id"]!.ToString() == id);
            if (d is null) return Results.Json(new { message = "Unable to retrieve movie." }, statusCode: 404);
            d["mediaInfo"] = MediaInfo("movie", id);
            d["externalIds"] = new { imdbId = d["imdbId"] };
            return Results.Json(d);
        });
        app.MapGet("/api/v1/tv/{id}", (string id) =>
        {
            var d = Catalog.Select(AsDict).FirstOrDefault(x => x["mediaType"]!.ToString() == "tv" && x["id"]!.ToString() == id);
            if (d is null) return Results.Json(new { message = "Unable to retrieve series." }, statusCode: 404);
            d["mediaInfo"] = MediaInfo("tv", id);
            d["externalIds"] = new { tvdbId = d["tvdb"] };
            return Results.Json(d);
        });
        app.MapGet("/api/v1/user", (int take, int skip) => Results.Json(new
        {
            pageInfo = new { results = UserMap.Count },
            results = UserMap.Skip(skip).Take(take).Select(kv => new { id = kv.Value, jellyfinUserId = kv.Key, displayName = "u" + kv.Value })
        }));
        app.MapPost("/api/v1/request", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var key = doc.RootElement.GetProperty("mediaType").GetString() + ":" + doc.RootElement.GetProperty("mediaId").GetRawText();
            if (_status.ContainsKey(key)) return Results.Json(new { message = "Request for this media already exists." }, statusCode: 409);
            _status[key] = 2;
            Requests.Add((ctx.Request.Headers["X-API-User"].FirstOrDefault(), body));
            return Results.Json(new { id = Requests.Count }, statusCode: 201);
        });
        return app;
    }
}

class FakeUsers : IUserManager
{
    private readonly List<User> _users;
    public FakeUsers(params User[] users) => _users = users.ToList();
    public void Delete(Guid id) => _users.RemoveAll(u => u.Id == id);
    public IEnumerable<User> GetUsers() => _users;
    public User? GetUserById(Guid id) => _users.FirstOrDefault(u => u.Id == id);
}

class FakePaths : IApplicationPaths
{
    public FakePaths(string d) => DataPath = d;
    public string DataPath { get; }
    public string PluginConfigurationsPath => DataPath;
}

class TestAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var uid)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim>
        {
            new("Jellyfin-UserId", uid.ToString()),
            new(ClaimTypes.Role, Request.Headers.ContainsKey("X-Test-Admin") ? "Administrator" : "User"),
            new("Jellyfin-IsApiKey", Request.Headers.ContainsKey("X-Test-ApiKey") ? "True" : "False")
        };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}
