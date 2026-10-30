using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlists.Data
{
    /// <summary>
    /// Stockage JSON unique (data/watchlists/watchlists.json), en mémoire + écriture atomique.
    /// Toutes les lectures/écritures passent par <see cref="Read{T}"/> / <see cref="Write{T}"/> sous verrou.
    /// </summary>
    public class WatchlistStore
    {
        internal static readonly JsonSerializerOptions FileJson = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly object _lock = new();
        private readonly string _path;
        private readonly ILogger<WatchlistStore> _logger;
        private StoreRoot _root;

        public WatchlistStore(IApplicationPaths paths, ILogger<WatchlistStore> logger)
        {
            _logger = logger;
            var dir = Path.Combine(paths.DataPath, "watchlists");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "watchlists.json");
            _root = Load();
        }

        public T Read<T>(Func<StoreRoot, T> reader)
        {
            lock (_lock)
            {
                return reader(_root);
            }
        }

        /// <summary>Exécute une mutation et ne persiste que si elle signale un changement (true).</summary>
        public bool Mutate(Func<StoreRoot, bool> mutation)
        {
            lock (_lock)
            {
                bool changed;
                try
                {
                    changed = mutation(_root);
                }
                catch
                {
                    _root = Load();
                    throw;
                }

                if (changed)
                {
                    Save();
                }

                return changed;
            }
        }

        /// <summary>Exécute une mutation puis persiste. Si la mutation lève, rien n'est écrit et l'état est rechargé.</summary>
        public T Write<T>(Func<StoreRoot, T> mutation)
        {
            lock (_lock)
            {
                T result;
                try
                {
                    result = mutation(_root);
                }
                catch
                {
                    // La mutation a pu modifier partiellement l'état : on recharge depuis le disque.
                    _root = Load();
                    throw;
                }

                Save();
                return result;
            }
        }

        private StoreRoot Load()
        {
            if (!File.Exists(_path))
            {
                return new StoreRoot();
            }

            try
            {
                StoreRoot root;
                using (var fs = File.OpenRead(_path))
                {
                    root = JsonSerializer.Deserialize<StoreRoot>(fs, FileJson) ?? new StoreRoot();
                }

                // Migration v1 -> v2 : chaque entrée reçoit un identifiant stable.
                foreach (var item in root.Watchlists.SelectMany(w => w.Items))
                {
                    if (item.EntryId == Guid.Empty)
                    {
                        item.EntryId = Guid.NewGuid();
                    }
                }

                root.SchemaVersion = 2;
                return root;
            }
            catch (Exception ex)
            {
                var backup = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                _logger.LogError(ex, "Watchlists : fichier illisible, sauvegardé sous {Backup}, démarrage à vide", backup);
                File.Copy(_path, backup, true);
                return new StoreRoot();
            }
        }

        private void Save()
        {
            var tmp = _path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, _root, FileJson);
                fs.Flush(true);
            }

            File.Move(tmp, _path, true);
        }
    }
}
