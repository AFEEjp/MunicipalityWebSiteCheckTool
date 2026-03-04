namespace MunicipalityWebSiteCheckTool.Config;

public record MatchConfig
{
    public string[] First { get; init; } = [];

    public string[] Second { get; init; } = [];

    public string[] Exclude { get; init; } = [];
}
