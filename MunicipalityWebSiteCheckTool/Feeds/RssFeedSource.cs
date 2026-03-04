using System.Xml.Linq;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Processing;

namespace MunicipalityWebSiteCheckTool.Feeds;

public sealed class RssFeedSource : IFeedSource
{
    public string Type => "rss";

    /// <summary>
    /// RSS または Atom の XML 文字列を解析し、後続処理で使いやすい FeedItem に変換する。
    /// 形式が異なる 2 系統を同じ入口で扱えるよう、ルート要素を見て分岐する。
    /// </summary>
    public IReadOnlyList<FeedItem> ParseItems(FeedConfig config, string content, string requestUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUrl);

        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        var baseUri = CreateBaseUri(requestUrl);
        var rootName = document.Root?.Name.LocalName;

        if (string.Equals(rootName, "rss", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rootName, "rdf", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRssFamilyItems(document, baseUri);
        }

        if (string.Equals(rootName, "feed", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAtomItems(document, baseUri);
        }

        throw new InvalidOperationException($"未対応のフィード形式です。feedId={config.Id}, root={rootName ?? "(null)"}");
    }

    /// <summary>
    /// RSS 2.0 / RDF 系の item を列挙して FeedItem 化する。
    /// 監視で必要なのは項目一覧だけなので、チャネル単位の付帯情報はここでは扱わない。
    /// </summary>
    private static IReadOnlyList<FeedItem> ParseRssFamilyItems(XDocument document, Uri? baseUri)
    {
        var items = document
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase))
            .Select(item => CreateRssItem(item, baseUri))
            .Where(item => item is not null)
            .Cast<FeedItem>()
            .DistinctBy(item => item.ItemKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items;
    }

    /// <summary>
    /// Atom の entry を列挙して FeedItem 化する。
    /// Atom は名前空間付き要素が多いため、LocalName ベースで拾って形式差を吸収する。
    /// </summary>
    private static IReadOnlyList<FeedItem> ParseAtomItems(XDocument document, Uri? baseUri)
    {
        var items = document
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "entry", StringComparison.OrdinalIgnoreCase))
            .Select(entry => CreateAtomItem(entry, baseUri))
            .Where(item => item is not null)
            .Cast<FeedItem>()
            .DistinctBy(item => item.ItemKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items;
    }

    /// <summary>
    /// RSS 系 item から URL・タイトル・公開日時を抽出する。
    /// URL が取得できないケースもあるため、guid やタイトルを併用して安定した項目キーを作る。
    /// </summary>
    private static FeedItem? CreateRssItem(XElement itemElement, Uri? baseUri)
    {
        var title = NormalizeTitle(GetElementValue(itemElement, "title"));
        var link = ResolveUrl(GetElementValue(itemElement, "link"), baseUri);
        var guid = NormalizeText(GetElementValue(itemElement, "guid"));
        var publishedAt =
            TryParseDate(GetElementValue(itemElement, "pubDate")) ??
            TryParseDate(GetElementValue(itemElement, "date")) ??
            TryParseDate(GetElementValue(itemElement, "published")) ??
            TryParseDate(GetElementValue(itemElement, "updated"));

        var itemKey = BuildItemKey(link, guid, title, publishedAt);
        if (itemKey is null)
        {
            // 監視対象として識別できない項目は、重複判定ができないため捨てる。
            return null;
        }

        return new FeedItem
        {
            ItemKey = itemKey,
            Title = title,
            Url = link,
            PublishedAt = publishedAt
        };
    }

    /// <summary>
    /// Atom entry から URL・タイトル・公開日時を抽出する。
    /// link 要素は複数あるため、まず alternate を優先し、なければ最初の href を使う。
    /// </summary>
    private static FeedItem? CreateAtomItem(XElement entryElement, Uri? baseUri)
    {
        var title = NormalizeTitle(GetElementValue(entryElement, "title"));
        var link = ResolveUrl(GetAtomLink(entryElement), baseUri);
        var atomId = NormalizeText(GetElementValue(entryElement, "id"));
        var publishedAt =
            TryParseDate(GetElementValue(entryElement, "updated")) ??
            TryParseDate(GetElementValue(entryElement, "published"));

        var itemKey = BuildItemKey(link, atomId, title, publishedAt);
        if (itemKey is null)
        {
            // Atom でも識別子が作れない項目は安全に追跡できないため無視する。
            return null;
        }

        return new FeedItem
        {
            ItemKey = itemKey,
            Title = title,
            Url = link,
            PublishedAt = publishedAt
        };
    }

    /// <summary>
    /// 同一項目を安定して追跡できるよう、URL を最優先に項目キーを組み立てる。
    /// URL が無い場合も、guid/id とタイトル等で代替キーを作り、URL 欠落フィードに対応する。
    /// </summary>
    private static string? BuildItemKey(string? url, string? alternateId, string? title, DateTimeOffset? publishedAt)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            return UrlNormalizer.ToItemKey(url);
        }

        var seedParts = new[]
        {
            NormalizeText(alternateId),
            NormalizeText(title),
            publishedAt?.UtcDateTime.ToString("O")
        };

        var seed = string.Join("|", seedParts.Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        return UrlNormalizer.ToItemKey(seed);
    }

    /// <summary>
    /// Atom の link 要素群から通知に使う代表 URL を選ぶ。
    /// rel="alternate" を優先し、無ければ最初の href を使う。
    /// </summary>
    private static string? GetAtomLink(XElement entryElement)
    {
        var links = entryElement
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "link", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (links.Length == 0)
        {
            return null;
        }

        var alternate = links.FirstOrDefault(link =>
            string.Equals(
                NormalizeText(link.Attribute("rel")?.Value),
                "alternate",
                StringComparison.OrdinalIgnoreCase));

        return NormalizeText(alternate?.Attribute("href")?.Value) ??
               NormalizeText(links[0].Attribute("href")?.Value);
    }

    /// <summary>
    /// 要素名の大小文字や名前空間差を吸収して、最初に見つかった値を返す。
    /// RSS と Atom の混在実装を単純化するため、LocalName ベースで検索している。
    /// </summary>
    private static string? GetElementValue(XElement parent, string localName)
    {
        var element = parent
            .Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

        return NormalizeText(element?.Value);
    }

    /// <summary>
    /// 相対 URL が返るフィードに対応するため、取得元 URL を基準に絶対 URL を組み立てる。
    /// URL として解釈できない値は、そのまま後段で扱えるよう元文字列を返す。
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
    /// タイトルの改行や余分な空白を 1 つに潰し、比較や通知で扱いやすくする。
    /// フィード提供元ごとの整形差分で誤検知しにくくする意図がある。
    /// </summary>
    private static string? NormalizeTitle(string? title)
    {
        var normalized = NormalizeText(title);
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
    /// XML 由来の文字列をトリムし、空文字だけの値は null 扱いに統一する。
    /// 後続で空文字と null を別扱いしないよう、ここで正規化しておく。
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
    /// RSS/Atom で一般的に使われる日付文字列を DateTimeOffset へ変換する。
    /// 解釈できない値は例外にせず null とし、監視全体を止めない方針にする。
    /// </summary>
    private static DateTimeOffset? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 取得元 URL を絶対 URL として解釈できる場合だけ基準 URI を作る。
    /// 基準 URI が作れない場合は、相対リンク解決を無効化して安全側で処理する。
    /// </summary>
    private static Uri? CreateBaseUri(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }
}
