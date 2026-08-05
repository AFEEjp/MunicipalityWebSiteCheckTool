namespace MunicipalityWebSiteCheckTool.Domain;

public record UrlMigrationHint
{
    public required string Reason { get; init; }

    public required string Confidence { get; init; }

    public required string Fingerprint { get; init; }

    public string? CandidateUrl { get; init; }
}

public static class UrlMigrationReasons
{
    public const string HttpRedirect = "http-redirect";
    public const string MetaRefresh = "meta-refresh";
    public const string JavaScriptRedirect = "javascript-redirect";
    public const string MigrationLink = "migration-link";
    public const string MigrationTextOnly = "migration-text-only";
}

public static class UrlMigrationConfidences
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}
