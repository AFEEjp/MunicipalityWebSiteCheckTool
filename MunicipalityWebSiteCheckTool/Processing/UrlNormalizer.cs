using System.Security.Cryptography;
using System.Text;

namespace MunicipalityWebSiteCheckTool.Processing;

public static class UrlNormalizer
{
    public static string NormalizeForItemKey(string url) =>
        url.Replace("?ref=rss", "", StringComparison.OrdinalIgnoreCase)
           .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
           .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
           .Replace("//", "", StringComparison.Ordinal);

    public static string ToItemKey(string url)
    {
        var normalized = NormalizeForItemKey(url);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
