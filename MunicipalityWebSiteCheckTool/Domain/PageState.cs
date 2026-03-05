namespace MunicipalityWebSiteCheckTool.Domain;

public record PageState
{
    public int Version { get; init; } = 1;

    public required string PageUrl { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public DateTimeOffset LastCheckedAt { get; init; }

    public string? ContentHash { get; init; }

    public string? Content { get; init; }

    public string? Title { get; init; }

    public HttpCacheInfo? TopPageHttpCache { get; init; }

    public HttpCacheInfo? ContentPageHttpCache { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? CircuitOpenUntil { get; init; }
}
