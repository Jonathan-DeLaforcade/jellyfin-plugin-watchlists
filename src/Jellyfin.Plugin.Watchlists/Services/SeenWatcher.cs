using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>
    /// Retrait automatique des titres vus (listes en mode Remove) : écoute les lectures enregistrées par Jellyfin.
    /// Un épisode renvoie à sa série. Les événements sont regroupés quelques secondes : marquer une saison
    /// comme vue en déclenche un par épisode, et vérifier qu'une série est vue parcourt tous ses épisodes.
    /// </summary>
    public class SeenWatcher : IHostedService, IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

        private readonly IUserDataManager _userData;
        private readonly WatchlistService _service;
        private readonly ILogger<SeenWatcher> _logger;
        private readonly object _lock = new();
        private readonly HashSet<Guid> _pending = new();
        private Timer? _timer;

        public SeenWatcher(IUserDataManager userData, WatchlistService service, ILogger<SeenWatcher> logger)
        {
            _userData = userData;
            _service = service;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _userData.UserDataSaved += OnUserDataSaved;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _userData.UserDataSaved -= OnUserDataSaved;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            GC.SuppressFinalize(this);
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            if (e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed
                or UserDataSaveReason.UpdateUserData or UserDataSaveReason.Import))
            {
                return;
            }

            var titleId = e.Item is Episode episode ? episode.SeriesId : e.Item?.Id ?? Guid.Empty;
            if (titleId == Guid.Empty)
            {
                return;
            }

            lock (_lock)
            {
                _pending.Add(titleId);
                _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(Delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            List<Guid> ids;
            lock (_lock)
            {
                ids = new List<Guid>(_pending);
                _pending.Clear();
            }

            try
            {
                var removed = _service.RemoveSeenTitles(ids);
                if (removed > 0)
                {
                    _logger.LogInformation("Watchlists : {Count} titre(s) vu(s) retiré(s) automatiquement", removed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchlists : échec du retrait automatique des titres vus");
            }
        }
    }
}
