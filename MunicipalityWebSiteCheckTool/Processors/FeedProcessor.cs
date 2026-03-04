using System.Collections.Immutable;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Feeds;
using MunicipalityWebSiteCheckTool.Http;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.Processing;
using MunicipalityWebSiteCheckTool.State;

namespace MunicipalityWebSiteCheckTool.Processors;

public sealed class FeedProcessor
{
    private const int CircuitOpenThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(30);

    private readonly IFeedHttpClient _feedHttpClient;
    private readonly StateStore _stateStore;
    private readonly MessageBuilder _messageBuilder;
    private readonly DiscordNotifier _discordNotifier;
    private readonly IReadOnlyDictionary<string, IFeedSource> _feedSources;

    public FeedProcessor(
        IFeedHttpClient feedHttpClient,
        StateStore stateStore,
        MessageBuilder messageBuilder,
        DiscordNotifier discordNotifier,
        IEnumerable<IFeedSource> feedSources)
    {
        ArgumentNullException.ThrowIfNull(feedHttpClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(messageBuilder);
        ArgumentNullException.ThrowIfNull(discordNotifier);
        ArgumentNullException.ThrowIfNull(feedSources);

        _feedHttpClient = feedHttpClient;
        _stateStore = stateStore;
        _messageBuilder = messageBuilder;
        _discordNotifier = discordNotifier;
        _feedSources = feedSources.ToDictionary(source => source.Type, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 単一フィードの監視処理を実行する。
    /// 取得、抽出、キーワード判定、通知、state 更新までを 1 つの単位で完結させる。
    /// </summary>
    public async Task<FeedProcessResult> ProcessAsync(
        FeedConfig config,
        string errorWebhookUrl,
        Func<string?, string> resolveWebhookUrl,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorWebhookUrl);
        ArgumentNullException.ThrowIfNull(resolveWebhookUrl);

        var state = await _stateStore.LoadAsync(config.Id, cancellationToken).ConfigureAwait(false) ??
                    FeedState.CreateNew(config);

        if (IsCircuitOpen(state))
        {
            return new FeedProcessResult
            {
                FeedId = config.Id,
                Succeeded = true,
                SkippedByCircuitBreaker = true
            };
        }

        try
        {
            var fetchResult = await _feedHttpClient.FetchAsync(config.Url, state.HttpCache, cancellationToken).ConfigureAwait(false);
            var updatedState = state with
            {
                FeedUrl = fetchResult.FinalUrl,
                FeedType = config.Type,
                UpdatedUtc = DateTimeOffset.UtcNow,
                HttpCache = fetchResult.NewCache,
                ConsecutiveFailures = 0,
                CircuitOpenUntil = null
            };

            await NotifyUrlChangedAsync(config, fetchResult.FinalUrl, errorWebhookUrl, dryRun, cancellationToken).ConfigureAwait(false);

            if (fetchResult.IsNotModified)
            {
                await _stateStore.SaveAsync(config.Id, updatedState, cancellationToken).ConfigureAwait(false);
                return new FeedProcessResult
                {
                    FeedId = config.Id,
                    Succeeded = true
                };
            }

            var feedSource = ResolveFeedSource(config.Type);
            var items = feedSource.ParseItems(config, fetchResult.Content!, fetchResult.FinalUrl);

            var seen = updatedState.Seen;
            var newItemCount = 0;
            var titleChangedCount = 0;
            var webhookUrl = resolveWebhookUrl(config.WebhookKey);

            foreach (var item in items)
            {
                if (!IsMatchTarget(item, config.Match))
                {
                    continue;
                }

                var existing = SeenList.Find(seen, item.ItemKey);
                if (existing is null)
                {
                    var keywords = KeywordMatcher.DetectKeywords(BuildMatchText(item), config.Match);
                    var messages = _messageBuilder.BuildNewItemMessages(config.Name, item, keywords);
                    if (!dryRun)
                    {
                        var notified = await _discordNotifier.SendMessagesAsync(webhookUrl, messages, cancellationToken).ConfigureAwait(false);
                        if (!notified)
                        {
                            throw new InvalidOperationException($"Discord 通知に失敗しました。feedId={config.Id}");
                        }
                    }

                    seen = SeenList.Add(
                        seen,
                        new SeenEntry
                        {
                            Key = item.ItemKey,
                            Title = item.Title,
                            FirstSeenAt = DateTimeOffset.UtcNow
                        },
                        updatedState.MaxSeen);

                    newItemCount++;
                    continue;
                }

                if (!string.Equals(existing.Title, item.Title, StringComparison.Ordinal))
                {
                    var messageUrl = item.Url ?? config.Url;
                    var messages = _messageBuilder.BuildTitleChangedMessages(config.Name, messageUrl, existing.Title, item.Title);
                    if (!dryRun)
                    {
                        var notified = await _discordNotifier.SendMessagesAsync(webhookUrl, messages, cancellationToken).ConfigureAwait(false);
                        if (!notified)
                        {
                            throw new InvalidOperationException($"タイトル変更通知に失敗しました。feedId={config.Id}");
                        }
                    }

                    seen = SeenList.UpdateTitle(seen, item.ItemKey, item.Title);
                    titleChangedCount++;
                }
            }

            updatedState = updatedState with
            {
                Seen = seen
            };

            await _stateStore.SaveAsync(config.Id, updatedState, cancellationToken).ConfigureAwait(false);
            return new FeedProcessResult
            {
                FeedId = config.Id,
                Succeeded = true,
                NewItemCount = newItemCount,
                TitleChangedCount = titleChangedCount
            };
        }
        catch (Exception ex)
        {
            var failedState = BuildFailureState(config, state);
            await _stateStore.SaveAsync(config.Id, failedState, cancellationToken).ConfigureAwait(false);

            return new FeedProcessResult
            {
                FeedId = config.Id,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 設定されたフィード種別に対応するパーサーを取得する。
    /// 未対応の種別は即例外にし、設定ミスを早期に表面化させる。
    /// </summary>
    private IFeedSource ResolveFeedSource(string type)
    {
        if (_feedSources.TryGetValue(type, out var source))
        {
            return source;
        }

        throw new InvalidOperationException($"未対応のフィード種別です。type={type}");
    }

    /// <summary>
    /// フィード項目が検索条件に一致するかを判定する。
    /// タイトルと URL をまとめて検索対象にし、従来実装と同じ使い勝手を維持する。
    /// </summary>
    private static bool IsMatchTarget(FeedItem item, MatchConfig match)
    {
        var text = BuildMatchText(item);
        return KeywordMatcher.IsMatch(text, match);
    }

    /// <summary>
    /// キーワード判定用に、タイトルと URL を 1 つの文字列へまとめる。
    /// フィードによってはどちらか片方しか無いことがあるため、両方を連結して扱う。
    /// </summary>
    private static string BuildMatchText(FeedItem item)
    {
        return string.Join(
            " ",
            new[] { item.Title, item.Url }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    /// <summary>
    /// リダイレクトで最終 URL が変わった場合、エラー通知チャンネルへ警告を送る。
    /// 取得先変更は運用上の見直し対象なので、通常通知とは分けて扱う。
    /// </summary>
    private async Task NotifyUrlChangedAsync(
        FeedConfig config,
        string finalUrl,
        string errorWebhookUrl,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (string.Equals(config.Url, finalUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (dryRun)
        {
            return;
        }

        var messages = _messageBuilder.BuildUrlChangedMessages(config.Name, config.Url, finalUrl);
        var notified = await _discordNotifier.SendMessagesAsync(errorWebhookUrl, messages, cancellationToken).ConfigureAwait(false);
        if (!notified)
        {
            throw new InvalidOperationException($"URL 変更警告の送信に失敗しました。feedId={config.Id}");
        }
    }

    /// <summary>
    /// 連続失敗回数に応じてサーキットブレーカー状態を更新する。
    /// 同じ外部障害で連続失敗し続けるのを避けるため、一定回数で一時停止する。
    /// </summary>
    private static FeedState BuildFailureState(FeedConfig config, FeedState state)
    {
        var nextFailureCount = state.ConsecutiveFailures + 1;
        var circuitOpenUntil = nextFailureCount >= CircuitOpenThreshold
            ? DateTimeOffset.UtcNow.Add(CircuitOpenDuration)
            : state.CircuitOpenUntil;

        return state with
        {
            FeedUrl = state.FeedUrl ?? config.Url,
            FeedType = config.Type,
            UpdatedUtc = DateTimeOffset.UtcNow,
            ConsecutiveFailures = nextFailureCount,
            CircuitOpenUntil = circuitOpenUntil
        };
    }

    /// <summary>
    /// サーキットブレーカーが開いている間は処理をスキップする。
    /// 回復待ちの間に外部サイトへ再試行を繰り返さないための判定。
    /// </summary>
    private static bool IsCircuitOpen(FeedState state)
    {
        return state.CircuitOpenUntil is not null &&
               state.CircuitOpenUntil.Value > DateTimeOffset.UtcNow;
    }
}
