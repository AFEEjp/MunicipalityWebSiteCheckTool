using MunicipalityWebSiteCheckTool.Config;

namespace MunicipalityWebSiteCheckTool.Processing;

public static class KeywordMatcher
{
    public static bool IsMatch(string text, MatchConfig match)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(match);

        return (!match.First.Any() || match.First.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
               && (!match.Second.Any() || match.Second.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
               && !match.Exclude.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> DetectKeywords(string text, MatchConfig match)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(match);

        return match.First
            .Concat(match.Second)
            .Where(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
