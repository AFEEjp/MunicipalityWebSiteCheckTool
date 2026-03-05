namespace MunicipalityWebSiteCheckTool.Config;

public record FeedConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    public required string Type { get; init; }

    public MatchConfig? Match { get; init; }

    public bool TemporaryDisabled { get; init; }

    public string? Cadence { get; init; }

    public string? WebhookKey { get; init; }

    public FeedStateConfig? State { get; init; }

    public BrowserFeedConfig? Browser { get; init; }
}

public record FeedStateConfig
{
    public int MaxSeen { get; init; } = 500;
}

public record FeedSettingsConfig
{
    public MatchConfig? DefaultMatch { get; init; }
}

public record BrowserFeedConfig
{
    public string WaitForSelector { get; init; } = "body";

    public string ItemSelector { get; init; } = "a";

    public string? TitleSelector { get; init; }

    public string LinkAttribute { get; init; } = "href";

    public string WaitUntil { get; init; } = "networkidle";

    public int TimeoutMs { get; init; } = 15000;
}
