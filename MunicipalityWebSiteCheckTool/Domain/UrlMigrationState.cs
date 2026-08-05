namespace MunicipalityWebSiteCheckTool.Domain;

public record UrlMigrationState
{
    public string? CurrentFingerprint { get; init; }

    public int ConsecutiveCount { get; init; }

    public string? LastReason { get; init; }

    public string? LastCandidateUrl { get; init; }

    public string? LastConfidence { get; init; }

    public string? LastNotifiedFingerprint { get; init; }

    public DateTimeOffset? LastNotifiedUtc { get; init; }
}
