using System.Text.Json.Serialization;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Serialization;

[JsonSerializable(typeof(FeedConfig))]
[JsonSerializable(typeof(FeedSettingsConfig))]
[JsonSerializable(typeof(FeedState))]
[JsonSerializable(typeof(PageConfig))]
[JsonSerializable(typeof(PageState))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext
{
}
