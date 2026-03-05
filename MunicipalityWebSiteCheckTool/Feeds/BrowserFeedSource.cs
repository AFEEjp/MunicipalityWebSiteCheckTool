using AngleSharp.Html.Parser;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Processing;

namespace MunicipalityWebSiteCheckTool.Feeds;

public sealed class BrowserFeedSource : IFeedSource
{
    private static readonly HtmlParser Parser = new();

    public string Type => "browser";

    /// <summary>
    /// ブラウザで描画後に取得した HTML から、browser 設定のセレクタで項目を抽出する。
    /// RSS/HTML モードと同じ FeedItem 形式に揃えることで、後段処理を共通化する。
    /// </summary>
    public IReadOnlyList<FeedItem> ParseItems(FeedConfig config, string content, string requestUrl)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUrl);

        var browser = config.Browser ??
                      throw new InvalidOperationException($"browser 設定がありません。feedId={config.Id}");

        var itemSelector = string.IsNullOrWhiteSpace(browser.ItemSelector)
            ? "a"
            : browser.ItemSelector;

        var document = Parser.ParseDocument(content);
        var baseUri = CreateBaseUri(requestUrl);
        var results = new List<FeedItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var itemElement in document.QuerySelectorAll(itemSelector))
        {
            var rawUrl = ResolveRawUrl(itemElement, browser.LinkAttribute);
            var resolvedUrl = ResolveUrl(rawUrl, baseUri);
            var title = ResolveTitle(itemElement, browser.TitleSelector);
            var itemKey = BuildItemKey(resolvedUrl, title);
            if (itemKey is null || !seenKeys.Add(itemKey))
            {
                continue;
            }

            results.Add(new FeedItem
            {
                ItemKey = itemKey,
                Title = title,
                Url = resolvedUrl
            });
        }

        return results;
    }

    /// <summary>
    /// URL 属性名でリンクを抽出する。
    /// 指定属性が空の場合は href を既定値として扱う。
    /// </summary>
    private static string? ResolveRawUrl(AngleSharp.Dom.IElement element, string? linkAttribute)
    {
        var attributeName = string.IsNullOrWhiteSpace(linkAttribute)
            ? "href"
            : linkAttribute.Trim();

        return NormalizeText(element.GetAttribute(attributeName));
    }

    /// <summary>
    /// 項目タイトルを抽出する。
    /// titleSelector が指定されていればその要素から、未指定なら項目要素の textContent を使う。
    /// </summary>
    private static string? ResolveTitle(AngleSharp.Dom.IElement itemElement, string? titleSelector)
    {
        if (!string.IsNullOrWhiteSpace(titleSelector))
        {
            var titleElement = itemElement.QuerySelector(titleSelector);
            return NormalizeTitle(titleElement?.TextContent);
        }

        return NormalizeTitle(itemElement.TextContent);
    }

    /// <summary>
    /// URL があれば URL ベースで、無ければタイトルベースで項目キーを作る。
    /// どちらも無い要素は通知・重複判定に使えないため対象外にする。
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
    /// 相対 URL を基準 URL から絶対化する。
    /// 不正 URL はそのまま返し、後段のログ調査で原文を確認できるようにする。
    /// </summary>
    private static string? ResolveUrl(string? rawUrl, Uri? baseUri)
    {
        var normalized = NormalizeText(rawUrl);
        if (normalized is null)
        {
            return null;
        }

        if (Uri.TryCreate(normalized, UriKind.RelativeOrAbsolute, out var parsedUri) &&
            parsedUri.IsAbsoluteUri &&
            !string.Equals(parsedUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return parsedUri.ToString();
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, normalized, out var relativeUri))
        {
            return relativeUri.ToString();
        }

        return normalized;
    }

    /// <summary>
    /// 改行や連続空白を詰め、比較や通知で扱いやすい 1 行タイトルへ整形する。
    /// </summary>
    private static string? NormalizeTitle(string? value)
    {
        var normalized = NormalizeText(value);
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
    /// 空白だけの値を null に統一し、後段判定を単純化する。
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
    /// 相対 URL 解決用の基準 URI を生成する。
    /// URL として不正な場合は null を返し、元値を保持する方針にする。
    /// </summary>
    private static Uri? CreateBaseUri(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }
}
