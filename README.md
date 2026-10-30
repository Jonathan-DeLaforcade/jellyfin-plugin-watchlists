# Jellyfin Watchlists (1.12.0.0)

> **À propos de ce projet**
>
> Je suis développeur et administrateur système de métier, mais je n'avais **jamais écrit une ligne de C#**, et encore moins développé sur **.NET**. Ce plugin est donc **entièrement vibecodé** : il a été écrit avec l'aide d'une IA (Claude Code), pour mon **usage personnel** sur mon serveur Jellyfin familial.
>
> Il fonctionne chez moi et il est couvert par des tests, mais il n'a pas été relu par un développeur .NET : attendez-vous à du code qui ne respecte pas toujours les conventions de l'écosystème.
>
> **Les contributions sont les bienvenues** : si vous voulez proposer une amélioration, une correction ou une nouvelle fonctionnalité, ouvrez une *issue* ou une *pull request*.

Plugin Jellyfin **12.x** (.NET 10, ABI 12.0.0.0) : watchlists par utilisateur, privées ou publiques, suivi des listes publiques des autres, et watchlists communes avec rôles et invitations. Les titres peuvent être ajoutés même s'ils ne sont pas sur le serveur (via Jellyseerr), et un tag est maintenu pour Maintainerr.

## Installation

