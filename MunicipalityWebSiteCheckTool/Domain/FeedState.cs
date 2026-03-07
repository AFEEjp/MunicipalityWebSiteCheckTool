using System.Collections.Immutable;
using MunicipalityWebSiteCheckTool.Config;

namespace MunicipalityWebSiteCheckTool.Domain;

public record FeedState
{
    public int Version { get; init; } = 1;

    public required string FeedUrl { get; init; }

    public required string FeedType { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public int MaxSeen { get; init; } = 500;

    public ImmutableList<SeenEntry> Seen { get; init; } = ImmutableList<SeenEntry>.Empty;

    public HttpCacheInfo? HttpCache { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? CircuitOpenUntil { get; init; }

    public int RssHtmlMismatchCount { get; init; }

    public int RssHtmlLastNotifiedCount { get; init; }

    public DateTimeOffset? RssHtmlLastNotifiedUtc { get; init; }

    public int ConsecutiveFailureLastNotifiedCount { get; init; }

    public DateTimeOffset? ConsecutiveFailureLastNotifiedUtc { get; init; }

    public static FeedState CreateNew(FeedConfig config) => new()
    {
        FeedUrl = config.Url,
        FeedType = config.Type,
        UpdatedUtc = DateTimeOffset.UtcNow,
        MaxSeen = config.State?.MaxSeen ?? 500
    };
}
