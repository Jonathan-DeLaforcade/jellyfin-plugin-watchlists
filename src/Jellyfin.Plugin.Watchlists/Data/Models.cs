using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Watchlists.Data
{
    public enum Visibility
    {
        Private,
        Public
    }

    /// <summary>Que faire d'un titre vu par tous les membres acceptés de la liste.</summary>
    public enum SeenMode
    {
        Keep,
        Dim,
        Remove
    }

    public enum MemberRole
    {
        Viewer,
        Editor,
        Owner
    }

    public enum MemberStatus
    {
        Invited,
        Accepted
    }

    /// <summary>Racine du fichier de stockage.</summary>
    public class StoreRoot
    {
        public int SchemaVersion { get; set; } = 2;

        public List<Watchlist> Watchlists { get; set; } = new();

        /// <summary>userId -> ids des watchlists publiques suivies.</summary>
        public Dictionary<Guid, List<Guid>> Follows { get; set; } = new();
    }

    public class Watchlist
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public Visibility Visibility { get; set; } = Visibility.Private;

        public SeenMode SeenMode { get; set; } = SeenMode.Keep;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public List<WatchlistMember> Members { get; set; } = new();

        /// <summary>Ordre de la liste = ordre d'affichage.</summary>
        public List<WatchlistItem> Items { get; set; } = new();
    }

    public class WatchlistMember
    {
        public Guid UserId { get; set; }

        public MemberRole Role { get; set; }

        public MemberStatus Status { get; set; }

        public Guid InvitedBy { get; set; }

        public DateTime InvitedAt { get; set; } = DateTime.UtcNow;

        public DateTime? JoinedAt { get; set; }
    }

    public class WatchlistItem
    {
        /// <summary>Identifiant stable de l'entrée (indépendant de la présence du média sur le serveur).</summary>
        public Guid EntryId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Dernier item Jellyfin connu pour ce titre. Guid.Empty si le titre a été ajouté sans être sur le serveur.
        /// Peut pointer vers un item supprimé : la résolution passe ensuite par les ids TMDB/IMDb/TVDB.
        /// </summary>
        public Guid ItemId { get; set; }

        public string? TmdbId { get; set; }

        public string? ImdbId { get; set; }

        public string? TvdbId { get; set; }

        /// <summary>Instantané au moment de l'ajout, affiché quand le titre n'est pas sur le serveur.</summary>
        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        /// <summary>Chemin d'affiche TMDB (ex. "/abc.jpg"), utilisé quand le titre n'est pas sur le serveur.</summary>
        public string? PosterPath { get; set; }

        /// <summary>Movie ou Series.</summary>
        public string Type { get; set; } = string.Empty;

        public Guid AddedBy { get; set; }

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        public string Note { get; set; } = string.Empty;
    }
}
