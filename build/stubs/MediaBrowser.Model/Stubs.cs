// Signature stubs reproduced from jellyfin v12.0 (compile-time only, never shipped).
namespace MediaBrowser.Model.Plugins { public class BasePluginConfiguration { } }
namespace MediaBrowser.Model.Serialization { public interface IXmlSerializer { } }
namespace MediaBrowser.Model.Plugins
{
    public class PluginPageInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string EmbeddedResourcePath { get; set; } = string.Empty;
        public bool EnableInMainMenu { get; set; }
        public string? MenuSection { get; set; }
        public string? MenuIcon { get; set; }
    }

    public interface IHasWebPages
    {
        System.Collections.Generic.IEnumerable<PluginPageInfo> GetPages();
    }
}
namespace MediaBrowser.Model.Tasks
{
    public class TaskTriggerInfo
    {
        public TaskTriggerInfoType Type { get; set; }
        public long? TimeOfDayTicks { get; set; }
        public long? IntervalTicks { get; set; }
        public System.DayOfWeek? DayOfWeek { get; set; }
        public long? MaxRuntimeTicks { get; set; }
    }

    public interface IScheduledTask
    {
        string Name { get; }
        string Key { get; }
        string Description { get; }
        string Category { get; }
        System.Threading.Tasks.Task ExecuteAsync(System.IProgress<double> progress, System.Threading.CancellationToken cancellationToken);
        System.Collections.Generic.IEnumerable<TaskTriggerInfo> GetDefaultTriggers();
    }
}
namespace MediaBrowser.Model.Entities
{
    public enum UserDataSaveReason
    {
        PlaybackStart,
        PlaybackProgress,
        PlaybackFinished,
        TogglePlayed,
        UpdateUserRating,
        Import,
        UpdateUserData
    }
}
