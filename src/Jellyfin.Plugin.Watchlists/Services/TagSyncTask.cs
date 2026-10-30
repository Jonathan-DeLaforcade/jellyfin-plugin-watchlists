using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>Tâche planifiée (Tableau de bord → Tâches planifiées) : resynchronise le tag Maintainerr.</summary>
    public class TagSyncTask : IScheduledTask
    {
        private readonly TagSyncService _sync;

        public TagSyncTask(TagSyncService sync)
        {
            _sync = sync;
        }

        public string Name => "Synchroniser les tags Watchlists";

        public string Key => "WatchlistsTagSync";

        public string Description => "Pose le tag de watchlist sur les titres présents sur le serveur et dans au moins une watchlist, et le retire des autres.";

        public string Category => "Watchlists";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress.Report(0);
            await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }
            };
        }
    }
}
