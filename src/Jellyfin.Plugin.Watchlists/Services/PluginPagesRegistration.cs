using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Watchlists.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchlists.Services
{
    /// <summary>
    /// Au démarrage : nettoyage des utilisateurs supprimés, puis intégrations web optionnelles, par réflexion
    /// (aucune dépendance de compilation) :
    ///  - Plugin Pages : entrée « Watchlists » dans le menu latéral ;
    ///  - File Transformation : injection du bouton d'en-tête dans index.html.
    /// Ces deux plugins gardent leurs enregistrements en mémoire : on les refait à chaque démarrage.
    /// </summary>
    public class PluginPagesRegistration : BackgroundService
    {
        public const string PageId = "watchlists-4ac0b6ae";
        public static readonly Guid HeaderTransformationId = new("4ac0b6ae-8aaf-499f-83e9-3886c2f24b6e");

        private readonly WatchlistService _service;
        private readonly ILogger<PluginPagesRegistration> _logger;

        public PluginPagesRegistration(WatchlistService service, ILogger<PluginPagesRegistration> logger)
        {
            _service = service;
            _logger = logger;
        }

        private enum RegisterResult
        {
            Done,
            NotInstalled,
            Retry
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var changes = _service.CleanupDeletedUsers();
                if (changes > 0)
                {
                    _logger.LogInformation("Watchlists : {Count} référence(s) à des utilisateurs supprimés nettoyée(s)", changes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchlists : échec du nettoyage au démarrage");
            }

            var pageJson = "{"
                + "\"id\":\"" + PageId + "\","
                + "\"url\":\"/Watchlists/Web/watchlists.html\","
                + "\"displayText\":\"Watchlists\","
                + "\"icon\":\"playlist_add_check\""
                + "}";
            var headerJson = "{"
                + "\"id\":\"" + HeaderTransformationId + "\","
                + "\"fileNamePattern\":\"index.html\","
                + "\"callbackAssembly\":\"" + typeof(Transformations).Assembly.FullName + "\","
                + "\"callbackClass\":\"" + typeof(Transformations).FullName + "\","
                + "\"callbackMethod\":\"" + nameof(Transformations.IndexHtml) + "\""
                + "}";

            var pending = new[]
            {
                (Name: "Plugin Pages", Assembly: "Jellyfin.Plugin.PluginPages", Method: "RegisterPage", Json: pageJson,
                    Missing: "pas d'entrée Watchlists dans le menu latéral"),
                (Name: "File Transformation", Assembly: "Jellyfin.Plugin.FileTransformation", Method: "RegisterTransformation", Json: headerJson,
                    Missing: "pas de bouton Watchlists dans l'en-tête")
            }.ToList();

            // Ces plugins peuvent être initialisés après nous : on réessaie pendant ~2 minutes.
            for (var attempt = 0; attempt < 24 && pending.Count > 0 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                foreach (var p in pending.ToList())
                {
                    switch (TryRegister(p.Assembly, p.Method, p.Json))
                    {
                        case RegisterResult.Done:
                            _logger.LogInformation("Watchlists : intégration {Plugin} enregistrée", p.Name);
                            pending.Remove(p);
                            break;
                        case RegisterResult.NotInstalled when attempt >= 6:
                            _logger.LogWarning("Watchlists : plugin « {Plugin} » introuvable, {Effect}", p.Name, p.Missing);
                            pending.Remove(p);
                            break;
                    }
                }

                if (pending.Count == 0)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }

            foreach (var p in pending)
            {
                _logger.LogWarning("Watchlists : impossible de s'enregistrer auprès de {Plugin} après plusieurs tentatives", p.Name);
            }
        }

        private RegisterResult TryRegister(string assemblyName, string methodName, string json)
        {
            try
            {
                var assembly = AssemblyLoadContext.All
                    .SelectMany(c => c.Assemblies)
                    .FirstOrDefault(a => a.GetName().Name == assemblyName);
                if (assembly is null)
                {
                    return RegisterResult.NotInstalled;
                }

                var method = assembly.GetType(assemblyName + ".PluginInterface")?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method is null)
                {
                    _logger.LogWarning("Watchlists : version de {Assembly} non supportée (PluginInterface.{Method} absent)", assemblyName, methodName);
                    return RegisterResult.Done;
                }

                // Le paramètre est un Newtonsoft JObject : construit via JObject.Parse(string) de l'assembly du plugin cible.
                var payloadType = method.GetParameters()[0].ParameterType;
                var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
                if (parse is null)
                {
                    _logger.LogWarning("Watchlists : type de payload inattendu pour {Assembly} ({Type})", assemblyName, payloadType.FullName);
                    return RegisterResult.Done;
                }

                method.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
                return RegisterResult.Done;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException)
            {
                // Instance du plugin cible pas encore initialisée.
                return RegisterResult.Retry;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Watchlists : enregistrement auprès de {Assembly} pas encore possible", assemblyName);
                return RegisterResult.Retry;
            }
        }
    }
}
