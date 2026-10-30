namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>Accès à la configuration (abstrait pour les tests).</summary>
    public interface IWatchlistsSettings
    {
        PluginConfiguration Current { get; }

        void Save();
    }

    public class PluginSettings : IWatchlistsSettings
    {
        private static readonly PluginConfiguration Fallback = new();

        public PluginConfiguration Current => Plugin.Instance?.Configuration ?? Fallback;

        public void Save() => Plugin.Instance?.SaveConfiguration();
    }
}
