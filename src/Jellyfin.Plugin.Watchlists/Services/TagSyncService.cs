using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Watchlists.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlists.Services
{
    public class TagSyncResult
    {
        public int InWatchlists { get; set; }

        public int OnServer { get; set; }

        public int Tagged { get; set; }

        public int Untagged { get; set; }

        public int EntriesUpdated { get; set; }

        public string? Skipped { get; set; }
    }

    /// <summary>
    /// Tag « watchlist » pour Maintainerr : présent sur un titre SI ET SEULEMENT SI il est sur le serveur
    /// ET dans au moins une watchlist. Le plugin ne supprime jamais rien : si Maintainerr supprime le fichier,
    /// l'entrée reste dans la watchlist (ids TMDB/IMDb/TVDB) et le tag revient quand le titre est re-téléchargé.
    /// Synchro complète au démarrage, après chaque modification des watchlists ou de la bibliothèque (anti-rebond),
    /// et par la tâche planifiée « Watchlists : synchroniser les tags ».
    /// </summary>
    public class TagSyncService : IHostedService, IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

        private readonly WatchlistStore _store;
        private readonly LibraryIndex _index;
        private readonly ILibraryManager _library;
        private readonly IWatchlistsSettings _settings;
        private readonly ILogger<TagSyncService> _logger;
        private readonly SemaphoreSlim _running = new(1, 1);
        private readonly ConcurrentDictionary<Guid, byte> _selfUpdates = new();
        private readonly object _timerLock = new();
        private Timer? _timer;
        private DateTime? _firstRequest;
        private CancellationTokenSource _cts = new();

        public TagSyncService(WatchlistStore store, LibraryIndex index, ILibraryManager library, IWatchlistsSettings settings, ILogger<TagSyncService> logger)
        {
            _store = store;
            _index = index;
            _library = library;
            _settings = settings;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded += OnLibraryChanged;
            _library.ItemUpdated += OnLibraryChanged;
            _library.ItemRemoved += OnLibraryChanged;
            // Premier passage différé : laisse le serveur finir de démarrer.
            ScheduleIn(TimeSpan.FromSeconds(45));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded -= OnLibraryChanged;
            _library.ItemUpdated -= OnLibraryChanged;
            _library.ItemRemoved -= OnLibraryChanged;
            _cts.Cancel();
            lock (_timerLock)
            {
                _timer?.Dispose();
                _timer = null;
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _cts.Dispose();
            _running.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Demande une synchro (anti-rebond : 15 s après la dernière demande, au plus 2 min après la première).</summary>
        public void RequestSync()
        {
            lock (_timerLock)
            {
                var now = DateTime.UtcNow;
                _firstRequest ??= now;
                var delay = Debounce;
                var deadline = _firstRequest.Value + MaxDelay;
                if (now + delay > deadline)
                {
                    delay = deadline > now ? deadline - now : TimeSpan.Zero;
                }

                ScheduleInLocked(delay);
            }
        }

        private void ScheduleIn(TimeSpan delay)
        {
            lock (_timerLock)
            {
                ScheduleInLocked(delay);
            }
        }

        private void ScheduleInLocked(TimeSpan delay)
        {
            _timer ??= new Timer(_ => _ = RunScheduledAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private async Task RunScheduledAsync()
        {
            lock (_timerLock)
            {
                _firstRequest = null;
            }

            try
            {
                await SyncAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchlists : échec de la synchronisation des tags");
            }
        }

        private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is null || LibraryIndex.ToType(e.Item) is null)
            {
                return;
            }

            // Nos propres écritures de tags déclenchent ItemUpdated : on les ignore.
            if (_selfUpdates.ContainsKey(e.Item.Id))
            {
                return;
            }

            _index.Invalidate();
            RequestSync();
        }

        /// <summary>Synchronisation complète. Sérialisée : un seul passage à la fois.</summary>
        public async Task<TagSyncResult> SyncAsync(CancellationToken ct)
        {
            await _running.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SyncCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _running.Release();
            }
        }

        private async Task<TagSyncResult> SyncCoreAsync(CancellationToken ct)
        {
            var result = new TagSyncResult();
            _index.Rebuild();

            // 1. Résolution des entrées : ItemId à jour + ids fournisseurs complétés (permet de retrouver
            //    le titre après suppression / re-téléchargement). Persisté seulement si quelque chose change.
            var desired = new HashSet<Guid>();
            var updated = 0;
            var total = 0;
            _store.Mutate(root =>
            {
                var changed = false;
                foreach (var entry in root.Watchlists.SelectMany(w => w.Items))
                {
                    total++;
                    var item = _index.Resolve(entry);
                    if (item is null)
                    {
                        continue;
                    }

                    desired.Add(item.Id);
                    if (LibraryIndex.Absorb(entry, item))
                    {
                        updated++;
                        changed = true;
                    }
                }

                return changed;
            });
            result.InWatchlists = total;
            result.OnServer = desired.Count;
            result.EntriesUpdated = updated;

            // 2. Tags.
            var config = _settings.Current;
            var tag = (config.TagName ?? string.Empty).Trim();
            if (!config.TagEnabled || tag.Length == 0)
            {
                result.Skipped = "Tag désactivé dans la configuration.";
                return result;
            }

            var previous = (config.LastAppliedTagName ?? string.Empty).Trim();
            var renamed = previous.Length > 0 && !string.Equals(previous, tag, StringComparison.OrdinalIgnoreCase);

            foreach (var light in _index.QueryAll())
            {
                ct.ThrowIfCancellationRequested();
                var tags = light.Tags ?? Array.Empty<string>();
                var has = tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
                var hasOld = renamed && tags.Contains(previous, StringComparer.OrdinalIgnoreCase);
                var want = desired.Contains(light.Id);
                if (has == want && !hasOld)
                {
                    continue;
                }

                // Item complet (ids fournisseurs, champs verrouillés...) avant toute écriture.
                var item = _library.GetItemById(light.Id);
                if (item is null)
                {
                    continue;
                }

                var newTags = (item.Tags ?? Array.Empty<string>())
                    .Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)
                        && !(renamed && string.Equals(t, previous, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (want)
                {
                    newTags.Add(tag);
                }

                item.Tags = newTags.ToArray();
                _selfUpdates[item.Id] = 0;
                try
                {
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                }
                finally
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => _selfUpdates.TryRemove(item.Id, out byte _), TaskScheduler.Default);
                }

                if (want && !has)
                {
                    result.Tagged++;
                }
                else if (!want && has)
                {
                    result.Untagged++;
                }
            }

            if (!string.Equals(previous, tag, StringComparison.Ordinal))
            {
                config.LastAppliedTagName = tag;
                _settings.Save();
            }

            if (result.Tagged + result.Untagged + result.EntriesUpdated > 0)
            {
                _logger.LogInformation(
                    "Watchlists : synchro — {Tagged} tag(s) « {Tag} » ajouté(s), {Untagged} retiré(s), {Updated} entrée(s) mises à jour ({OnServer}/{Total} titres de watchlist sur le serveur)",
                    result.Tagged,
                    tag,
                    result.Untagged,
                    result.EntriesUpdated,
                    result.OnServer,
                    result.InWatchlists);
            }

            return result;
        }
    }
}
