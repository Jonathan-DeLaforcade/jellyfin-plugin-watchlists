using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Watchlists.Data;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>
    /// Index en mémoire des films et séries du serveur, par ids TMDB / IMDb / TVDB.
    /// Permet de retrouver un titre de watchlist après suppression puis re-téléchargement
    /// (Jellyfin lui donne alors un nouvel ItemId). Reconstruit à la demande quand la bibliothèque change.
    /// </summary>
    public class LibraryIndex
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

        private readonly ILibraryManager _library;
        private readonly object _lock = new();
        private Dictionary<string, Guid> _byProvider = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _builtAt = DateTime.MinValue;
        private bool _dirty = true;

        public LibraryIndex(ILibraryManager library)
        {
            _library = library;
        }

        public static string? ToType(BaseItem item) => item.GetType().Name switch
        {
            "Movie" => "Movie",
            "Series" => "Series",
            _ => null
        };

        /// <summary>Clé d'index : type + fournisseur + id (ex. "Movie|tmdb|603").</summary>
        public static string Key(string type, string provider, string id) => type + "|" + provider + "|" + id.Trim();

        public void Invalidate()
        {
            lock (_lock)
            {
                _dirty = true;
            }
        }

        /// <summary>Films et séries du serveur (requête légère : ids fournisseurs uniquement).</summary>
        public IReadOnlyList<BaseItem> QueryAll()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
                DtoOptions = new DtoOptions(false)
                {
                    Fields = new[] { ItemFields.ProviderIds },
                    EnableImages = false,
                    EnableUserData = false
                }
            };
            return _library.GetItemList(query);
        }

        public void Rebuild()
        {
            var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in QueryAll())
            {
                var type = ToType(item);
                if (type is null || item.ProviderIds is null)
                {
                    continue;
                }

                foreach (var provider in new[] { "Tmdb", "Imdb", "Tvdb" })
                {
                    if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id))
                    {
                        map.TryAdd(Key(type, provider.ToLowerInvariant(), id), item.Id);
                    }
                }
            }

            lock (_lock)
            {
                _byProvider = map;
                _builtAt = DateTime.UtcNow;
                _dirty = false;
            }
        }

        private Dictionary<string, Guid> Map()
        {
            bool stale;
            lock (_lock)
            {
                stale = _dirty || DateTime.UtcNow - _builtAt > MaxAge;
            }

            if (stale)
            {
                Rebuild();
            }

            lock (_lock)
            {
                return _byProvider;
            }
        }

        /// <summary>Item du serveur correspondant à un id fournisseur, ou null.</summary>
        public BaseItem? FindByProvider(string type, string? tmdbId, string? imdbId, string? tvdbId)
        {
            var map = Map();
            foreach (var (provider, id) in new[] { ("tmdb", tmdbId), ("imdb", imdbId), ("tvdb", tvdbId) })
            {
                if (!string.IsNullOrWhiteSpace(id) && map.TryGetValue(Key(type, provider, id), out var itemId))
                {
                    var item = _library.GetItemById(itemId);
                    if (item is not null && ToType(item) == type)
                    {
                        return item;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Résout une entrée de watchlist vers l'item actuellement présent sur le serveur :
        /// d'abord le dernier ItemId connu, sinon les ids fournisseurs. Null si le titre n'est pas sur le serveur.
        /// </summary>
        public BaseItem? Resolve(WatchlistItem entry)
        {
            if (entry.ItemId != Guid.Empty)
            {
                var item = _library.GetItemById(entry.ItemId);
                if (item is not null && ToType(item) == entry.Type)
                {
                    return item;
                }
            }

            return FindByProvider(entry.Type, entry.TmdbId, entry.ImdbId, entry.TvdbId);
        }

        public static string? ProviderId(BaseItem item, string provider)
            => item.ProviderIds is not null && item.ProviderIds.TryGetValue(provider, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        /// <summary>Recopie dans l'entrée ce que le serveur sait du titre. Retourne true si l'entrée a changé.</summary>
        public static bool Absorb(WatchlistItem entry, BaseItem item)
        {
            var changed = false;
            void Set(Func<string?> get, Action<string?> set, string? value)
            {
                if (value is not null && get() != value)
                {
                    set(value);
                    changed = true;
                }
            }

            if (entry.ItemId != item.Id)
            {
                entry.ItemId = item.Id;
                changed = true;
            }

            Set(() => entry.TmdbId, v => entry.TmdbId = v, ProviderId(item, "Tmdb"));
            Set(() => entry.ImdbId, v => entry.ImdbId = v, ProviderId(item, "Imdb"));
            Set(() => entry.TvdbId, v => entry.TvdbId = v, ProviderId(item, "Tvdb"));
            if (!string.IsNullOrEmpty(item.Name) && entry.Title != item.Name)
            {
                entry.Title = item.Name;
                changed = true;
            }

            if (item.ProductionYear.HasValue && entry.Year != item.ProductionYear)
            {
                entry.Year = item.ProductionYear;
                changed = true;
            }

            return changed;
        }
    }
}
