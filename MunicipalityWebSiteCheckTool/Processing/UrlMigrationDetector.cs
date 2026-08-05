using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Processing;

public static partial class UrlMigrationDetector
{
    private static readonly HtmlParser HtmlParser = new();
    private static readonly string[] MigrationKeywords =
    [
        "移転",
        "移行",
        "新サイト",
        "ページは移動",
        "ページを移動",
        "ページが移動",
        "URLをお気に入り登録",
        "再度お気に入り登録",
        "閉鎖します",
        "切り替わります"
    ];

    public static UrlMigrationHint? Detect(string? content, string inspectedUrl)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(inspectedUrl) || !LooksLikeHtml(content))
        {
            return null;
        }

        var document = HtmlParser.ParseDocument(content);
        var baseUri = CreateBaseUri(inspectedUrl);

        return DetectMetaRefresh(document, baseUri)
               ?? DetectJavaScriptRedirect(document, baseUri)
               ?? DetectMigrationLink(document, baseUri)
               ?? DetectMigrationTextOnly(document);
    }

    private static UrlMigrationHint? DetectMetaRefresh(IParentNode document, Uri? baseUri)
    {
        var refreshMeta = document.QuerySelectorAll("meta[http-equiv]")
            .OfType<IElement>()
            .FirstOrDefault(static meta =>
                string.Equals(meta.GetAttribute("http-equiv")?.Trim(), "refresh", StringComparison.OrdinalIgnoreCase));
        if (refreshMeta is null)
        {
            return null;
        }

        var content = refreshMeta.GetAttribute("content");
        var match = MetaRefreshUrlRegex().Match(content ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var candidateUrl = ResolveUrl(match.Groups["url"].Value, baseUri);
        if (candidateUrl is null)
        {
            return null;
        }

        return new UrlMigrationHint
        {
            Reason = UrlMigrationReasons.MetaRefresh,
            Confidence = UrlMigrationConfidences.High,
            CandidateUrl = candidateUrl,
            Fingerprint = BuildFingerprint(UrlMigrationReasons.MetaRefresh, candidateUrl, null)
        };
    }

    private static UrlMigrationHint? DetectJavaScriptRedirect(IParentNode document, Uri? baseUri)
    {
        foreach (var script in document.QuerySelectorAll("script"))
        {
            var scriptContent = script.TextContent;
            if (string.IsNullOrWhiteSpace(scriptContent))
            {
                continue;
            }

            var match = JavaScriptRedirectRegex().Match(scriptContent);
            if (!match.Success)
            {
                continue;
            }

            var candidateUrl = ResolveUrl(match.Groups["url"].Value, baseUri);
            if (candidateUrl is null)
            {
                continue;
            }

            return new UrlMigrationHint
            {
                Reason = UrlMigrationReasons.JavaScriptRedirect,
                Confidence = UrlMigrationConfidences.High,
                CandidateUrl = candidateUrl,
                Fingerprint = BuildFingerprint(UrlMigrationReasons.JavaScriptRedirect, candidateUrl, null)
            };
        }

        return null;
    }

    private static UrlMigrationHint? DetectMigrationLink(IParentNode document, Uri? baseUri)
    {
        var root = document.QuerySelector("main") ??
                   document.QuerySelector("article") ??
                   document.QuerySelector("body");
        if (root is null)
        {
            return null;
        }

        foreach (var element in root.QuerySelectorAll("p, div, section, article, li, h1, h2, h3"))
        {
            var text = NormalizeText(element.TextContent);
            if (!ContainsMigrationKeyword(text))
            {
                continue;
            }

            var candidateUrls = element.QuerySelectorAll("a[href]")
                .Select(anchor => ResolveUrl(anchor.GetAttribute("href"), baseUri))
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Select(static url => url!)
                .Where(static url => !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                .Where(static url => !url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidateUrls.Length != 1)
            {
                continue;
            }

            return new UrlMigrationHint
            {
                Reason = UrlMigrationReasons.MigrationLink,
                Confidence = UrlMigrationConfidences.Medium,
                CandidateUrl = candidateUrls[0],
                Fingerprint = BuildFingerprint(UrlMigrationReasons.MigrationLink, candidateUrls[0], null)
            };
        }

        return null;
    }

    private static UrlMigrationHint? DetectMigrationTextOnly(IParentNode document)
    {
        var root = document.QuerySelector("main") ??
                   document.QuerySelector("article") ??
                   document.QuerySelector("body");
        if (root is null)
        {
            return null;
        }

        var title = NormalizeText(document.QuerySelector("title")?.TextContent);
        var bodyText = NormalizeText(root.TextContent);
        if (!ContainsMigrationKeyword(title) && !ContainsMigrationKeyword(bodyText))
        {
            return null;
        }

        var evidence = title ?? bodyText;
        return new UrlMigrationHint
        {
            Reason = UrlMigrationReasons.MigrationTextOnly,
            Confidence = UrlMigrationConfidences.Low,
            Fingerprint = BuildFingerprint(UrlMigrationReasons.MigrationTextOnly, null, evidence)
        };
    }

    private static bool LooksLikeHtml(string content)
    {
        return content.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<head", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<meta", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<title", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMigrationKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return MigrationKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveUrl(string? rawUrl, Uri? baseUri)
    {
        var normalized = NormalizeText(rawUrl);
        if (normalized is null)
        {
            return null;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri) &&
            !string.Equals(absoluteUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return absoluteUri.ToString();
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, normalized, out var relativeUri))
        {
            return relativeUri.ToString();
        }

        return normalized;
    }

    private static Uri? CreateBaseUri(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildFingerprint(string reason, string? candidateUrl, string? evidence)
    {
        var raw = candidateUrl ?? evidence ?? string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"{reason}|{Convert.ToHexStringLower(bytes)}";
    }

    [GeneratedRegex(
        @"^\s*\d+\s*;\s*url\s*=\s*['""]?(?<url>[^'""]+)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaRefreshUrlRegex();

    [GeneratedRegex(
        @"(?:(?:window|self|top)\.)?location(?:\.href)?\s*=\s*['""](?<url>[^'""]+)['""]|(?:(?:window|self|top)\.)?location\.(?:replace|assign)\(\s*['""](?<url>[^'""]+)['""]\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptRedirectRegex();
}
