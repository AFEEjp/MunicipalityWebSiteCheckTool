namespace MunicipalityWebSiteCheckTool.Domain;

public record FetchResult
{
    public string? Content { get; init; }

    public required string FinalUrl { get; init; }

    public required HttpCacheInfo NewCache { get; init; }

    public UrlMigrationHint? UrlMigrationHint { get; init; }

    public bool IsNotModified => Content is null;
}
