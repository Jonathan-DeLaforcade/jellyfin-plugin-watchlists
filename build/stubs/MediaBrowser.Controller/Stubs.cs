// Stubs de signatures reproduits depuis jellyfin v12.0 : utilisés pour compiler hors-ligne et pour les tests. Jamais livrés avec le plugin.
using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;
namespace MediaBrowser.Controller { public interface IServerApplicationHost { } }
namespace MediaBrowser.Controller.Plugins
{
    public interface IPluginServiceRegistrator
    {
        void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost);
    }
}
namespace MediaBrowser.Controller.Entities
{
    public abstract class BaseItem
    {
        public Guid Id { get; set; }
        public virtual string Name { get; set; }
        public int? ProductionYear { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public virtual bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
        public virtual long? RunTimeTicks { get; set; }
        public virtual string[] Genres { get; set; } = Array.Empty<string>();
        public virtual bool IsPlayed(User user, UserItemData? userItemData) => userItemData?.Played ?? false;
        public virtual System.Threading.Tasks.Task UpdateToRepositoryAsync(MediaBrowser.Controller.Library.ItemUpdateType updateReason, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
    }

    public class UserItemData
    {
        public bool Played { get; set; }
    }

    public class InternalItemsQuery
    {
        public InternalItemsQuery() { }
        public InternalItemsQuery(User? user) { }
        public bool Recursive { get; set; }
        public int? Limit { get; set; }
        public Jellyfin.Data.Enums.BaseItemKind[] IncludeItemTypes { get; set; } = Array.Empty<Jellyfin.Data.Enums.BaseItemKind>();
        public string[] Tags { get; set; } = Array.Empty<string>();
        public bool? IsVirtualItem { get; set; }
        public MediaBrowser.Controller.Dto.DtoOptions DtoOptions { get; set; } = new();
    }
}
namespace MediaBrowser.Controller.Entities.TV
{
    public class Episode : MediaBrowser.Controller.Entities.BaseItem
    {
        public virtual Guid SeriesId { get; set; }
    }
}
namespace MediaBrowser.Controller.Library
{
    using MediaBrowser.Controller.Entities;
    public interface IUserDataManager
    {
        event EventHandler<UserDataSaveEventArgs>? UserDataSaved;
        UserItemData GetUserData(User user, BaseItem item);
    }

    public class UserDataSaveEventArgs : EventArgs
    {
        public Guid UserId { get; set; }
        public BaseItem Item { get; set; }
        public MediaBrowser.Model.Entities.UserDataSaveReason SaveReason { get; set; }
    }
    public interface ILibraryManager
    {
        event EventHandler<ItemChangeEventArgs>? ItemAdded;
        event EventHandler<ItemChangeEventArgs>? ItemUpdated;
        event EventHandler<ItemChangeEventArgs>? ItemRemoved;
        BaseItem? GetItemById(Guid id);
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);
    }

    public class ItemChangeEventArgs
    {
        public BaseItem Item { get; set; }
        public BaseItem Parent { get; set; }
        public ItemUpdateType UpdateReason { get; set; }
    }
    public interface IUserManager
    {
        IEnumerable<User> GetUsers();
        User? GetUserById(Guid id);
    }
}

namespace MediaBrowser.Controller.Dto
{
    public class DtoOptions
    {
        public DtoOptions() : this(true) { }
        public DtoOptions(bool allFields) { Fields = Array.Empty<MediaBrowser.Model.Querying.ItemFields>(); EnableImages = true; EnableUserData = true; }
        public IReadOnlyList<MediaBrowser.Model.Querying.ItemFields> Fields { get; set; }
        public bool EnableImages { get; set; }
        public bool EnableUserData { get; set; }
    }
}
