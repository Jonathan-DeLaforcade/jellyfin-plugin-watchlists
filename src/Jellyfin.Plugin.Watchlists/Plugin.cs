using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Watchlists
{
    /// <summary>Plugin Watchlists : listes personnelles, publiques et communes.</summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginId = new Guid("4ac0b6ae-8aaf-499f-83e9-3886c2f24b6d");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Watchlists";

        public override Guid Id => PluginId;

        public override string Description => "Watchlists par utilisateur (privées/publiques) et watchlists communes.";

        /// <summary>Page de configuration administrateur (Tableau de bord → Extensions → Watchlists).</summary>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html"
                }
            };
        }
    }

    /// <summary>Configuration globale (admin).</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>URL de Jellyseerr / Seerr vue depuis le serveur Jellyfin (ex. http://10.30.0.5:5055).</summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>Clé d'API Jellyseerr (Paramètres → Général). Jamais envoyée aux clients.</summary>
        public string SeerrApiKey { get; set; } = string.Empty;

        /// <summary>Pose un tag sur les titres présents sur le serveur et dans au moins une watchlist.</summary>
        public bool TagEnabled { get; set; } = true;

        public string TagName { get; set; } = "watchlist";

        /// <summary>Interne : dernier nom de tag appliqué, pour nettoyer l'ancien tag après un renommage.</summary>
        public string LastAppliedTagName { get; set; } = string.Empty;
    }
}
