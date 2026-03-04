using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Http;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.State;

namespace MunicipalityWebSiteCheckTool.Processors;

public sealed class PageProcessor
{
    private const int CircuitOpenThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(30);
    private static readonly HtmlParser HtmlParser = new();

    private readonly IFeedHttpClient _feedHttpClient;
    private readonly StateStore _stateStore;
    private readonly MessageBuilder _messageBuilder;
    private readonly DiscordNotifier _discordNotifier;

    public PageProcessor(
        IFeedHttpClient feedHttpClient,
        StateStore stateStore,
        MessageBuilder messageBuilder,
        DiscordNotifier discordNotifier)
    {
        ArgumentNullException.ThrowIfNull(feedHttpClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(messageBuilder);
        ArgumentNullException.ThrowIfNull(discordNotifier);

        _feedHttpClient = feedHttpClient;
        _stateStore = stateStore;
        _messageBuilder = messageBuilder;
        _discordNotifier = discordNotifier;
    }

    /// <summary>
    /// 単一ページの差分監視を実行する。
    /// トップページ取得、必要なら followLink 解決、本文比較、通知、state 更新までを一括で処理する。
    /// </summary>
    public async Task<PageProcessResult> ProcessAsync(
        PageConfig config,
        string errorWebhookUrl,
        Func<string, string> resolveWebhookUrl,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorWebhookUrl);
        ArgumentNullException.ThrowIfNull(resolveWebhookUrl);

        var state = await _stateStore.LoadPageAsync(config.Id, cancellationToken).ConfigureAwait(false) ??
                    CreateInitialState(config);

        if (IsCircuitOpen(state))
        {
            return new PageProcessResult
            {
                PageId = config.Id,
                Succeeded = true,
                SkippedByCircuitBreaker = true
            };
        }

        try
        {
            var topFetch = await _feedHttpClient.FetchAsync(config.Url, state.TopPageHttpCache, cancellationToken).ConfigureAwait(false);
            await NotifyUrlChangedAsync(config.Name, config.Url, topFetch.FinalUrl, errorWebhookUrl, dryRun, cancellationToken).ConfigureAwait(false);

            var topState = state with
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                LastCheckedAt = DateTimeOffset.UtcNow,
                TopPageHttpCache = topFetch.NewCache,
                ConsecutiveFailures = 0,
                CircuitOpenUntil = null
            };

            var contentFetchPlan = await ResolveContentFetchAsync(config, topState, topFetch, cancellationToken).ConfigureAwait(false);
            var nextState = topState with
            {
                PageUrl = contentFetchPlan.PageUrl,
                ContentPageHttpCache = contentFetchPlan.FetchResult?.NewCache ?? topState.ContentPageHttpCache
            };

            if (contentFetchPlan.FetchResult is null)
            {
                await _stateStore.SavePageAsync(config.Id, nextState, cancellationToken).ConfigureAwait(false);
                return new PageProcessResult
                {
                    PageId = config.Id,
                    Succeeded = true
                };
            }

            await NotifyUrlChangedAsync(config.Name, contentFetchPlan.PageUrl, contentFetchPlan.FetchResult.FinalUrl, errorWebhookUrl, dryRun, cancellationToken).ConfigureAwait(false);
            nextState = nextState with
            {
                PageUrl = contentFetchPlan.FetchResult.FinalUrl,
                ContentPageHttpCache = contentFetchPlan.FetchResult.NewCache
            };

            if (contentFetchPlan.FetchResult.IsNotModified)
            {
                await _stateStore.SavePageAsync(config.Id, nextState, cancellationToken).ConfigureAwait(false);
                return new PageProcessResult
                {
                    PageId = config.Id,
                    Succeeded = true
                };
            }

            var extractedContent = ExtractTargetContent(contentFetchPlan.FetchResult.Content!, config.ContentSelector);
            var contentHash = ComputeHash(extractedContent);

            if (string.Equals(nextState.ContentHash, contentHash, StringComparison.Ordinal))
            {
                nextState = nextState with
                {
                    Content = extractedContent,
                    ContentHash = contentHash
                };

                await _stateStore.SavePageAsync(config.Id, nextState, cancellationToken).ConfigureAwait(false);
                return new PageProcessResult
                {
                    PageId = config.Id,
                    Succeeded = true
                };
            }

            var changed = !string.IsNullOrWhiteSpace(nextState.ContentHash);
            if (changed)
            {
                var webhookUrl = resolveWebhookUrl(config.WebhookSecretKey);
                if (!dryRun)
                {
                    var messages = _messageBuilder.BuildPageChangedMessages(
                        config.Name,
                        contentFetchPlan.FetchResult.FinalUrl,
                        nextState.Content,
                        extractedContent);

                    var notified = await _discordNotifier.SendMessagesAsync(webhookUrl, messages, cancellationToken).ConfigureAwait(false);
                    if (!notified)
                    {
                        throw new InvalidOperationException($"ページ差分通知に失敗しました。pageId={config.Id}");
                    }
                }
            }

            nextState = nextState with
            {
                Content = extractedContent,
                ContentHash = contentHash
            };

            await _stateStore.SavePageAsync(config.Id, nextState, cancellationToken).ConfigureAwait(false);
            return new PageProcessResult
            {
                PageId = config.Id,
                Succeeded = true,
                Changed = changed
            };
        }
        catch (Exception ex)
        {
            var failedState = BuildFailureState(config, state);
            await _stateStore.SavePageAsync(config.Id, failedState, cancellationToken).ConfigureAwait(false);

            return new PageProcessResult
            {
                PageId = config.Id,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 本文ページの取得計画を決定する。
    /// followLink の有無と 304 の組み合わせに応じて、どの URL を条件付き取得するかを分岐する。
    /// </summary>
    private async Task<ContentFetchPlan> ResolveContentFetchAsync(
        PageConfig config,
        PageState state,
        FetchResult topFetch,
        CancellationToken cancellationToken)
    {
        if (config.FollowLink is null)
        {
            return new ContentFetchPlan(config.Url, topFetch);
        }

        if (topFetch.IsNotModified)
        {
            if (string.IsNullOrWhiteSpace(state.PageUrl))
            {
                // 前回の本文 URL が無ければ再取得先を決められないため、今回はトップページ cache 更新のみで終える。
                return new ContentFetchPlan(config.Url, FetchResult: null);
            }

            var previousContentFetch = await _feedHttpClient
                .FetchAsync(state.PageUrl, state.ContentPageHttpCache, cancellationToken)
                .ConfigureAwait(false);

            return new ContentFetchPlan(state.PageUrl, previousContentFetch);
        }

        var targetUrl = ResolveFollowLinkUrl(topFetch.Content!, topFetch.FinalUrl, config.FollowLink);
        var contentFetch = await _feedHttpClient
            .FetchAsync(targetUrl, state.ContentPageHttpCache, cancellationToken)
            .ConfigureAwait(false);

        return new ContentFetchPlan(targetUrl, contentFetch);
    }

    /// <summary>
    /// トップページ内から followLink 条件に合うリンク先 URL を解決する。
    /// selector で絞ったリンクの表示文字列を `OrdinalIgnoreCase` で比較し、最初の一致を使う。
    /// </summary>
    private static string ResolveFollowLinkUrl(string topContent, string topUrl, FollowLinkConfig followLink)
    {
        var document = HtmlParser.ParseDocument(topContent);
        var candidates = document.QuerySelectorAll(followLink.LinkSelector);

        foreach (var candidate in candidates)
        {
            var text = NormalizeText(candidate.TextContent);
            if (text is null ||
                !text.Contains(followLink.TextMatch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = candidate.GetAttribute("href") ?? candidate.GetAttribute("data-href");
            var onclick = candidate.GetAttribute("onclick");
            var resolvedUrl = ResolveLinkUrl(href, onclick, topUrl);
            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                return resolvedUrl;
            }
        }

        throw new InvalidOperationException($"followLink に一致するリンクが見つかりません。textMatch={followLink.TextMatch}");
    }

    /// <summary>
    /// 監視対象本文を抽出する。
    /// selector が指定されていればその要素群、無ければ body 全体のテキストを比較対象にする。
    /// </summary>
    private static string ExtractTargetContent(string html, string? selector)
    {
        var document = HtmlParser.ParseDocument(html);

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var nodes = document.QuerySelectorAll(selector);
            var selectedText = string.Join(
                "\n",
                nodes
                    .Select(ExtractReadableText)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                return selectedText;
            }
        }

        return ExtractReadableText(document.Body) ??
               ExtractReadableText(document.DocumentElement) ??
               string.Empty;
    }

    /// <summary>
    /// href または onclick から実際に辿る URL を決める。
    /// JavaScript 疑似リンクでも、クォートされた URL 断片があれば拾って解決する。
    /// </summary>
    private static string ResolveLinkUrl(string? href, string? onclick, string baseUrl)
    {
        var directUrl = NormalizeText(href);
        if (!string.IsNullOrWhiteSpace(directUrl) &&
            !directUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveAbsoluteUrl(directUrl, baseUrl);
        }

        var scriptUrl = ExtractUrlFromOnclick(onclick);
        if (!string.IsNullOrWhiteSpace(scriptUrl))
        {
            return ResolveAbsoluteUrl(scriptUrl, baseUrl);
        }

        throw new InvalidOperationException("リンク URL を解決できませんでした。");
    }

    /// <summary>
    /// 相対 URL を取得元 URL 基準の絶対 URL へ変換する。
    /// 解決できない場合は元値を返し、エラー時に何が入っていたか分かるようにする。
    /// </summary>
    private static string ResolveAbsoluteUrl(string rawUrl, string baseUrl)
    {
        if (Uri.TryCreate(rawUrl, UriKind.RelativeOrAbsolute, out var parsed) &&
            parsed.IsAbsoluteUri &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.ToString();
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, rawUrl, out var relative))
        {
            return relative.ToString();
        }

        return rawUrl;
    }

    /// <summary>
    /// onclick 文字列から URL 断片を抜き出す。
    /// ここでは JavaScript 全体を評価せず、クォートされた URL らしい文字列だけを対象にする。
    /// </summary>
    private static string? ExtractUrlFromOnclick(string? onclick)
    {
        if (string.IsNullOrWhiteSpace(onclick))
        {
            return null;
        }

        var firstSingleQuote = onclick.IndexOf('\'');
        if (firstSingleQuote >= 0)
        {
            var secondSingleQuote = onclick.IndexOf('\'', firstSingleQuote + 1);
            if (secondSingleQuote > firstSingleQuote)
            {
                return NormalizeText(onclick[(firstSingleQuote + 1)..secondSingleQuote]);
            }
        }

        var firstDoubleQuote = onclick.IndexOf('"');
        if (firstDoubleQuote >= 0)
        {
            var secondDoubleQuote = onclick.IndexOf('"', firstDoubleQuote + 1);
            if (secondDoubleQuote > firstDoubleQuote)
            {
                return NormalizeText(onclick[(firstDoubleQuote + 1)..secondDoubleQuote]);
            }
        }

        return null;
    }

    /// <summary>
    /// 監視対象本文のハッシュを計算する。
    /// 比較対象は内容だけでよいので、SHA-256 を固定してブレのない判定にする。
    /// </summary>
    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// URL 変更検知メッセージをエラー通知チャンネルへ送る。
    /// 設定 URL と実取得 URL が違う場合のみ通知し、同一なら何もしない。
    /// </summary>
    private async Task NotifyUrlChangedAsync(
        string targetName,
        string registeredUrl,
        string actualUrl,
        string errorWebhookUrl,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (string.Equals(registeredUrl, actualUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (dryRun)
        {
            return;
        }

        var messages = _messageBuilder.BuildUrlChangedMessages(targetName, registeredUrl, actualUrl);
        var notified = await _discordNotifier.SendMessagesAsync(errorWebhookUrl, messages, cancellationToken).ConfigureAwait(false);
        if (!notified)
        {
            throw new InvalidOperationException("URL 変更警告の送信に失敗しました。");
        }
    }

    /// <summary>
    /// 失敗時の state を更新する。
    /// 連続失敗回数が閾値に達したら、一定時間だけ再試行を止める。
    /// </summary>
    private static PageState BuildFailureState(PageConfig config, PageState state)
    {
        var nextFailureCount = state.ConsecutiveFailures + 1;
        var circuitOpenUntil = nextFailureCount >= CircuitOpenThreshold
            ? DateTimeOffset.UtcNow.Add(CircuitOpenDuration)
            : state.CircuitOpenUntil;

        return state with
        {
            PageUrl = string.IsNullOrWhiteSpace(state.PageUrl) ? config.Url : state.PageUrl,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTimeOffset.UtcNow,
            ConsecutiveFailures = nextFailureCount,
            CircuitOpenUntil = circuitOpenUntil
        };
    }

    /// <summary>
    /// state 未作成時の初期値を作る。
    /// 本文 URL は最初は設定 URL を入れておき、followLink 解決後に上書きする。
    /// </summary>
    private static PageState CreateInitialState(PageConfig config)
    {
        return new PageState
        {
            PageUrl = config.Url,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTimeOffset.MinValue
        };
    }

    /// <summary>
    /// 空白だけの値を null に寄せ、比較や hash 前の入力を安定させる。
    /// 余分な空白は 1 つに詰めて、見た目だけの変更で差分が増えにくくする。
    /// </summary>
    private static string? NormalizeText(string? value)
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
    /// 差分比較用の本文テキストを、段落区切りを保ちながら取り出す。
    /// 段落や br の区切りは改行として残し、行内の余分な空白だけを詰める。
    /// </summary>
    private static string? ExtractReadableText(INode? node)
    {
        if (node is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        AppendReadableText(builder, node);

        return NormalizeMultilineText(builder.ToString());
    }

    /// <summary>
    /// ノードを順にたどり、本文テキストを StringBuilder へ積み上げる。
    /// ブロック要素の終端と br 要素では明示的に改行を入れる。
    /// </summary>
    private static void AppendReadableText(StringBuilder builder, INode node)
    {
        if (node is IText textNode)
        {
            builder.Append(textNode.Data);
            return;
        }

        if (node is IElement element)
        {
            if (string.Equals(element.TagName, "BR", StringComparison.OrdinalIgnoreCase))
            {
                AppendLineBreak(builder);
                return;
            }

            foreach (var child in element.ChildNodes)
            {
                AppendReadableText(builder, child);
            }

            if (IsBlockElement(element))
            {
                AppendLineBreak(builder);
            }

            return;
        }

        foreach (var child in node.ChildNodes)
        {
            AppendReadableText(builder, child);
        }
    }

    /// <summary>
    /// 複数行の本文を比較用に整形する。
    /// 各行の空白は詰めるが、行の区切り自体は保つ。
    /// </summary>
    private static string? NormalizeMultilineText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedLines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeText)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (normalizedLines.Length == 0)
        {
            return null;
        }

        return string.Join("\n", normalizedLines);
    }

    /// <summary>
    /// ブロック要素かどうかを判定する。
    /// 段落ごとに改行を入れたい要素だけを対象にする。
    /// </summary>
    private static bool IsBlockElement(IElement element)
    {
        return element.TagName.ToUpperInvariant() switch
        {
            "ADDRESS" or "ARTICLE" or "ASIDE" or "BLOCKQUOTE" or "DD" or "DIV" or "DL" or "DT" or
            "FIELDSET" or "FIGCAPTION" or "FIGURE" or "FOOTER" or "FORM" or "H1" or "H2" or "H3" or
            "H4" or "H5" or "H6" or "HEADER" or "HR" or "LI" or "MAIN" or "NAV" or "OL" or "P" or
            "PRE" or "SECTION" or "TABLE" or "TBODY" or "TD" or "TFOOT" or "TH" or "THEAD" or "TR" or
            "UL" => true,
            _ => false
        };
    }

    /// <summary>
    /// 末尾が改行でない場合だけ改行を追加する。
    /// ネストしたブロック要素があっても空行を増やしすぎないための補助。
    /// </summary>
    private static void AppendLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0 || builder[^1] == '\n')
        {
            return;
        }

        builder.Append('\n');
    }

    /// <summary>
    /// サーキットブレーカーが開いている間は監視を中断する。
    /// 外部サイト障害時に同じ失敗を短時間で繰り返さないための判定。
    /// </summary>
    private static bool IsCircuitOpen(PageState state)
    {
        return state.CircuitOpenUntil is not null &&
               state.CircuitOpenUntil.Value > DateTimeOffset.UtcNow;
    }

    private sealed record ContentFetchPlan(string PageUrl, FetchResult? FetchResult);
}
