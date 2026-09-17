using System.Collections.Immutable;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Processing;

public static class SeenList
{
    public static SeenEntry? Find(ImmutableList<SeenEntry> seen, string key) =>
        seen.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static ImmutableList<SeenEntry> Add(
        ImmutableList<SeenEntry> seen,
        SeenEntry entry,
        int maxSeen)
    {
        var updated = seen.Add(entry);
        while (updated.Count > maxSeen)
        {
            updated = updated.RemoveAt(0);
        }

        return updated;
    }

    public static ImmutableList<SeenEntry> UpdateTitle(
        ImmutableList<SeenEntry> seen,
        string key,
        string? newTitle)
    {
        var index = seen.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? seen.SetItem(index, seen[index] with { Title = newTitle })
            : seen;
    }

    public static ImmutableList<SeenEntry> UpdateUrls(
        ImmutableList<SeenEntry> seen,
        string key,
        string? currentUrl)
    {
        var index = seen.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || string.IsNullOrWhiteSpace(currentUrl))
        {
            return seen;
        }

        var entry = seen[index];
        return seen.SetItem(index, entry with
        {
            FirstUrl = string.IsNullOrWhiteSpace(entry.FirstUrl) ? currentUrl : entry.FirstUrl,
            CurrentUrl = currentUrl
        });
    }
}
