using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Watchlists.Web
{
    /// <summary>Payload passé par File Transformation (désérialisé par Newtonsoft depuis {"contents": "..."}).</summary>
    public class TransformationPayload
    {
        public string? Contents { get; set; }
    }

    /// <summary>Callbacks appelés par réflexion par le plugin File Transformation.</summary>
    public static class Transformations
    {
        private const string Marker = "data-watchlists-header";

        /// <summary>
        /// Ajoute le script du bouton d'en-tête avant &lt;/body&gt; d'index.html.
        /// Chemin relatif ("../Watchlists/...") : index.html est servi sous {baseUrl}/web/, ce qui marche avec ou sans BaseUrl.
        /// </summary>
        public static string IndexHtml(TransformationPayload payload)
        {
            var html = payload.Contents ?? string.Empty;
            if (html.Contains(Marker, System.StringComparison.Ordinal))
            {
                return html;
            }

            var version = typeof(Transformations).Assembly.GetName().Version?.ToString() ?? "1";
            var tag = "<script " + Marker + " src=\"../Watchlists/Web/header.js?v=" + version + "\" defer></script>";
            return Regex.Replace(html, "</body>", tag + "$0", RegexOptions.IgnoreCase, System.TimeSpan.FromSeconds(1));
        }
    }
}
