using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchlists.Api
{
    /// <summary>Accepte les GUID au format Jellyfin "N" (sans tirets) comme au format "D".</summary>
    public class LenientGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            return Guid.TryParse(s, out var g) ? g : throw new JsonException("GUID invalide : " + s);
        }

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("D"));
    }
}
