namespace MunicipalityWebSiteCheckTool.Configuration;

public sealed record RunOptions
{
    public required string Mode { get; init; }

    public string? Cadence { get; init; }

    public required string FeedSettingsPath { get; init; }

    public required string FeedsDirectory { get; init; }

    public required string PagesDirectory { get; init; }

    public required string StateDirectory { get; init; }

    public bool DryRun { get; init; }
}