1. Prérequis : **File Transformation** et **Plugin Pages** (disponibles dans le catalogue de plugins Jellyfin). Sans Plugin Pages, il n'y a pas d'entrée de menu ; sans File Transformation, pas de bouton d'en-tête. L'API fonctionne dans tous les cas.
2. Supprimer l'éventuel dossier d'une version précédente, puis copier `dist/Watchlists_1.12.0.0/` (DLL + `meta.json`) dans `<config>/plugins/` du serveur. Sur Unraid / Docker : `/config/plugins/Watchlists_1.12.0.0/`.
3. Redémarrer Jellyfin.
4. Recharger le client web (Ctrl+F5) : une icône **Watchlists** apparaît dans l'en-tête, avant le bouton cast, avec une pastille rouge si des invitations sont en attente. L'entrée reste aussi dans le menu latéral, section *Plugin Settings*.

   Sur la fiche d'un film ou d'une série, un bouton **Ajouter à une watchlist** apparaît à côté de Lecture / Favori (avant « … »). Avec une seule watchlist modifiable, un clic ajoute ou retire le titre. Avec plusieurs, un petit menu s'ouvre sous le bouton (au-dessus faute de place) : une case par liste, groupées en Privées (gris), Communes (vert) et Publiques (bleu), plus « Nouvelle watchlist… ». L'icône devient bleue quand le titre est dans au moins une liste. Le même bouton apparaît au survol des affiches de films et séries (bibliothèque, accueil, recherche...), à côté de Vu / Favori.

   Dans une bibliothèque Films ou Séries, le panneau **Filtres** de Jellyfin (nouveau menu de Jellyfin 12 comme ancien panneau) a une section **Watchlist** (dépliable sous « Filtres » dans le nouveau menu, sous Favoris dans l'ancien) : une case à trois états par watchlist (les miennes et celles que je suis) : 1 clic ✓ « dans la liste », 2 clics ✗ « pas dans la liste », 3 clics retour à vide. La bibliothèque affiche les titres présents sur le serveur qui sont dans au moins une liste ✓ et dans aucune liste ✗ (ex. ✓ liste 1 + ✗ liste 2 = dans la 1 mais pas dans la 2). La case « Dans aucune watchlist » (exclusive) montre les titres absents de toutes ces listes. Le choix est mémorisé par bibliothèque (navigateur). « Réinitialiser les filtres » le retire aussi, et le point du bouton Filtres s'allume quand seul ce filtre est actif. Limite : « Lire tout » et « Lecture aléatoire » ignorent ce filtre quand c'est le seul actif, et lisent toute la bibliothèque.

5. Tableau de bord → Extensions → Watchlists : renseigner l'URL et la clé d'API de Jellyseerr, vérifier le nom du tag, **Enregistrer**.

Données : `<data>/watchlists/watchlists.json`, avec écriture atomique. Elles restent en place en cas de mise à jour ou de suppression du plugin. Un fichier illisible est sauvegardé en `.corrupt-<date>` et le plugin repart à vide.

## Build

| Script | Usage |
|---|---|
| `./build.sh` | Build canonique contre les paquets NuGet `Jellyfin.Controller` / `Jellyfin.Model` 12.0.0. **À privilégier.** |
| `./build-offline.sh` | Build sans NuGet contre `build/stubs` (signatures reproduites depuis les sources v12.0) + tests API + tests UI. C'est ainsi qu'a été produite la DLL fournie. |

Membres Jellyfin utilisés (signatures vérifiées dans les sources v12.0 / v12.2) : `BasePlugin<T>`, `IHasWebPages`, `IApplicationPaths.DataPath`, `IPluginServiceRegistrator`, `ILibraryManager.GetItemById/GetItemList` + événements `ItemAdded/ItemUpdated/ItemRemoved`, `InternalItemsQuery`, `DtoOptions`, `BaseItem.Tags/ProviderIds/UpdateToRepositoryAsync/IsVisible`, `IScheduledTask`, `IUserManager`. Les enums des stubs sont des copies exactes des sources. `./build.sh` (vrais paquets NuGet) reste la référence.

## Règles

- **Visibilité** : `Private` (membres seulement) ou `Public` (tous les utilisateurs du serveur).
- **Rôles** : `Owner` (gère tout), `Editor` (ajoute, retire, réordonne, annote), `Viewer` (lecture). Une liste devient « commune » dès qu'elle a plus d'un membre.
- **Invitation obligatoire** : un invité ne voit la liste dans « Mes listes » et ne peut l'éditer qu'après acceptation. Un admin peut forcer l'ajout direct (`force: true`).
- **Transfert** : `PATCH Members/{id} {role:"Owner"}`. L'ancien owner devient editor. Un owner ne peut pas quitter sa liste sans transférer la propriété ou supprimer la liste.
- **Items** : films et **séries entières** uniquement (saisons et épisodes refusés), sans doublon (même item ou même id TMDB / IMDb).
- **Listes communes proposées** : quand on ajoute un titre qu'un autre utilisateur a déjà dans une de ses watchlists (privée ou publique, hors listes dont on est membre), un encadré le signale sous le bouton cliqué, à la place du menu (« bob a aussi ce titre dans sa watchlist »). Si on a déjà une liste commune modifiable avec lui, on peut y ajouter le titre en un clic. Sinon, on peut en créer une : liste privée « moi & bob », qui contient le titre, dont on est propriétaire, et où bob est invité comme editor. Rien n'est ajouté sans clic. Rien n'est proposé si une liste commune contient déjà le titre (bob invité compris). Fonctionne depuis les fiches, les affiches et la page Watchlists (cette dernière a besoin du script d'en-tête, donc de File Transformation). À noter : ça révèle qu'un titre est dans une liste privée d'un autre utilisateur.
- **Vu / favori** : sur l'affiche de chaque titre présent sur le serveur, une pastille bleue ✓ si on l'a déjà vu (série : tous les épisodes vus) et une pastille rouge avec un cœur si on l'a en favori. Ce sont les données de l'utilisateur qui regarde la liste, pas celles des autres membres.
- **Titres vus** : réglage par liste, dans ses paramètres. Trois choix : *les garder* (par défaut), *les griser et les mettre en bas*, ou *les retirer automatiquement*. Un titre compte comme vu quand **tous les membres** de la liste l'ont vu (moi seul pour une liste perso). Pour une série, il faut avoir vu tous ses épisodes. En mode retrait, les titres déjà vus partent dès l'enregistrement. Les autres partent quelques secondes après leur fin de lecture, ou après avoir été marqués comme vus dans Jellyfin. Un titre retiré perd sa note.
- **Que regarder ?** : bouton sur l'accueil de la page Watchlists. On coche les membres qui regardent (par défaut : moi seul). Le tirage se fait parmi les titres sur le serveur des listes dont tous les cochés sont membres. Moi seul, ce sont toutes mes listes. Avec bob, ce sont nos listes communes. Les titres déjà vus par l'un des cochés sont écartés, sauf avec la case « Inclure les déjà vus ». On peut filtrer par type, par durée (films seulement) et par genre. « Un autre » tire un nouveau titre sans répéter les précédents. À noter : ça révèle quels titres des listes communes les autres membres ont déjà vus.
- **Avant suppression** : quand on supprime de Jellyfin (fiche, menu d'une affiche, sélection multiple) un titre qui est dans une watchlist, une seconde confirmation s'affiche après celle de Jellyfin et liste les watchlists concernées, de **tous** les utilisateurs. Une liste privée qu'on ne peut pas voir apparaît comme « une liste privée de bob », sans son nom (un admin voit le nom). Annuler ne supprime rien. Une sélection multiple ne pose qu'une question. Si la vérification échoue, la suppression a lieu normalement. La vérification se fait côté serveur, sur la même correspondance que les listes (même item, ou même id TMDB / IMDb), et pas sur le tag Maintainerr : le tag n'est mis à jour qu'après un délai, peut être désactivé, et ne dit pas dans quelles listes est le titre. Nécessite le script d'en-tête (File Transformation).
- **Vue admin** : un administrateur a un onglet « Toutes (admin) » qui liste toutes les watchlists du serveur, privées comprises, groupées par propriétaire (membres et invitations en attente affichés). Il peut ouvrir n'importe quelle liste en **lecture seule** ; pour la modifier, il doit en être membre.
- **Pas de fuite** : un titre présent sur le serveur que le lecteur n'a pas le droit de voir (bibliothèque non autorisée, contrôle parental) est omis, et les compteurs sont calculés après filtrage. Un titre absent du serveur est affiché à tous ceux qui voient la liste (métadonnées TMDB uniquement).

## Titres absents du serveur, Jellyseerr et Maintainerr

- **Identité d'un titre** : chaque entrée garde ses ids TMDB / IMDb / TVDB en plus du dernier ItemId connu. Si le fichier est supprimé (Maintainerr), l'entrée **reste dans la watchlist**, affichée « Pas sur le serveur ». S'il est re-téléchargé, Jellyfin lui donne un nouvel ItemId : le plugin le retrouve par id TMDB / IMDb / TVDB. Les entrées créées avant la 1.2 récupèrent leurs ids à la première synchro, tant que le titre est encore sur le serveur.
- **Recherche** : la barre d'ajout cherche sur le serveur et, si Jellyseerr est configuré, sur TMDB via Jellyseerr. Un titre déjà sur le serveur n'apparaît qu'une fois.
- **Statut** d'un titre absent (chargé après l'affichage) : *Pas sur le serveur*, *Demandé* (en attente de validation), *Téléchargement en cours*, *Bloqué dans Jellyseerr*, ou *Jellyseerr pas à jour* (Jellyseerr croit encore le titre disponible : il ne peut pas être redemandé tant que Jellyseerr n'a pas resynchronisé).
- **Demander** : la demande est faite **au nom de l'utilisateur** (header `X-API-User`, compte Jellyseerr lié à son id Jellyfin) : ses droits, quotas et l'auto-validation Jellyseerr s'appliquent. Série : toutes les saisons. Un utilisateur absent de Jellyseerr reçoit un message clair (403).
- **Tag Maintainerr** (`watchlist` par défaut) : présent sur un titre **si et seulement si** il est sur le serveur **et** dans au moins une watchlist (privée, publique ou commune). Synchro 15 s après une modification (au plus 2 min), après les changements de bibliothèque (ajout, re-scan, actualisation des métadonnées qui effacerait le tag), au démarrage, toutes les 6 h et à la demande (tâche planifiée « Synchroniser les tags Watchlists » ou bouton dans la configuration). Le plugin ne supprime jamais de média.
  - Le tag est retiré de tout titre qui n'est dans aucune watchlist : ne pas l'utiliser pour autre chose. Après un renommage, l'ancien tag est retiré.
  - Si l'écriture des NFO est activée, le tag est aussi écrit dans le `.nfo`.
  - Dans Maintainerr, la propriété s'appelle **[list] Labels**.
- **Repassage en privé** : les suivis des non-membres sont purgés.
- **Utilisateur supprimé** (nettoyage au démarrage) : il est retiré des listes. Si c'était l'owner, le plus ancien editor est promu, sinon le plus ancien membre. Une liste sans aucun membre est supprimée.
- Les clés d'API sont refusées (403) : l'API agit toujours au nom de l'utilisateur du token.

## API (`/Watchlists`, authentification Jellyfin standard, JSON camelCase)

```
GET    /Watchlists                         mes listes (membre accepté)
GET    /Watchlists/Public                  listes publiques dont je ne suis pas membre
GET    /Watchlists/Followed                listes publiques suivies
GET    /Watchlists/Invitations             invitations en attente
GET    /Watchlists/Users                   utilisateurs invitables (id, name)
GET    /Watchlists/Containing/{itemId}     listes éditables + contains + entryId + visibility + shared (bouton des fiches)
GET    /Watchlists/Pick?members=a,b&includeSeen=false  {partners, lists, candidates:[{itemId, title, year, type, runtimeMinutes, genres, lists}]} (tirage au sort)
GET    /Watchlists/DeleteCheck?ids=a,b     [{itemId, name, lists:[{name|null, owner, private}]}] : listes de tous les utilisateurs contenant ces items (avant suppression)
GET    /Watchlists/{id}                    (admin : toute liste, lecture seule)
POST   /Watchlists                         {name, description?, visibility?}
PATCH  /Watchlists/{id}                    {name?, description?, visibility?, seenMode?: Keep|Dim|Remove}  owner
DELETE /Watchlists/{id}                                                               owner
POST   /Watchlists/{id}/Items              {itemId, note?} ou {tmdbId, mediaType: movie|tv, note?} ou {fromWatchlistId, fromEntryId}   editor+
                                           réponse : la liste + addedEntryId + matches [{user, lists:[{id,name}]}] (lists vide = pas de liste commune)
POST   /Watchlists/Shared                  {userId, fromWatchlistId, entryId}  liste privée commune avec le titre, userId invité (editor)
PATCH  /Watchlists/{id}/Items/{entryId}    {note}                                     editor+
DELETE /Watchlists/{id}/Items/{entryId}                                               editor+
PUT    /Watchlists/{id}/Items/Order        {itemIds:[entryId...]}  (ids fournis en tête)  editor+
GET    /Watchlists/{id}/Availability       statut Jellyseerr des titres absents [{entryId, status, canRequest}]
GET    /Watchlists/Seerr                   {enabled, isAdmin}
GET    /Watchlists/Search?query=           recherche TMDB via Jellyseerr, annotée (itemId si sur le serveur, status, canRequest)
POST   /Watchlists/Request                 {tmdbId, mediaType}  demande au nom de l'utilisateur
GET    /Watchlists/Admin/All               toutes les listes, avec membres et nb d'invitations (admin)
POST   /Watchlists/Admin/SyncTags          synchro des tags (admin)
POST   /Watchlists/Admin/TestSeerr         test URL + clé Jellyseerr (admin)
POST   /Watchlists/{id}/Members            {userId, role: Editor|Viewer, force?}      owner
PATCH  /Watchlists/{id}/Members/{userId}   {role: Editor|Viewer|Owner}                owner
DELETE /Watchlists/{id}/Members/{userId}   owner : retirer / soi-même : quitter
POST   /Watchlists/{id}/Invitation/Accept | Decline
POST   /Watchlists/{id}/Follow   ·   DELETE /Watchlists/{id}/Follow
GET    /Watchlists/Web/watchlists.html     page chargée par Plugin Pages
GET    /Watchlists/Web/header.js           bouton d'en-tête (anonyme, injecté dans index.html par File Transformation)
```

Les erreurs sont renvoyées sous la forme `{"error": "message"}` avec 400, 403, 404, 409, 502 (Jellyseerr injoignable / mauvaise clé) ou 503 (Jellyseerr non configuré). Les GUID sont acceptés avec ou sans tirets. Sur les routes `Items/{entryId}`, un ancien ItemId est encore accepté.

## Structure

```
src/Jellyfin.Plugin.Watchlists/
  Plugin.cs, PluginServiceRegistrator.cs
  Data/        Models.cs, WatchlistStore.cs (JSON + verrou + écriture atomique)
  Services/    WatchlistService.cs (règles d'accès), Dtos.cs, PluginPagesRegistration.cs (réflexion, retry),
               LibraryIndex.cs (index TMDB/IMDb/TVDB du serveur), SeerrClient.cs (API Jellyseerr v1),
               TagSyncService.cs + TagSyncTask.cs (tag Maintainerr), WatchlistsSettings.cs
  Api/         WatchlistsController.cs, LenientGuidConverter.cs
  Web/         watchlists.html (UI embarquée), config.html (page admin), header.js (bouton d'en-tête), Transformations.cs (callback File Transformation)
build/stubs/   assemblies de signatures Jellyfin 12 (compilation hors-ligne et tests, jamais livrées)
tests/Watchlists.ApiTests/   tests d'intégration HTTP (faux Jellyfin + faux Jellyseerr)
tests/ui/      tests UI de bout en bout (jsdom), `npm i` puis `node ui-test.mjs`
```

## Écarts avec la spec

- **Stockage JSON au lieu de SQLite** : aucun paquet NuGet à embarquer, et c'est suffisant à l'échelle d'un serveur familial. La migration vers SQLite reste possible plus tard sans changer l'API.
- Le libellé de la section du menu (« Plugin Settings ») est imposé par Plugin Pages.

## Contribuer

Toute proposition est la bienvenue : *issue* pour signaler un bug ou suggérer une idée, *pull request* pour proposer du code. Avant une PR, merci de lancer `./build-offline.sh` (build + tests API + tests UI) et de vérifier que tout passe.
