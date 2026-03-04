namespace MunicipalityWebSiteCheckTool.Processors;

public sealed record PageProcessResult
{
    public required string PageId { get; init; }

    public bool Succeeded { get; init; }

    public bool SkippedByCircuitBreaker { get; init; }

    public bool Changed { get; init; }

    public string? ErrorMessage { get; init; }
}
