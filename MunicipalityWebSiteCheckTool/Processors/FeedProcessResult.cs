namespace MunicipalityWebSiteCheckTool.Processors;

public sealed record FeedProcessResult
{
    public required string FeedId { get; init; }

    public bool Succeeded { get; init; }

    public bool SkippedByCircuitBreaker { get; init; }

    public int NewItemCount { get; init; }

    public int TitleChangedCount { get; init; }

    public string? ErrorMessage { get; init; }
}
