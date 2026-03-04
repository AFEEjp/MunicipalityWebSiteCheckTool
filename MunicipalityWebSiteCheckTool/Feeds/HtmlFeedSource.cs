using AngleSharp.Html.Parser;
using System.Text.RegularExpressions;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Processing;

namespace MunicipalityWebSiteCheckTool.Feeds;

public sealed partial class HtmlFeedSource : IFeedSource
{
    private static readonly HtmlParser Parser = new();

    public string Type => "html";

    /// <summary>
    /// HTML 文書からリンク一覧を抽出し、監視対象の FeedItem に変換する。
    /// AngleSharp で DOM として解釈し、リンク抽出の取りこぼしを減らす。
    /// </summary>
    public IReadOnlyList<FeedItem> ParseItems(FeedConfig config, string content, string requestUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUrl);

        var document = Parser.ParseDocument(content);
        var baseUri = CreateBaseUri(requestUrl);
        var results = new List<FeedItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in document.QuerySelectorAll("a"))
        {
            var linkUrl = TryResolveLinkUrl(anchor.GetAttribute("href"), anchor.GetAttribute("onclick"), baseUri);

            // mailto は監視対象ページではなく通知にも使えないため除外する。
            if (linkUrl is not null && linkUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = NormalizeTitle(anchor.TextContent);
            var itemKey = BuildItemKey(linkUrl, title);
            if (itemKey is null || !seenKeys.Add(itemKey))
            {
                // 同一ページ内の重複リンクや、識別子が作れないリンクは監視対象にしない。
                continue;
            }

            results.Add(new FeedItem
            {
                ItemKey = itemKey,
                Title = title,
                Url = linkUrl
            });
        }

        return results;
    }

    /// <summary>
    /// a タグの属性から、通知や重複判定に使う実 URL を決定する。
    /// href を優先し、JavaScript 疑似リンクしか無い場合は onclick から URL を復元する。
    /// </summary>
    private static string? TryResolveLinkUrl(string? href, string? onclick, Uri? baseUri)
    {
        if (!string.IsNullOrWhiteSpace(href) &&
            !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveUrl(href, baseUri);
        }

        if (string.IsNullOrWhiteSpace(onclick))
        {
            return ResolveUrl(href, baseUri);
        }

        var scriptUrl = ExtractUrlFromOnclick(onclick);
        return ResolveUrl(scriptUrl, baseUri);
    }

    /// <summary>
    /// onclick に埋め込まれた URL 文字列を取り出す。
    /// e-Gov を含む JavaScript 疑似リンク対策として、クォートされた URL 断片を拾う。
    /// </summary>
    private static string? ExtractUrlFromOnclick(string onclick)
    {
        var match = OnclickUrlRegex().Match(onclick);
        if (!match.Success)
        {
            return null;
        }

        return NormalizeText(match.Groups["url"].Value);
    }

    /// <summary>
    /// リンク URL が取れれば URL を優先して項目キーを作る。
    /// URL が無い HTML リンクでも、タイトルだけは残して重複判定できるようにする。
    /// </summary>
    private static string? BuildItemKey(string? url, string? title)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            return UrlNormalizer.ToItemKey(url);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return UrlNormalizer.ToItemKey(title);
    }

    /// <summary>
    /// 相対 URL を絶対 URL に解決する。
    /// URL として不正な値でも、後段でログに残せるよう元文字列は維持する。
    /// </summary>
    private static string? ResolveUrl(string? rawUrl, Uri? baseUri)
    {
        var normalized = NormalizeText(rawUrl);
        if (normalized is null)
        {
            return null;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, normalized, out var relativeUri))
        {
            return relativeUri.ToString();
        }

        return normalized;
    }

    /// <summary>
    /// a タグの表示文字列を比較しやすい形へ正規化する。
    /// AngleSharp 側でテキスト化された値を受け取り、空白だけ整える。
    /// </summary>
    private static string? NormalizeTitle(string? textContent)
    {
        var normalized = NormalizeText(textContent);
        if (normalized is null)
        {
            return null;
        }

        return string.Join(
            " ",
            normalized
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// 空白だけの文字列を null とみなし、以後の比較条件を単純化する。
    /// どの抽出経路から来た値でも同じ正規化を通すための共通入口。
    /// </summary>
    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// 取得元 URL を基準 URI として使える場合だけ作成する。
    /// 基準 URI が作れない場合は、相対リンク解決を諦めて元値を返す方針にする。
    /// </summary>
    private static Uri? CreateBaseUri(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    /// <summary>
    /// onclick から URL らしい文字列を拾うための正規表現を返す。
    /// 絶対 URL とルート相対 URL を対象にし、JavaScript 全体の解釈は行わない。
    /// </summary>
    [GeneratedRegex(
        @"['""](?<url>(?:https?://|/)[^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OnclickUrlRegex();

}
