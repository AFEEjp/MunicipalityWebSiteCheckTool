namespace MunicipalityWebSiteCheckTool.Config;

public record FeedConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    public required string Type { get; init; }

    public required MatchConfig Match { get; init; }

    public bool TemporaryDisabled { get; init; }

    public string? Cadence { get; init; }

    public string? WebhookKey { get; init; }

    public FeedStateConfig? State { get; init; }
}

public record FeedStateConfig
{
    public int MaxSeen { get; init; } = 500;
}
