namespace MunicipalityWebSiteCheckTool.Domain;

public record HttpCacheInfo
{
    public string? ETag { get; init; }

    public DateTime? LastModifiedUtc { get; init; }
}
