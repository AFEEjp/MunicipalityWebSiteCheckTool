namespace MunicipalityWebSiteCheckTool.Domain;

public record FeedItem
{
    public required string ItemKey { get; init; }

    public string? Title { get; init; }

    public string? Url { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}
