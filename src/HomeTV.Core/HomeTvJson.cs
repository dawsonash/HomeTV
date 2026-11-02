using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeTV.Core;

/// <summary>JSON settings shared by the HTTP API, WebSockets and config files: camelCase names and enum values.</summary>
public static class HomeTvJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
