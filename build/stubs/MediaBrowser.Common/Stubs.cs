// Stubs de signatures reproduits depuis jellyfin v12.0 : utilisés pour compiler hors-ligne et pour les tests. Jamais livrés avec le plugin.
using System;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
namespace MediaBrowser.Common.Configuration
{
    public interface IApplicationPaths
    {
        string DataPath { get; }
        string PluginConfigurationsPath { get; }
    }
}
namespace MediaBrowser.Common.Plugins
{
    public abstract class BasePlugin
    {
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual Guid Id { get; private set; }
        public string DataFolderPath { get; private set; }
    }
    public abstract class BasePlugin<TConfigurationType> : BasePlugin
        where TConfigurationType : BasePluginConfiguration
    {
        protected BasePlugin(MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) { }
        public TConfigurationType Configuration { get; protected set; } = default!;
        public virtual void SaveConfiguration() { }
    }
}
