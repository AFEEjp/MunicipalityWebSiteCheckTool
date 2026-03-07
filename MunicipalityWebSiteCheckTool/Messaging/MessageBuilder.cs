using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Text;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Messaging;

public sealed class MessageBuilder
{
    private const int DefaultChunkLength = 1000;
    private const int DiffLineLimit = 80;

    /// <summary>
    /// 新規検出した監視項目の通知文を組み立てる。
    /// 1 件の通知でも Discord 制限を超える可能性があるため、最後に分割して返す。
    /// </summary>
    public IReadOnlyList<string> BuildNewItemMessages(
        string sourceName,
        FeedItem item,
        IEnumerable<string>? matchedKeywords = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(item);

        var builder = new StringBuilder();
        builder.AppendLine($"[新規検出] {sourceName}");

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            builder.AppendLine($"件名: {item.Title}");
        }

        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            builder.AppendLine($"URL: {item.Url}");
        }

        if (item.PublishedAt is not null)
        {
            builder.AppendLine($"公開日時: {item.PublishedAt.Value:yyyy-MM-dd HH:mm:ss zzz}");
        }

        var keywords = matchedKeywords?
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keywords is { Length: > 0 })
        {
            builder.AppendLine($"一致キーワード: {string.Join(", ", keywords)}");
        }

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// タイトル変更時の通知文を組み立てる。
    /// 旧タイトルと新タイトルを並べ、監視対象が何に変わったかをすぐ確認できるようにする。
    /// </summary>
    public IReadOnlyList<string> BuildTitleChangedMessages(
        string sourceName,
        string itemUrl,
        string? oldTitle,
        string? newTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemUrl);

        var builder = new StringBuilder();
        builder.AppendLine($"[タイトル変更] {sourceName}");
        builder.AppendLine($"URL: {itemUrl}");
        builder.AppendLine($"変更前: {NormalizeLine(oldTitle) ?? "(なし)"}");
        builder.AppendLine($"変更後: {NormalizeLine(newTitle) ?? "(なし)"}");

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// ページ本文の差分通知文を組み立てる。
    /// DiffPlex で差分行を作り、Discord の diff コードブロックで見やすく通知する。
    /// </summary>
    public IReadOnlyList<string> BuildPageChangedMessages(
        string pageName,
        string pageUrl,
        string? previousContent,
        string? currentContent,
        string? previousTitle = null,
        string? currentTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);

        var builder = new StringBuilder();
        builder.AppendLine($"[ページ更新] {pageName}");
        builder.AppendLine($"URL: {pageUrl}");
        builder.AppendLine($"変更前タイトル: {NormalizeLine(previousTitle) ?? "(なし)"}");
        builder.AppendLine($"変更後タイトル: {NormalizeLine(currentTitle) ?? "(なし)"}");

        var diffLines = BuildDiffLines(previousContent, currentContent);
        if (diffLines.Count == 0)
        {
            builder.AppendLine("差分表示: 変更行を抽出できませんでした。");
            return SplitMessage(builder.ToString());
        }

        builder.AppendLine("差分表示:");
        return BuildDiffMessages(builder.ToString().TrimEnd(), diffLines);
    }

    /// <summary>
    /// 監視 URL の変更検知メッセージを組み立てる。
    /// feed/page どちらでも使えるよう、設定種別に依存しない中立文面にしている。
    /// </summary>
    public IReadOnlyList<string> BuildUrlChangedMessages(
        string targetName,
        string registeredUrl,
        string actualUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUrl);

        var builder = new StringBuilder();
        builder.AppendLine($"[URL変更検知] {targetName}");
        builder.AppendLine($"設定URL: {registeredUrl}");
        builder.AppendLine($"実際の取得先: {actualUrl}");
        builder.AppendLine("リダイレクトやサイト移転の可能性があります。設定値の見直しを検討してください。");

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// エラー要約通知を組み立てる。
    /// 複数件の失敗をまとめて送り、エラー通知チャンネルのノイズを抑える。
    /// </summary>
    public IReadOnlyList<string> BuildErrorSummaryMessages(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var normalizedErrors = errors
            .Where(static error => !string.IsNullOrWhiteSpace(error))
            .Select(static error => NormalizeLine(error)!)
            .ToArray();

        if (normalizedErrors.Length == 0)
        {
            return [];
        }

        var builder = new StringBuilder();
        builder.AppendLine("[エラー要約]");
        foreach (var error in normalizedErrors)
        {
            builder.AppendLine($"- {error}");
        }

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// RSS 想定 URL で HTML 応答が続く異常を通知する。
    /// 単発ではなく連続回数がしきい値を超えたときにだけ呼ばれる想定。
    /// </summary>
    public IReadOnlyList<string> BuildRssHtmlMismatchMessages(
        string feedName,
        string feedUrl,
        int mismatchCount,
        int notifyThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);

        var builder = new StringBuilder();
        builder.AppendLine("[RSS取得異常]");
        builder.AppendLine($"対象: {feedName}");
        builder.AppendLine($"URL: {feedUrl}");
        builder.AppendLine($"内容: RSS想定URLからHTML応答を検出");
        builder.AppendLine($"連続検知回数: {mismatchCount} 回");
        builder.AppendLine($"通知しきい値: {notifyThreshold} 回");
        builder.AppendLine("サーバー側の一時障害、メンテナンス画面、WAF/CDN応答置換の可能性があります。");

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// 通信系の連続失敗（timeout / 4xx / 5xx など）をしきい値到達時に通知する。
    /// 単発障害では通知せず、運用対応が必要な継続障害のみを通知対象にする。
    /// </summary>
    public IReadOnlyList<string> BuildFeedConsecutiveFailureMessages(
        string feedName,
        string feedId,
        string feedUrl,
        int failureCount,
        int notifyThreshold,
        string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var builder = new StringBuilder();
        builder.AppendLine("[フィード取得連続失敗]");
        builder.AppendLine($"対象: {feedName} ({feedId})");
        builder.AppendLine($"URL: {feedUrl}");
        builder.AppendLine($"連続失敗回数: {failureCount} 回");
        builder.AppendLine($"通知しきい値: {notifyThreshold} 回");
        builder.AppendLine($"最新エラー: {NormalizeLine(errorMessage) ?? errorMessage}");

        return SplitMessage(builder.ToString());
    }

    /// <summary>
    /// Discord 送信しやすいよう、長文を一定文字数ごとに分割する。
    /// 改行を優先しつつ、長すぎる 1 行だけは強制分割する。
    /// </summary>
    public IReadOnlyList<string> SplitMessage(string message, int maxLength = DefaultChunkLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var safeMaxLength = Math.Max(1, maxLength);
        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var results = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            AppendLineWithSplit(results, current, line, safeMaxLength);
        }

        FlushCurrent(results, current);
        return results;
    }

    /// <summary>
    /// DiffPlex で本文差分を計算し、Discord の diff 表示向けに行頭記号付きで整形する。
    /// 変更が多すぎると通知が読みにくくなるため、行数に上限を掛ける。
    /// </summary>
    private static IReadOnlyList<string> BuildDiffLines(string? previousContent, string? currentContent)
    {
        var diffBuilder = new InlineDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(previousContent ?? string.Empty, currentContent ?? string.Empty);
        var results = new List<string>();

        foreach (var line in diff.Lines)
        {
            var renderedLine = RenderDiffLine(line);
            if (renderedLine is null)
            {
                continue;
            }

            results.Add(renderedLine);
            if (results.Count >= DiffLineLimit)
            {
                results.Add("! 差分行が多いため途中で省略しました。");
                return results;
            }
        }

        return results;
    }

    /// <summary>
    /// DiffPlex の 1 行を Discord の diff 表示向けテキストに変換する。
    /// 未変更行は通知量削減のため省き、変更行だけを残す。
    /// </summary>
    private static string? RenderDiffLine(DiffPiece line)
    {
        var text = TrimForMessage(line.Text ?? string.Empty);

        return line.Type switch
        {
            ChangeType.Inserted => $"+ {text}",
            ChangeType.Deleted => $"- {text}",
            ChangeType.Modified => $"! {text}",
            _ => null
        };
    }

    /// <summary>
    /// 1 行の内容を通知文向けに短く整形する。
    /// 異常に長い行は切り詰めて、1 メッセージの肥大化を防ぐ。
    /// </summary>
    private static string TrimForMessage(string line)
    {
        const int maxLineLength = 120;

        var normalized = NormalizeLine(line) ?? string.Empty;
        if (normalized.Length <= maxLineLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLineLength]}...";
    }

    /// <summary>
    /// 空白を詰めて 1 行テキストへ正規化する。
    /// 改行やタブの揺れで通知文が読みにくくならないようにする。
    /// </summary>
    private static string? NormalizeLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// 1 行を現在のバッファへ追加し、長すぎる場合は安全に分割する。
    /// 行単位をなるべく維持し、必要なときだけ文字数で強制分割する。
    /// </summary>
    private static void AppendLineWithSplit(
        List<string> results,
        StringBuilder current,
        string line,
        int maxLength)
    {
        var remaining = line;

        while (remaining.Length > 0)
        {
            var prefix = current.Length == 0 ? string.Empty : "\n";
            var available = maxLength - current.Length - prefix.Length;

            if (available <= 0)
            {
                FlushCurrent(results, current);
                continue;
            }

            if (remaining.Length <= available)
            {
                if (prefix.Length > 0)
                {
                    current.Append(prefix);
                }

                current.Append(remaining);
                return;
            }

            if (prefix.Length > 0)
            {
                current.Append(prefix);
            }

            current.Append(remaining[..available]);
            remaining = remaining[available..];
            FlushCurrent(results, current);
        }

        if (line.Length == 0)
        {
            if (current.Length + 1 > maxLength)
            {
                FlushCurrent(results, current);
            }
            else if (current.Length > 0)
            {
                current.Append('\n');
            }
        }
    }

    /// <summary>
    /// バッファに溜めたメッセージを結果へ確定する。
    /// 空バッファは何もせず、不要な空メッセージ生成を避ける。
    /// </summary>
    private static void FlushCurrent(List<string> results, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        results.Add(current.ToString());
        current.Clear();
    }

    /// <summary>
    /// 差分コードブロックを Discord 向けに分割して返す。
    /// 各チャンクごとにコードフェンスを閉じることで、分割時も diff 表示を維持する。
    /// </summary>
    private static IReadOnlyList<string> BuildDiffMessages(string header, IReadOnlyList<string> diffLines)
    {
        var results = new List<string>();
        var current = new StringBuilder();

        void StartChunk()
        {
            current.Clear();
            current.AppendLine(header);
            current.AppendLine("```diff");
        }

        void FlushChunk()
        {
            if (current.Length == 0)
            {
                return;
            }

            current.Append("```");
            results.Add(current.ToString());
            current.Clear();
        }

        StartChunk();

        foreach (var diffLine in diffLines)
        {
            var candidate = $"{diffLine}\n";
            var closingFenceLength = 3;

            if (current.Length + candidate.Length + closingFenceLength > DefaultChunkLength)
            {
                FlushChunk();
                StartChunk();
            }

            current.Append(candidate);
        }

        FlushChunk();
        return results;
    }
}
