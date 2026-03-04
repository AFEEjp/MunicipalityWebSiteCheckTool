using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MunicipalityWebSiteCheckTool.Http;

namespace MunicipalityWebSiteCheckTool.Processing;

public sealed partial class TitleFetcher(FeedHttpClient feedHttpClient)
{
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".zip",
        ".doc",
        ".docx",
        ".xls",
        ".xlsx",
        ".ppt",
        ".pptx"
    };

    /// <summary>
    /// URL ごとのページタイトルをまとめて取得する。
    /// 取得件数と並列数を制限し、対象が多すぎる場合でも外部サイトへ過度な負荷を掛けないようにする。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> FetchTitlesAsync(
        IEnumerable<string> urls,
        int maxCount,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var targetUrls = urls
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maxCount))
            .ToArray();

        if (targetUrls.Length == 0)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        using var semaphore = new SemaphoreSlim(Math.Max(1, maxParallelism));
        var results = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var tasks = targetUrls.Select(async url =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var title = await FetchSingleTitleAsync(url, cancellationToken).ConfigureAwait(false);
                results[url] = title;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<string, string?>(results, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 単一 URL の HTML タイトルを取得する。
    /// HTML 以外のファイル拡張子は先に除外し、不要な HTTP アクセスを減らす。
    /// </summary>
    private async Task<string?> FetchSingleTitleAsync(string url, CancellationToken cancellationToken)
    {
        if (ShouldSkipByExtension(url))
        {
            return null;
        }

        try
        {
            var result = await feedHttpClient.FetchAsync(url, cache: null, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                return null;
            }

            return ExtractTitle(result.Content);
        }
        catch
        {
            // タイトル取得失敗は補助機能の失敗に留め、監視全体は継続させる。
            return null;
        }
    }

    /// <summary>
    /// URL の拡張子から、HTML ではない可能性が高いリソースを除外する。
    /// PDF や画像は title 抽出しても意味が薄いため、先に弾く。
    /// </summary>
    private static bool ShouldSkipByExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return !string.IsNullOrWhiteSpace(extension) && SkipExtensions.Contains(extension);
    }

    /// <summary>
    /// HTML 文字列から title 要素の内容だけを抜き出す。
    /// 取得できない場合は null を返し、後続でタイトル未取得として扱えるようにする。
    /// </summary>
    private static string? ExtractTitle(string content)
    {
        var match = TitleRegex().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var rawTitle = match.Groups["title"].Value;
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return null;
        }

        return string.Join(
            " ",
            rawTitle
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// title 要素を最短一致で抽出する正規表現を返す。
    /// head 全体を厳密に解析せず、タイトル取得に必要な最小限だけを読む。
    /// </summary>
    [GeneratedRegex(
        @"<title[^>]*>\s*(?<title>.*?)\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();
}
