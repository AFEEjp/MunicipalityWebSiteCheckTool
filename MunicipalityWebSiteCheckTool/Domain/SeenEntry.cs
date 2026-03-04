namespace MunicipalityWebSiteCheckTool.Domain;

public record SeenEntry
{
    public required string Key { get; init; }

    public string? Title { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }
}
