using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Watchlists.Services
{
    // ---- entrées (lues par le contrôleur, insensibles à la casse)

    public class WatchlistInput
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        /// <summary>Private | Public.</summary>
        public string? Visibility { get; set; }

        /// <summary>Keep | Dim | Remove : titres vus par tous les membres.</summary>
        public string? SeenMode { get; set; }
    }

    public class ItemInput
    {
        /// <summary>Item du serveur.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Ou titre TMDB (présent ou non sur le serveur), avec MediaType = movie | tv.</summary>
        public string? TmdbId { get; set; }

        public string? MediaType { get; set; }

        public string? Note { get; set; }

        /// <summary>Ou copie d'une entrée d'une autre liste visible (proposition de liste commune).</summary>
        public Guid? FromWatchlistId { get; set; }

        public Guid? FromEntryId { get; set; }
    }

    /// <summary>Création d'une liste commune avec un autre utilisateur, à partir d'une entrée existante.</summary>
    public class SharedInput
    {
        public Guid? UserId { get; set; }

        public Guid? FromWatchlistId { get; set; }

        public Guid? EntryId { get; set; }
    }

    public class RequestInput
    {
        public string? TmdbId { get; set; }

        /// <summary>movie | tv.</summary>
        public string? MediaType { get; set; }
    }

    public class OrderInput
    {
        /// <summary>EntryId (ou ItemId pour compatibilité), dans l'ordre voulu.</summary>
        public List<Guid>? ItemIds { get; set; }
    }

    public class MemberInput
    {
        public Guid? UserId { get; set; }

        /// <summary>Viewer | Editor | Owner (Owner = transfert, uniquement en modification).</summary>
        public string? Role { get; set; }

        /// <summary>Admin uniquement : ajoute directement sans invitation.</summary>
        public bool? Force { get; set; }
    }

    // ---- sorties

    public class UserRefDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class WatchlistSummaryDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Visibility { get; set; } = string.Empty;

        /// <summary>Keep | Dim | Remove.</summary>
        public string SeenMode { get; set; } = string.Empty;

        public string? MyRole { get; set; }

        public string? MyStatus { get; set; }

        public UserRefDto? Owner { get; set; }

        public int MemberCount { get; set; }

        public int ItemCount { get; set; }

        /// <summary>Obsolète (1.1) : remplacé par Posters.</summary>
        public List<Guid> PosterItemIds { get; set; } = new();

        /// <summary>Jusqu'à 4 affiches : item du serveur (ItemId) ou affiche TMDB (PosterPath).</summary>
        public List<PosterDto> Posters { get; set; } = new();

        /// <summary>Nombre de titres de la liste présents sur le serveur.</summary>
        public int AvailableCount { get; set; }

        public bool IsFollowed { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    public class WatchlistDetailDto : WatchlistSummaryDto
    {
        public List<MemberDto> Members { get; set; } = new();

        public List<ItemDto> Items { get; set; } = new();

        public PermissionsDto Permissions { get; set; } = new();
    }

    /// <summary>Résultat d'un ajout : la liste, plus les autres utilisateurs qui ont aussi ce titre.</summary>
    public class AddItemResultDto : WatchlistDetailDto
    {
        public Guid AddedEntryId { get; set; }

        public List<MatchDto> Matches { get; set; } = new();
    }

    /// <summary>
    /// Un autre utilisateur a ce titre dans une de ses listes. Lists = listes communes modifiables qui ne l'ont pas encore ;
    /// vide = aucune liste commune, proposer d'en créer une.
    /// </summary>
    public class MatchDto
    {
        public UserRefDto User { get; set; } = new();

        public List<ListRefDto> Lists { get; set; } = new();
    }

    public class ListRefDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Item de la bibliothèque sur le point d'être supprimé, et les listes qui le contiennent.</summary>
    public class DeleteCheckDto
    {
        public Guid ItemId { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<DeleteCheckListDto> Lists { get; set; } = new();
    }

    public class DeleteCheckListDto
    {
        /// <summary>Null pour une liste privée que l'appelant ne peut pas voir.</summary>
        public string? Name { get; set; }

        public UserRefDto Owner { get; set; } = new();

        public bool Private { get; set; }
    }

    /// <summary>Tirage au sort : partenaires possibles, listes retenues et titres candidats.</summary>
    public class PickDto
    {
        /// <summary>Utilisateurs membres d'au moins une de mes listes (cochables).</summary>
        public List<UserRefDto> Partners { get; set; } = new();

        /// <summary>Mes listes dont tous les membres cochés sont aussi membres.</summary>
        public List<ListRefDto> Lists { get; set; } = new();

        public List<PickItemDto> Candidates { get; set; } = new();
    }

    public class PickItemDto
    {
        public Guid ItemId { get; set; }

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string Type { get; set; } = string.Empty;

        /// <summary>Durée du film (d'un épisode pour une série).</summary>
        public int? RuntimeMinutes { get; set; }

        public List<string> Genres { get; set; } = new();

        /// <summary>Noms des listes retenues qui contiennent le titre.</summary>
        public List<string> Lists { get; set; } = new();
    }

    public class MemberDto
    {
        public Guid UserId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Role { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    public class ItemDto
    {
        /// <summary>Identifiant de l'entrée : à utiliser pour modifier / retirer / réordonner.</summary>
        public Guid EntryId { get; set; }

        /// <summary>Item du serveur, null si le titre n'est pas (ou plus) sur le serveur.</summary>
        public Guid? ItemId { get; set; }

        public string? TmdbId { get; set; }

        /// <summary>movie | tv (pour Jellyseerr).</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Affiche TMDB pour un titre absent du serveur.</summary>
        public string? PosterPath { get; set; }

        /// <summary>Available ou NotOnServer ; le détail (demandé, en cours...) vient de /Availability.</summary>
        public AvailabilityStatus Status { get; set; }

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string Type { get; set; } = string.Empty;

        /// <summary>True si le titre est sur le serveur.</summary>
        public bool Available { get; set; }

        public string Note { get; set; } = string.Empty;

        public UserRefDto AddedBy { get; set; } = new();

        public DateTime AddedAt { get; set; }

        /// <summary>Vu par tous les membres acceptés. Calculé seulement si la liste n'est pas en mode Keep.</summary>
        public bool Seen { get; set; }
    }

    public class PermissionsDto
    {
        public bool CanEdit { get; set; }

        public bool CanManage { get; set; }

        public bool CanFollow { get; set; }

        public bool CanLeave { get; set; }

        public bool IsInvited { get; set; }
    }

    /// <summary>Vue admin : résumé + membres acceptés et nombre d'invitations en attente.</summary>
    public class AdminWatchlistDto : WatchlistSummaryDto
    {
        public List<UserRefDto> Members { get; set; } = new();

        public int InvitedCount { get; set; }
    }

    public class ContainingDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Contains { get; set; }

        /// <summary>Private | Public.</summary>
        public string Visibility { get; set; } = string.Empty;

        /// <summary>Plus d'un membre accepté (liste commune).</summary>
        public bool Shared { get; set; }

        /// <summary>Entrée correspondante (pour la retirer), null si absente.</summary>
        public Guid? EntryId { get; set; }
    }

    public enum AvailabilityStatus
    {
        /// <summary>Sur le serveur.</summary>
        Available,

        /// <summary>Pas sur le serveur (statut Jellyseerr pas encore consulté).</summary>
        NotOnServer,

        /// <summary>Pas sur le serveur, jamais demandé (ou supprimé) : demandable.</summary>
        NotRequested,

        /// <summary>Demandé, en attente de validation.</summary>
        Pending,

        /// <summary>Validé, téléchargement en cours.</summary>
        Processing,

        /// <summary>Jellyseerr le croit disponible mais il n'est pas sur le serveur (scan Jellyseerr en retard).</summary>
        SeerrAvailable,

        /// <summary>Bloqué dans Jellyseerr.</summary>
        Blocklisted,

        /// <summary>Statut indéterminé (pas d'id TMDB ou Jellyseerr injoignable).</summary>
        Unknown
    }

    public class PosterDto
    {
        public Guid? ItemId { get; set; }

        public string? PosterPath { get; set; }
    }

    public class AvailabilityDto
    {
        public Guid EntryId { get; set; }

        public AvailabilityStatus Status { get; set; }

        public bool CanRequest { get; set; }
    }

    public class SearchResultDto
    {
        public string TmdbId { get; set; } = string.Empty;

        /// <summary>movie | tv.</summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>Movie | Series.</summary>
        public string Type { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string? PosterPath { get; set; }

        /// <summary>Item du serveur si le titre y est (et visible par l'utilisateur).</summary>
        public Guid? ItemId { get; set; }

        public AvailabilityStatus Status { get; set; }

        public bool CanRequest { get; set; }
    }

    public class SeerrStateDto
    {
        public bool Enabled { get; set; }

        public bool IsAdmin { get; set; }
    }
}
