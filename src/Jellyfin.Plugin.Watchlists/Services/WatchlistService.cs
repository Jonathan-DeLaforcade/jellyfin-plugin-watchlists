using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchlists.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>Erreur métier traduite en code HTTP par le contrôleur.</summary>
    public class WatchlistException : Exception
    {
        public WatchlistException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }

    /// <summary>Règles d'accès et opérations sur les watchlists.</summary>
    public class WatchlistService
    {
        public const int MaxNameLength = 100;
        public const int MaxDescriptionLength = 500;
        public const int MaxNoteLength = 300;
        public const int MaxMatches = 5;

        private readonly WatchlistStore _store;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly LibraryIndex _index;
        private readonly SeerrClient _seerr;
        private readonly TagSyncService _tags;
        private readonly IUserDataManager _userData;

        public WatchlistService(WatchlistStore store, ILibraryManager library, IUserManager users, IUserDataManager userData, LibraryIndex index, SeerrClient seerr, TagSyncService tags)
        {
            _store = store;
            _library = library;
            _users = users;
            _userData = userData;
            _index = index;
            _seerr = seerr;
            _tags = tags;
        }

        // ------------------------------------------------------------------ lectures

        public List<WatchlistSummaryDto> GetMine(Guid userId)
        {
            var user = RequireUser(userId);
            return _store.Read(root => root.Watchlists
                .Where(w => IsAccepted(w, userId))
                .OrderByDescending(w => w.UpdatedAt)
                .Select(w => ToSummary(root, w, user))
                .ToList());
        }

        public List<WatchlistSummaryDto> GetPublic(Guid userId)
        {
            var user = RequireUser(userId);
            return _store.Read(root => root.Watchlists
                .Where(w => w.Visibility == Visibility.Public && !IsAccepted(w, userId))
                .OrderByDescending(w => w.UpdatedAt)
                .Select(w => ToSummary(root, w, user))
                .ToList());
        }

        public List<WatchlistSummaryDto> GetFollowed(Guid userId)
        {
            var user = RequireUser(userId);
            return _store.Read(root =>
            {
                var ids = root.Follows.TryGetValue(userId, out var l) ? l : new List<Guid>();
                return root.Watchlists
                    .Where(w => ids.Contains(w.Id) && CanView(w, userId))
                    .OrderByDescending(w => w.UpdatedAt)
                    .Select(w => ToSummary(root, w, user))
                    .ToList();
            });
        }

        public List<WatchlistSummaryDto> GetInvitations(Guid userId)
        {
            var user = RequireUser(userId);
            return _store.Read(root => root.Watchlists
                .Where(w => w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Invited))
                .Select(w => ToSummary(root, w, user))
                .ToList());
        }

        /// <summary>Vue admin : toutes les watchlists du serveur, privées comprises, triées par propriétaire.</summary>
        public List<AdminWatchlistDto> GetAll(Guid userId, bool isAdmin)
        {
            var user = RequireUser(userId);
            if (!isAdmin)
            {
                throw new WatchlistException(403, "Réservé aux administrateurs.");
            }

            return _store.Read(root => root.Watchlists
                .Select(w =>
                {
                    var dto = new AdminWatchlistDto();
                    FillSummary(dto, root, w, user, VisibleItems(w, user));
                    dto.Members = w.Members
                        .Where(m => m.Status == MemberStatus.Accepted)
                        .OrderByDescending(m => m.Role)
                        .Select(m => UserRef(m.UserId))
                        .ToList();
                    dto.InvitedCount = w.Members.Count(m => m.Status == MemberStatus.Invited);
                    return dto;
                })
                .OrderBy(d => d.Owner?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
        }

        /// <summary>Détail d'une liste. Un admin peut lire n'importe quelle liste (lecture seule s'il n'en est pas membre).</summary>
        public WatchlistDetailDto Get(Guid userId, Guid id, bool isAdmin = false)
        {
            var user = RequireUser(userId);
            return _store.Read(root =>
            {
                var w = Find(root, id);
                var invited = w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Invited);
                if (!CanView(w, userId) && !invited && !isAdmin)
                {
                    throw NotFound();
                }

                return ToDetail(root, w, user, isAdmin);
            });
        }

        /// <summary>Listes éditables par l'utilisateur, avec indicateur de présence de l'item (pour le bouton des fiches).</summary>
        public List<ContainingDto> GetContaining(Guid userId, Guid itemId)
        {
            RequireUser(userId);
            var matches = ItemMatcher(itemId, _library.GetItemById(itemId));
            return _store.Read(root => root.Watchlists
                .Where(w => CanEdit(w, userId))
                .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(w => new ContainingDto
                {
                    Id = w.Id,
                    Name = w.Name,
                    Contains = w.Items.Any(matches),
                    Visibility = w.Visibility.ToString(),
                    Shared = w.Members.Count(m => m.Status == MemberStatus.Accepted) > 1,
                    EntryId = w.Items.FirstOrDefault(matches)?.EntryId
                })
                .ToList());
        }

        /// <summary>
        /// Avant de supprimer des items de la bibliothèque : toutes les listes, de tous les utilisateurs, qui les contiennent.
        /// Le nom d'une liste privée n'est donné que si l'appelant peut la voir (ou est admin).
        /// </summary>
        public List<DeleteCheckDto> GetDeleteCheck(Guid userId, IEnumerable<Guid> itemIds, bool isAdmin)
        {
            RequireUser(userId);
            var items = itemIds.Distinct().Take(500)
                .Select(id => _library.GetItemById(id))
                .OfType<BaseItem>()
                .ToList();
            return _store.Read(root => items
                .Select(item =>
                {
                    var matches = ItemMatcher(item.Id, item);
                    return new DeleteCheckDto
                    {
                        ItemId = item.Id,
                        Name = item.Name,
                        Lists = root.Watchlists
                            .Where(w => w.Items.Any(matches))
                            .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                            .Select(w => new DeleteCheckListDto
                            {
                                Name = isAdmin || CanView(w, userId) ? w.Name : null,
                                Owner = UserRef(w.Members.FirstOrDefault(m => m.Role == MemberRole.Owner && m.Status == MemberStatus.Accepted)?.UserId ?? Guid.Empty),
                                Private = w.Visibility == Visibility.Private
                            })
                            .ToList()
                    };
                })
                .Where(d => d.Lists.Count > 0)
                .ToList());
        }

        /// <summary>
        /// Tirage au sort : titres sur le serveur des listes dont je suis membre et dont tous les membres cochés
        /// sont aussi membres (moi seul : toutes mes listes). Sauf includeSeen, un titre déjà vu par moi ou par
        /// un des cochés est écarté. Les filtres type / durée / genre sont appliqués par la page.
        /// </summary>
        public PickDto GetPick(Guid userId, IEnumerable<Guid> memberIds, bool includeSeen)
        {
            var user = RequireUser(userId);
            var members = memberIds.Where(id => id != userId).Distinct().ToList();
            return _store.Read(root =>
            {
                var mine = root.Watchlists.Where(w => IsAccepted(w, userId)).ToList();
                var lists = mine
                    .Where(w => members.All(m => IsAccepted(w, m)))
                    .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var seenBy = includeSeen ? new List<Guid>() : members.Append(userId).ToList();
                var candidates = new Dictionary<Guid, PickItemDto>();
                var skipped = new HashSet<Guid>();
                foreach (var w in lists)
                {
                    foreach (var (_, item) in VisibleItems(w, user))
                    {
                        if (item is null || skipped.Contains(item.Id))
                        {
                            continue;
                        }

                        if (!candidates.TryGetValue(item.Id, out var dto))
                        {
                            if (seenBy.Any(u => HasPlayed(u, item)))
                            {
                                skipped.Add(item.Id);
                                continue;
                            }

                            dto = new PickItemDto
                            {
                                ItemId = item.Id,
                                Title = item.Name,
                                Year = item.ProductionYear,
                                Type = LibraryIndex.ToType(item) ?? string.Empty,
                                RuntimeMinutes = item.RunTimeTicks is long t && t > 0 ? (int)Math.Round(t / (double)TimeSpan.TicksPerMinute) : null,
                                Genres = (item.Genres ?? Array.Empty<string>()).ToList()
                            };
                            candidates[item.Id] = dto;
                        }

                        if (!dto.Lists.Contains(w.Name))
                        {
                            dto.Lists.Add(w.Name);
                        }
                    }
                }

                return new PickDto
                {
                    Partners = mine
                        .SelectMany(w => w.Members)
                        .Where(m => m.Status == MemberStatus.Accepted && m.UserId != userId && _users.GetUserById(m.UserId) is not null)
                        .Select(m => m.UserId)
                        .Distinct()
                        .Select(UserRef)
                        .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList(),
                    Lists = lists.Select(w => new ListRefDto { Id = w.Id, Name = w.Name }).ToList(),
                    Candidates = candidates.Values.ToList()
                };
            });
        }

        /// <summary>
        /// Listes en mode Remove : retire ces titres (films, séries) là où tous les membres acceptés les ont vus.
        /// Appelé par SeenWatcher quand Jellyfin enregistre une lecture. Retourne le nombre d'entrées retirées.
        /// </summary>
        public int RemoveSeenTitles(IEnumerable<Guid> itemIds)
        {
            var items = itemIds.Distinct().Select(id => _library.GetItemById(id)).OfType<BaseItem>().ToList();
            if (items.Count == 0)
            {
                return 0;
            }

            var removed = _store.Write(root =>
            {
                var n = 0;
                foreach (var w in root.Watchlists.Where(w => w.SeenMode == SeenMode.Remove))
                {
                    foreach (var item in items)
                    {
                        var matches = ItemMatcher(item.Id, item);
                        if (w.Items.Any(matches) && SeenByAll(w, item))
                        {
                            n += w.Items.RemoveAll(e => matches(e));
                            Touch(w);
                        }
                    }
                }

                return n;
            });
            if (removed > 0)
            {
                _tags.RequestSync();
            }

            return removed;
        }

        public List<UserRefDto> GetUsers(Guid userId)
        {
            return _users.GetUsers()
                .Where(u => u.Id != userId)
                .OrderBy(u => u.Username, StringComparer.CurrentCultureIgnoreCase)
                .Select(u => new UserRefDto { Id = u.Id, Name = u.Username })
                .ToList();
        }

        // ------------------------------------------------------------------ listes

        public WatchlistDetailDto Create(Guid userId, WatchlistInput input)
        {
            var user = RequireUser(userId);
            var name = ValidateName(input.Name);
            var description = ValidateDescription(input.Description);
            var visibility = ParseVisibility(input.Visibility) ?? Visibility.Private;

            return _store.Write(root =>
            {
                var w = new Watchlist
                {
                    Name = name,
                    Description = description,
                    Visibility = visibility
                };
                w.Members.Add(new WatchlistMember
                {
                    UserId = userId,
                    Role = MemberRole.Owner,
                    Status = MemberStatus.Accepted,
                    InvitedBy = userId,
                    JoinedAt = DateTime.UtcNow
                });
                root.Watchlists.Add(w);
                return ToDetail(root, w, user);
            });
        }

        public WatchlistDetailDto Update(Guid userId, Guid id, WatchlistInput input)
        {
            var user = RequireUser(userId);
            string? name = input.Name is null ? null : ValidateName(input.Name);
            string? description = input.Description is null ? null : ValidateDescription(input.Description);
            var visibility = ParseVisibility(input.Visibility);
            var seenMode = ParseSeenMode(input.SeenMode);
            var removed = 0;

            var dto = _store.Write(root =>
            {
                var w = FindManaged(root, id, userId);
                if (seenMode.HasValue && seenMode.Value != w.SeenMode)
                {
                    w.SeenMode = seenMode.Value;
                    if (w.SeenMode == SeenMode.Remove)
                    {
                        // Passage en retrait automatique : les titres déjà vus partent tout de suite.
                        removed = w.Items.RemoveAll(e => _index.Resolve(e) is { } item && SeenByAll(w, item));
                    }
                }

                if (name is not null)
                {
                    w.Name = name;
                }

                if (description is not null)
                {
                    w.Description = description;
                }

                if (visibility.HasValue && visibility.Value != w.Visibility)
                {
                    w.Visibility = visibility.Value;
                    if (w.Visibility == Visibility.Private)
                    {
                        // Une liste repassée en privé n'est plus suivie par les non-membres.
                        foreach (var kv in root.Follows)
                        {
                            if (!IsAccepted(w, kv.Key))
                            {
                                kv.Value.Remove(w.Id);
                            }
                        }
                    }
                }

                Touch(w);
                return ToDetail(root, w, user);
            });
            if (removed > 0)
            {
                _tags.RequestSync();
            }

            return dto;
        }

        public void Delete(Guid userId, Guid id)
        {
            RequireUser(userId);
            _store.Write(root =>
            {
                var w = FindManaged(root, id, userId);
                root.Watchlists.Remove(w);
                foreach (var list in root.Follows.Values)
                {
                    list.Remove(w.Id);
                }

                return true;
            });
            _tags.RequestSync();
        }

        // ------------------------------------------------------------------ items

        /// <summary>
        /// Ajoute un titre : soit un item du serveur (itemId), soit un titre TMDB (tmdbId + mediaType movie|tv),
        /// présent ou non sur le serveur, soit la copie d'une entrée d'une autre liste (fromWatchlistId + fromEntryId).
        /// Les métadonnées d'un titre absent viennent de Jellyseerr. Le résultat liste les autres utilisateurs qui ont aussi ce titre.
        /// </summary>
        public async Task<AddItemResultDto> AddItemAsync(Guid userId, Guid id, ItemInput input, CancellationToken ct)
        {
            var user = RequireUser(userId);
            var note = ValidateNote(input.Note);
            WatchlistItem entry;

            if (input.FromWatchlistId is { } fromId && fromId != Guid.Empty)
            {
                entry = CopyEntry(userId, fromId, input.FromEntryId ?? Guid.Empty);
            }
            else if (input.ItemId is { } itemId && itemId != Guid.Empty)
            {
                var item = _library.GetItemById(itemId);
                if (item is null || !item.IsVisible(user))
                {
                    throw new WatchlistException(404, "Élément introuvable.");
                }

                entry = FromLibrary(item) ?? throw new WatchlistException(400, "Seuls les films et les séries (série entière) peuvent être ajoutés.");
            }
            else if (!string.IsNullOrWhiteSpace(input.TmdbId))
            {
                var mediaType = (input.MediaType ?? string.Empty).Trim().ToLowerInvariant();
                if (mediaType != "movie" && mediaType != "tv")
                {
                    throw new WatchlistException(400, "mediaType doit valoir movie ou tv.");
                }

                var tmdbId = input.TmdbId.Trim();
                if (!tmdbId.All(char.IsAsciiDigit) || tmdbId.Length > 12)
                {
                    throw new WatchlistException(400, "tmdbId invalide.");
                }

                var type = SeerrClient.ToItemType(mediaType);
                var onServer = _index.FindByProvider(type, tmdbId, null, null);
                if (onServer is not null && onServer.IsVisible(user))
                {
                    entry = FromLibrary(onServer)!;
                }
                else
                {
                    SeerrMedia media;
                    try
                    {
                        media = await _seerr.GetMediaAsync(mediaType, tmdbId, ct).ConfigureAwait(false);
                    }
                    catch (SeerrException ex)
                    {
                        throw new WatchlistException(ex.StatusCode, ex.Message);
                    }

                    entry = new WatchlistItem
                    {
                        Type = type,
                        TmdbId = tmdbId,
                        ImdbId = media.ImdbId,
                        TvdbId = media.TvdbId,
                        Title = media.Title,
                        Year = media.Year,
                        PosterPath = media.PosterPath
                    };
                }
            }
            else
            {
                throw new WatchlistException(400, "itemId ou tmdbId manquant.");
            }

            entry.AddedBy = userId;
            entry.Note = note ?? string.Empty;

            var dto = _store.Write(root =>
            {
                var w = FindEditable(root, id, userId);
                if (w.Items.Any(i => SameTitle(i, entry)))
                {
                    throw new WatchlistException(409, "Déjà présent dans cette watchlist.");
                }

                w.Items.Add(entry);
                Touch(w);
                var result = new AddItemResultDto { AddedEntryId = entry.EntryId, Matches = FindMatches(root, userId, entry) };
                FillDetail(result, root, w, user);
                return result;
            });
            _tags.RequestSync();
            return dto;
        }

        /// <summary>
        /// Crée une liste privée commune avec un autre utilisateur (invité comme editor), contenant une entrée copiée
        /// d'une liste visible par l'appelant.
        /// </summary>
        public WatchlistDetailDto CreateShared(Guid userId, SharedInput input)
        {
            var user = RequireUser(userId);
            if (input.UserId is null || input.UserId == Guid.Empty || input.FromWatchlistId is null)
            {
                throw new WatchlistException(400, "userId et fromWatchlistId requis.");
            }

            var other = _users.GetUserById(input.UserId.Value) ?? throw new WatchlistException(404, "Utilisateur introuvable.");
            if (other.Id == userId)
            {
                throw new WatchlistException(400, "Choisissez un autre utilisateur.");
            }

            var entry = CopyEntry(userId, input.FromWatchlistId.Value, input.EntryId ?? Guid.Empty);
            var name = user.Username + " & " + other.Username;
            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength];
            }

            var dto = _store.Write(root =>
            {
                var w = new Watchlist { Name = name, Visibility = Visibility.Private };
                w.Members.Add(new WatchlistMember
                {
                    UserId = userId,
                    Role = MemberRole.Owner,
                    Status = MemberStatus.Accepted,
                    InvitedBy = userId,
                    JoinedAt = DateTime.UtcNow
                });
                w.Members.Add(new WatchlistMember
                {
                    UserId = other.Id,
                    Role = MemberRole.Editor,
                    Status = MemberStatus.Invited,
                    InvitedBy = userId
                });
                w.Items.Add(entry);
                root.Watchlists.Add(w);
                return ToDetail(root, w, user);
            });
            _tags.RequestSync();
            return dto;
        }

        public WatchlistDetailDto UpdateItem(Guid userId, Guid id, Guid entryId, ItemInput input)
        {
            var user = RequireUser(userId);
            var note = ValidateNote(input.Note) ?? string.Empty;
            return _store.Write(root =>
            {
                var w = FindEditable(root, id, userId);
                var it = FindEntry(w, entryId) ?? throw new WatchlistException(404, "Élément absent de la watchlist.");
                it.Note = note;
                Touch(w);
                return ToDetail(root, w, user);
            });
        }

        public WatchlistDetailDto RemoveItem(Guid userId, Guid id, Guid entryId)
        {
            var user = RequireUser(userId);
            var dto = _store.Write(root =>
            {
                var w = FindEditable(root, id, userId);
                var it = FindEntry(w, entryId) ?? throw new WatchlistException(404, "Élément absent de la watchlist.");
                w.Items.Remove(it);
                Touch(w);
                return ToDetail(root, w, user);
            });
            _tags.RequestSync();
            return dto;
        }

        /// <summary>Réordonne : les ids fournis (entryId ou itemId) passent en tête dans cet ordre, les autres suivent.</summary>
        public WatchlistDetailDto Reorder(Guid userId, Guid id, OrderInput input)
        {
            var user = RequireUser(userId);
            var order = (input.ItemIds ?? new List<Guid>()).Distinct().ToList();
            return _store.Write(root =>
            {
                var w = FindEditable(root, id, userId);
                var reordered = new List<WatchlistItem>();
                foreach (var key in order)
                {
                    var e = FindEntry(w, key);
                    if (e is not null && !reordered.Contains(e))
                    {
                        reordered.Add(e);
                    }
                }

                reordered.AddRange(w.Items.Where(i => !reordered.Contains(i)));
                w.Items = reordered;
                Touch(w);
                return ToDetail(root, w, user);
            });
        }

        // ------------------------------------------------------------------ Jellyseerr

        public SeerrStateDto GetSeerrState(bool isAdmin)
            => new() { Enabled = _seerr.IsConfigured, IsAdmin = isAdmin };

        /// <summary>Recherche TMDB via Jellyseerr, annotée de la présence sur le serveur.</summary>
        public async Task<List<SearchResultDto>> SearchAsync(Guid userId, string? query, CancellationToken ct)
        {
            var user = RequireUser(userId);
            var q = (query ?? string.Empty).Trim();
            if (q.Length < 2 || !_seerr.IsConfigured)
            {
                return new List<SearchResultDto>();
            }

            List<SeerrMedia> results;
            try
            {
                results = await _seerr.SearchAsync(q.Length > 100 ? q[..100] : q, ct).ConfigureAwait(false);
            }
            catch (SeerrException ex)
            {
                throw new WatchlistException(ex.StatusCode, ex.Message);
            }

            return results.Select(m =>
            {
                var onServer = _index.FindByProvider(SeerrClient.ToItemType(m.MediaType), m.TmdbId, null, null);
                var visible = onServer is not null && onServer.IsVisible(user);
                return new SearchResultDto
                {
                    TmdbId = m.TmdbId,
                    MediaType = m.MediaType,
                    Type = SeerrClient.ToItemType(m.MediaType),
                    Title = m.Title,
                    Year = m.Year,
                    PosterPath = m.PosterPath,
                    ItemId = visible ? onServer!.Id : null,
                    Status = visible ? AvailabilityStatus.Available : FromSeerr(m.Status),
                    CanRequest = !visible && CanRequest(m.Status)
                };
            }).ToList();
        }

        /// <summary>
        /// Statut détaillé des titres de la liste qui ne sont pas sur le serveur (demandé, en cours...).
        /// Séparé du détail pour ne pas ralentir l'affichage : appelé par l'UI après le rendu.
        /// </summary>
        public async Task<List<AvailabilityDto>> GetAvailabilityAsync(Guid userId, Guid id, CancellationToken ct, bool isAdmin = false)
        {
            var user = RequireUser(userId);
            var absent = _store.Read(root =>
            {
                var w = Find(root, id);
                var invited = w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Invited);
                if (!CanView(w, userId) && !invited && !isAdmin)
                {
                    throw NotFound();
                }

                return VisibleItems(w, user)
                    .Where(x => x.Item is null)
                    .Select(x => (x.Entry.EntryId, x.Entry.Type, x.Entry.TmdbId))
                    .ToList();
            });

            var result = new List<AvailabilityDto>();
            if (!_seerr.IsConfigured)
            {
                return result;
            }

            using var gate = new SemaphoreSlim(4);
            var tasks = absent.Select(async a =>
            {
                var dto = new AvailabilityDto { EntryId = a.EntryId, Status = AvailabilityStatus.NotRequested };
                if (string.IsNullOrEmpty(a.TmdbId))
                {
                    dto.Status = AvailabilityStatus.Unknown;
                    return dto;
                }

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var media = await _seerr.GetMediaAsync(SeerrClient.ToMediaType(a.Type), a.TmdbId, ct).ConfigureAwait(false);
                    dto.Status = FromSeerr(media.Status);
                    dto.CanRequest = CanRequest(media.Status);
                }
                catch (SeerrException)
                {
                    dto.Status = AvailabilityStatus.Unknown;
                }
                finally
                {
                    gate.Release();
                }

                return dto;
            }).ToList();
            result.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            return result;
        }

        /// <summary>Demande dans Jellyseerr, au nom du compte Jellyseerr lié à l'utilisateur Jellyfin.</summary>
        public async Task<AvailabilityDto> RequestAsync(Guid userId, RequestInput input, CancellationToken ct)
        {
            var user = RequireUser(userId);
            var mediaType = (input.MediaType ?? string.Empty).Trim().ToLowerInvariant();
            var tmdbId = (input.TmdbId ?? string.Empty).Trim();
            if ((mediaType != "movie" && mediaType != "tv") || tmdbId.Length == 0 || !tmdbId.All(char.IsAsciiDigit))
            {
                throw new WatchlistException(400, "mediaType (movie|tv) et tmdbId requis.");
            }

            var type = SeerrClient.ToItemType(mediaType);
            var onServer = _index.FindByProvider(type, tmdbId, null, null);
            if (onServer is not null && onServer.IsVisible(user))
            {
                throw new WatchlistException(409, "Ce titre est déjà disponible sur le serveur.");
            }

            try
            {
                var seerrUser = await _seerr.FindUserAsync(userId, ct).ConfigureAwait(false)
                    ?? throw new WatchlistException(403, "Votre compte n'existe pas dans Jellyseerr : connectez-vous une fois à Jellyseerr avec votre compte Jellyfin, ou demandez à l'administrateur de l'importer.");
                var media = await _seerr.GetMediaAsync(mediaType, tmdbId, ct, bypassCache: true).ConfigureAwait(false);
                if (!CanRequest(media.Status))
                {
                    throw new WatchlistException(409, "Ce titre est déjà demandé ou disponible dans Jellyseerr.");
                }

                await _seerr.RequestAsync(seerrUser, mediaType, tmdbId, media.TvdbId, ct).ConfigureAwait(false);
                var after = await _seerr.GetMediaAsync(mediaType, tmdbId, ct, bypassCache: true).ConfigureAwait(false);
                return new AvailabilityDto
                {
                    Status = after.Status is null ? AvailabilityStatus.Pending : FromSeerr(after.Status),
                    CanRequest = false
                };
            }
            catch (SeerrException ex)
            {
                throw new WatchlistException(ex.StatusCode, ex.Message);
            }
        }

        private static AvailabilityStatus FromSeerr(SeerrStatus? s) => s switch
        {
            SeerrStatus.Pending => AvailabilityStatus.Pending,
            SeerrStatus.Processing => AvailabilityStatus.Processing,
            SeerrStatus.PartiallyAvailable or SeerrStatus.Available => AvailabilityStatus.SeerrAvailable,
            SeerrStatus.Blocklisted => AvailabilityStatus.Blocklisted,
            _ => AvailabilityStatus.NotRequested
        };

        /// <summary>Demandable si Jellyseerr ne le connaît pas, ou l'a marqué inconnu / supprimé.</summary>
        private static bool CanRequest(SeerrStatus? s) => s is null or SeerrStatus.Unknown or SeerrStatus.Deleted;

        private static WatchlistItem? FromLibrary(BaseItem item)
        {
            var type = LibraryIndex.ToType(item);
            if (type is null)
            {
                return null;
            }

            var entry = new WatchlistItem { Type = type };
            LibraryIndex.Absorb(entry, item);
            return entry;
        }

        /// <summary>Copie (nouvelle entrée, sans note) d'une entrée d'une liste visible par l'utilisateur.</summary>
        private WatchlistItem CopyEntry(Guid userId, Guid fromId, Guid entryId)
        {
            return _store.Read(root =>
            {
                var source = Find(root, fromId);
                if (!CanView(source, userId))
                {
                    throw NotFound();
                }

                var e = FindEntry(source, entryId) ?? throw new WatchlistException(404, "Élément absent de la watchlist.");
                return new WatchlistItem
                {
                    ItemId = e.ItemId,
                    TmdbId = e.TmdbId,
                    ImdbId = e.ImdbId,
                    TvdbId = e.TvdbId,
                    Title = e.Title,
                    Year = e.Year,
                    PosterPath = e.PosterPath,
                    Type = e.Type,
                    AddedBy = userId
                };
            });
        }

        /// <summary>
        /// Autres utilisateurs qui ont ce titre dans une liste dont l'appelant n'est pas membre (privée comprise),
        /// sauf ceux avec qui il partage déjà une liste qui le contient. Pour chacun : les listes communes
        /// modifiables par l'appelant (l'autre y est membre ou invité) où l'ajouter, ou aucune (proposer d'en créer une).
        /// </summary>
        private List<MatchDto> FindMatches(StoreRoot root, Guid userId, WatchlistItem entry)
        {
            var others = root.Watchlists
                .Where(w => !IsAccepted(w, userId) && w.Items.Any(i => SameTitle(i, entry)))
                .SelectMany(w => w.Members.Where(m => m.Status == MemberStatus.Accepted && m.UserId != userId).Select(m => m.UserId))
                .Distinct()
                .Where(u => _users.GetUserById(u) is not null);

            var result = new List<MatchDto>();
            foreach (var other in others)
            {
                var common = root.Watchlists.Where(w => IsAccepted(w, userId) && w.Members.Any(m => m.UserId == other)).ToList();
                if (common.Any(w => w.Items.Any(i => SameTitle(i, entry))))
                {
                    continue;
                }

                result.Add(new MatchDto
                {
                    User = UserRef(other),
                    Lists = common.Where(w => CanEdit(w, userId))
                        .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(w => new ListRefDto { Id = w.Id, Name = w.Name })
                        .ToList()
                });
                if (result.Count == MaxMatches)
                {
                    break;
                }
            }

            return result;
        }

        private bool HasPlayed(Guid userId, BaseItem item)
        {
            var user = _users.GetUserById(userId);
            return user is not null && item.IsPlayed(user, _userData.GetUserData(user, item));
        }

        /// <summary>Vu par tous les membres acceptés (série : tous les épisodes, calcul de Jellyfin).</summary>
        private bool SeenByAll(Watchlist w, BaseItem item)
        {
            var members = w.Members.Where(m => m.Status == MemberStatus.Accepted).Select(m => m.UserId).ToList();
            return members.Count > 0 && members.All(u => HasPlayed(u, item));
        }

        /// <summary>Entrée correspondant à un item de la bibliothèque : même item, ou même type et même id TMDB / IMDb.</summary>
        private static Func<WatchlistItem, bool> ItemMatcher(Guid itemId, BaseItem? item)
        {
            var type = item is null ? null : LibraryIndex.ToType(item);
            var tmdb = item is null ? null : LibraryIndex.ProviderId(item, "Tmdb");
            var imdb = item is null ? null : LibraryIndex.ProviderId(item, "Imdb");
            return e => e.ItemId == itemId
                || (type is not null && e.Type == type
                    && ((tmdb is not null && e.TmdbId == tmdb) || (imdb is not null && e.ImdbId == imdb)));
        }

        /// <summary>Même titre : même item, ou même type et même id TMDB / IMDb.</summary>
        private static bool SameTitle(WatchlistItem a, WatchlistItem b)
        {
            if (a.ItemId != Guid.Empty && a.ItemId == b.ItemId)
            {
                return true;
            }

            if (a.Type != b.Type)
            {
                return false;
            }

            return (a.TmdbId is not null && a.TmdbId == b.TmdbId) || (a.ImdbId is not null && a.ImdbId == b.ImdbId);
        }

        /// <summary>Entrée par EntryId, ou par ItemId (compatibilité avec les anciennes URLs).</summary>
        private static WatchlistItem? FindEntry(Watchlist w, Guid key)
            => w.Items.FirstOrDefault(i => i.EntryId == key) ?? (key == Guid.Empty ? null : w.Items.FirstOrDefault(i => i.ItemId == key));

        // ------------------------------------------------------------------ membres

        public WatchlistDetailDto Invite(Guid userId, bool isAdmin, Guid id, MemberInput input)
        {
            var user = RequireUser(userId);
            if (input.UserId is null || input.UserId == Guid.Empty)
            {
                throw new WatchlistException(400, "userId manquant.");
            }

            var target = _users.GetUserById(input.UserId.Value) ?? throw new WatchlistException(404, "Utilisateur introuvable.");
            var role = ParseRole(input.Role) ?? MemberRole.Editor;
            if (role == MemberRole.Owner)
            {
                throw new WatchlistException(400, "Le rôle owner se transfère, il ne s'attribue pas à l'invitation.");
            }

            var force = input.Force == true;
            if (force && !isAdmin)
            {
                throw new WatchlistException(403, "Seul un administrateur peut ajouter un membre sans invitation.");
            }

            return _store.Write(root =>
            {
                var w = FindManaged(root, id, userId);
                if (w.Members.Any(m => m.UserId == target.Id))
                {
                    throw new WatchlistException(409, "Cet utilisateur est déjà membre ou invité.");
                }

                w.Members.Add(new WatchlistMember
                {
                    UserId = target.Id,
                    Role = role,
                    Status = force ? MemberStatus.Accepted : MemberStatus.Invited,
                    InvitedBy = userId,
                    JoinedAt = force ? DateTime.UtcNow : null
                });
                Touch(w);
                return ToDetail(root, w, user);
            });
        }

        /// <summary>Change le rôle d'un membre. Role=Owner transfère la propriété (l'ancien owner devient editor).</summary>
        public WatchlistDetailDto UpdateMember(Guid userId, Guid id, Guid memberId, MemberInput input)
        {
            var user = RequireUser(userId);
            var role = ParseRole(input.Role) ?? throw new WatchlistException(400, "role manquant ou invalide.");
            return _store.Write(root =>
            {
                var w = FindManaged(root, id, userId);
                if (memberId == userId)
                {
                    throw new WatchlistException(400, "Transférez la propriété à un autre membre pour changer votre rôle.");
                }

                var m = w.Members.FirstOrDefault(x => x.UserId == memberId) ?? throw new WatchlistException(404, "Membre introuvable.");
                if (role == MemberRole.Owner)
                {
                    if (m.Status != MemberStatus.Accepted)
                    {
                        throw new WatchlistException(400, "Le nouveau propriétaire doit avoir accepté l'invitation.");
                    }

                    w.Members.First(x => x.UserId == userId).Role = MemberRole.Editor;
                }

                m.Role = role;
                Touch(w);
                return ToDetail(root, w, user);
            });
        }

        /// <summary>Retire un membre (owner), quitte la liste ou refuse une invitation (soi-même).</summary>
        public void RemoveMember(Guid userId, Guid id, Guid memberId)
        {
            RequireUser(userId);
            _store.Write(root =>
            {
                var w = Find(root, id);
                var self = w.Members.FirstOrDefault(x => x.UserId == userId);
                if (self is null)
                {
                    throw NotFound();
                }

                var isOwner = self.Role == MemberRole.Owner && self.Status == MemberStatus.Accepted;
                if (memberId != userId && !isOwner)
                {
                    throw new WatchlistException(403, "Seul le propriétaire peut retirer un membre.");
                }

                if (memberId == userId && isOwner)
                {
                    throw new WatchlistException(400, "Le propriétaire doit transférer la propriété (ou supprimer la liste) avant de la quitter.");
                }

                if (w.Members.RemoveAll(x => x.UserId == memberId) == 0)
                {
                    throw new WatchlistException(404, "Membre introuvable.");
                }

                Touch(w);
                return true;
            });
        }

        public WatchlistDetailDto AcceptInvitation(Guid userId, Guid id)
        {
            var user = RequireUser(userId);
            return _store.Write(root =>
            {
                var w = Find(root, id);
                var m = w.Members.FirstOrDefault(x => x.UserId == userId && x.Status == MemberStatus.Invited)
                    ?? throw new WatchlistException(404, "Aucune invitation en attente pour cette liste.");
                m.Status = MemberStatus.Accepted;
                m.JoinedAt = DateTime.UtcNow;
                if (root.Follows.TryGetValue(userId, out var follows))
                {
                    follows.Remove(w.Id);
                }

                Touch(w);
                return ToDetail(root, w, user);
            });
        }

        public void DeclineInvitation(Guid userId, Guid id)
        {
            RequireUser(userId);
            _store.Write(root =>
            {
                var w = Find(root, id);
                if (w.Members.RemoveAll(x => x.UserId == userId && x.Status == MemberStatus.Invited) == 0)
                {
                    throw new WatchlistException(404, "Aucune invitation en attente pour cette liste.");
                }

                return true;
            });
        }

        // ------------------------------------------------------------------ suivi

        public void Follow(Guid userId, Guid id, bool follow)
        {
            RequireUser(userId);
            _store.Write(root =>
            {
                var w = Find(root, id);
                if (!root.Follows.TryGetValue(userId, out var list))
                {
                    list = new List<Guid>();
                    root.Follows[userId] = list;
                }

                if (!follow)
                {
                    list.Remove(w.Id);
                    return true;
                }

                if (w.Visibility != Visibility.Public)
                {
                    throw NotFound();
                }

                if (IsAccepted(w, userId))
                {
                    throw new WatchlistException(400, "Vous êtes déjà membre de cette liste.");
                }

                if (!list.Contains(w.Id))
                {
                    list.Add(w.Id);
                }

                return true;
            });
        }

        // ------------------------------------------------------------------ maintenance

        /// <summary>
        /// Nettoie les références aux utilisateurs supprimés : retrait des membres et des suivis,
        /// promotion d'un nouveau propriétaire (plus ancien editor accepté, sinon plus ancien membre accepté),
        /// suppression des listes sans aucun membre accepté. Appelé au démarrage.
        /// </summary>
        public int CleanupDeletedUsers()
        {
            var total = _store.Write(root =>
            {
                var changes = 0;
                bool Exists(Guid id) => _users.GetUserById(id) is not null;

                foreach (var w in root.Watchlists.ToList())
                {
                    changes += w.Members.RemoveAll(m => !Exists(m.UserId));
                    var accepted = w.Members.Where(m => m.Status == MemberStatus.Accepted).OrderBy(m => m.JoinedAt ?? m.InvitedAt).ToList();
                    if (accepted.Count == 0)
                    {
                        root.Watchlists.Remove(w);
                        changes++;
                        continue;
                    }

                    if (!accepted.Any(m => m.Role == MemberRole.Owner))
                    {
                        (accepted.FirstOrDefault(m => m.Role == MemberRole.Editor) ?? accepted[0]).Role = MemberRole.Owner;
                        changes++;
                    }
                }

                foreach (var key in root.Follows.Keys.Where(k => !Exists(k)).ToList())
                {
                    root.Follows.Remove(key);
                    changes++;
                }

                var ids = root.Watchlists.Select(w => w.Id).ToHashSet();
                foreach (var list in root.Follows.Values)
                {
                    changes += list.RemoveAll(id => !ids.Contains(id));
                }

                return changes;
            });
            if (total > 0)
            {
                _tags.RequestSync();
            }

            return total;
        }

        // ------------------------------------------------------------------ helpers accès

        private static bool IsAccepted(Watchlist w, Guid userId)
            => w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Accepted);

        private static bool CanView(Watchlist w, Guid userId)
            => w.Visibility == Visibility.Public || IsAccepted(w, userId);

        private static bool CanEdit(Watchlist w, Guid userId)
            => w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Accepted && m.Role >= MemberRole.Editor);

        private static bool CanManage(Watchlist w, Guid userId)
            => w.Members.Any(m => m.UserId == userId && m.Status == MemberStatus.Accepted && m.Role == MemberRole.Owner);

        private static WatchlistException NotFound() => new(404, "Watchlist introuvable.");

        private static Watchlist Find(StoreRoot root, Guid id)
            => root.Watchlists.FirstOrDefault(w => w.Id == id) ?? throw NotFound();

        private static Watchlist FindEditable(StoreRoot root, Guid id, Guid userId)
        {
            var w = Find(root, id);
            if (!CanView(w, userId))
            {
                throw NotFound();
            }

            if (!CanEdit(w, userId))
            {
                throw new WatchlistException(403, "Vous n'avez pas le droit de modifier cette watchlist.");
            }

            return w;
        }

        private static Watchlist FindManaged(StoreRoot root, Guid id, Guid userId)
        {
            var w = Find(root, id);
            if (!CanView(w, userId))
            {
                throw NotFound();
            }

            if (!CanManage(w, userId))
            {
                throw new WatchlistException(403, "Réservé au propriétaire de la watchlist.");
            }

            return w;
        }

        private static void Touch(Watchlist w) => w.UpdatedAt = DateTime.UtcNow;

        private User RequireUser(Guid userId)
            => _users.GetUserById(userId) ?? throw new WatchlistException(401, "Utilisateur inconnu.");

        // ------------------------------------------------------------------ validation

        private static string ValidateName(string? name)
        {
            var n = (name ?? string.Empty).Trim();
            if (n.Length == 0)
            {
                throw new WatchlistException(400, "Le nom est obligatoire.");
            }

            if (n.Length > MaxNameLength)
            {
                throw new WatchlistException(400, $"Nom trop long ({MaxNameLength} caractères max).");
            }

            return n;
        }

        private static string ValidateDescription(string? d)
        {
            var s = (d ?? string.Empty).Trim();
            if (s.Length > MaxDescriptionLength)
            {
                throw new WatchlistException(400, $"Description trop longue ({MaxDescriptionLength} caractères max).");
            }

            return s;
        }

        private static string? ValidateNote(string? note)
        {
            if (note is null)
            {
                return null;
            }

            var s = note.Trim();
            if (s.Length > MaxNoteLength)
            {
                throw new WatchlistException(400, $"Note trop longue ({MaxNoteLength} caractères max).");
            }

            return s;
        }

        private static Visibility? ParseVisibility(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return null;
            }

            return Enum.TryParse<Visibility>(v, true, out var r) && Enum.IsDefined(r)
                ? r
                : throw new WatchlistException(400, "visibility doit valoir Private ou Public.");
        }

        private static SeenMode? ParseSeenMode(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return null;
            }

            return Enum.TryParse<SeenMode>(v, true, out var r) && Enum.IsDefined(r)
                ? r
                : throw new WatchlistException(400, "seenMode doit valoir Keep, Dim ou Remove.");
        }

        private static MemberRole? ParseRole(string? v)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return null;
            }

            return Enum.TryParse<MemberRole>(v, true, out var r) && Enum.IsDefined(r)
                ? r
                : throw new WatchlistException(400, "role doit valoir Viewer, Editor ou Owner.");
        }

        // ------------------------------------------------------------------ mapping

        private UserRefDto UserRef(Guid id)
        {
            var u = _users.GetUserById(id);
            return new UserRefDto { Id = id, Name = u?.Username ?? "Utilisateur supprimé" };
        }

        /// <summary>
        /// Titres visibles par le lecteur. Un titre présent sur le serveur mais interdit au lecteur
        /// (bibliothèque non autorisée, contrôle parental) est omis. Un titre absent du serveur est affiché
        /// avec ses métadonnées TMDB (rien n'est révélé sur le contenu du serveur).
        /// </summary>
        private List<(WatchlistItem Entry, BaseItem? Item)> VisibleItems(Watchlist w, User viewer)
        {
            var result = new List<(WatchlistItem, BaseItem?)>();
            foreach (var entry in w.Items)
            {
                var item = _index.Resolve(entry);
                if (item is null)
                {
                    result.Add((entry, null));
                }
                else if (item.IsVisible(viewer))
                {
                    result.Add((entry, item));
                }
            }

            return result;
        }

        private WatchlistSummaryDto ToSummary(StoreRoot root, Watchlist w, User viewer)
        {
            var dto = new WatchlistSummaryDto();
            FillSummary(dto, root, w, viewer, VisibleItems(w, viewer));
            return dto;
        }

        private void FillSummary(WatchlistSummaryDto dto, StoreRoot root, Watchlist w, User viewer, List<(WatchlistItem Entry, BaseItem? Item)> items)
        {
            var me = w.Members.FirstOrDefault(m => m.UserId == viewer.Id);
            var owner = w.Members.FirstOrDefault(m => m.Role == MemberRole.Owner);
            dto.Id = w.Id;
            dto.Name = w.Name;
            dto.Description = w.Description;
            dto.Visibility = w.Visibility.ToString();
            dto.SeenMode = w.SeenMode.ToString();
            dto.MyRole = me?.Role.ToString();
            dto.MyStatus = me?.Status.ToString();
            dto.Owner = owner is null ? null : UserRef(owner.UserId);
            dto.MemberCount = w.Members.Count(m => m.Status == MemberStatus.Accepted);
            dto.ItemCount = items.Count;
            dto.Posters = items
                .Where(i => i.Item is not null || !string.IsNullOrEmpty(i.Entry.PosterPath))
                .Take(4)
                .Select(i => new PosterDto { ItemId = i.Item?.Id, PosterPath = i.Item is null ? i.Entry.PosterPath : null })
                .ToList();
            dto.PosterItemIds = items.Where(i => i.Item is not null).Take(4).Select(i => i.Item!.Id).ToList();
            dto.AvailableCount = items.Count(i => i.Item is not null);
            dto.IsFollowed = root.Follows.TryGetValue(viewer.Id, out var f) && f.Contains(w.Id);
            dto.UpdatedAt = w.UpdatedAt;
        }

        /// <param name="admin">Vue admin : les invitations en attente sont listées comme pour un membre.</param>
        private WatchlistDetailDto ToDetail(StoreRoot root, Watchlist w, User viewer, bool admin = false)
        {
            var dto = new WatchlistDetailDto();
            FillDetail(dto, root, w, viewer, admin);
            return dto;
        }

        private void FillDetail(WatchlistDetailDto dto, StoreRoot root, Watchlist w, User viewer, bool admin = false)
        {
            var items = VisibleItems(w, viewer);
            FillSummary(dto, root, w, viewer, items);

            var member = IsAccepted(w, viewer.Id);
            dto.Members = w.Members
                .Where(m => member || admin || m.Status == MemberStatus.Accepted)
                .OrderByDescending(m => m.Role)
                .ThenBy(m => m.InvitedAt)
                .Select(m => new MemberDto
                {
                    UserId = m.UserId,
                    Name = UserRef(m.UserId).Name,
                    Role = m.Role.ToString(),
                    Status = m.Status.ToString()
                })
                .ToList();

            dto.Items = items.Select(x => new ItemDto
            {
                EntryId = x.Entry.EntryId,
                ItemId = x.Item?.Id,
                TmdbId = x.Entry.TmdbId,
                MediaType = SeerrClient.ToMediaType(x.Entry.Type),
                PosterPath = x.Entry.PosterPath,
                Title = x.Item?.Name ?? x.Entry.Title,
                Year = x.Item?.ProductionYear ?? x.Entry.Year,
                Type = x.Entry.Type,
                Available = x.Item is not null,
                Status = x.Item is not null ? AvailabilityStatus.Available : AvailabilityStatus.NotOnServer,
                Note = x.Entry.Note,
                AddedBy = UserRef(x.Entry.AddedBy),
                AddedAt = x.Entry.AddedAt,
                Seen = w.SeenMode != SeenMode.Keep && x.Item is not null && SeenByAll(w, x.Item)
            }).ToList();

            var invited = w.Members.Any(m => m.UserId == viewer.Id && m.Status == MemberStatus.Invited);
            dto.Permissions = new PermissionsDto
            {
                CanEdit = CanEdit(w, viewer.Id),
                CanManage = CanManage(w, viewer.Id),
                CanFollow = w.Visibility == Visibility.Public && !member,
                CanLeave = member && !CanManage(w, viewer.Id),
                IsInvited = invited
            };
        }
    }
}
