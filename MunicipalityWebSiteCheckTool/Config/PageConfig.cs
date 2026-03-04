namespace MunicipalityWebSiteCheckTool.Config;

public record PageConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    public bool TemporaryDisabled { get; init; }

    public required string WebhookSecretKey { get; init; }

    public FollowLinkConfig? FollowLink { get; init; }

    public string? ContentSelector { get; init; }
}

public record FollowLinkConfig
{
    public required string TextMatch { get; init; }

    public string LinkSelector { get; init; } = "a";
}
