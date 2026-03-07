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
    private const int RssHtmlNotifyThreshold = 3;
    private const int RssHtmlNotifySuppressCount = 3;

    private readonly IFeedHttpClient _feedHttpClient;
    private readonly IBrowserFeedHttpClient _browserFeedHttpClient;
    private readonly StateStore _stateStore;
    private readonly MessageBuilder _messageBuilder;
    private readonly IReadOnlyDictionary<string, IFeedSource> _feedSources;

    public FeedProcessor(
        IFeedHttpClient feedHttpClient,
        IBrowserFeedHttpClient browserFeedHttpClient,
        StateStore stateStore,
        MessageBuilder messageBuilder,
        IEnumerable<IFeedSource> feedSources)
    {
        ArgumentNullException.ThrowIfNull(feedHttpClient);
        ArgumentNullException.ThrowIfNull(browserFeedHttpClient);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(messageBuilder);
        ArgumentNullException.ThrowIfNull(feedSources);

        _feedHttpClient = feedHttpClient;
        _browserFeedHttpClient = browserFeedHttpClient;
        _stateStore = stateStore;
        _messageBuilder = messageBuilder;
        _feedSources = feedSources.ToDictionary(source => source.Type, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 単一フィードの監視処理を実行する。
    /// 取得、抽出、キーワード判定を行い、保存候補 state と通知予定を返す。
    /// </summary>
    public async Task<FeedProcessResult> ProcessAsync(
        FeedConfig config,
        string errorWebhookUrl,
        Func<string?, string> resolveWebhookUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorWebhookUrl);
        ArgumentNullException.ThrowIfNull(resolveWebhookUrl);

        // ここに来る時点で ConfigFileLoader により match 解決済みの前提。
        var match = config.Match ?? throw new InvalidOperationException($"match が解決されていません。feedId={config.Id}");

        var state = await _stateStore.LoadAsync(config.Id, cancellationToken).ConfigureAwait(false) ??
                    FeedState.CreateNew(config);

        if (IsCircuitOpen(state))
        {
            return new FeedProcessResult
            {
                FeedId = config.Id,
                FeedName = config.Name,
                Succeeded = true,
                SkippedByCircuitBreaker = true
            };
        }

        try
        {
            var fetchResult = await FetchFeedContentAsync(config, state, cancellationToken).ConfigureAwait(false);
            var updatedState = state with
            {
                FeedUrl = fetchResult.FinalUrl,
                FeedType = config.Type,
                UpdatedUtc = DateTimeOffset.UtcNow,
                HttpCache = fetchResult.NewCache,
                ConsecutiveFailures = 0,
                CircuitOpenUntil = null,
                RssHtmlMismatchCount = 0,
                RssHtmlLastNotifiedCount = 0,
                RssHtmlLastNotifiedUtc = null,
                ConsecutiveFailureLastNotifiedCount = 0,
                ConsecutiveFailureLastNotifiedUtc = null
            };

            var pendingNotifications = new List<PendingNotification>();
            pendingNotifications.AddRange(BuildUrlChangedNotifications(config, fetchResult.FinalUrl, errorWebhookUrl));

            if (fetchResult.IsNotModified)
            {
                await _stateStore.SaveAsync(config.Id, updatedState, cancellationToken).ConfigureAwait(false);
                return new FeedProcessResult
                {
                    FeedId = config.Id,
                    FeedName = config.Name,
                    Succeeded = true,
                    PendingNotifications = pendingNotifications,
                    BaseState = updatedState,
                    CandidateState = updatedState
                };
            }

            var feedSource = ResolveFeedSource(config.Type);
            var items = feedSource.ParseItems(config, fetchResult.Content!, fetchResult.FinalUrl);

            var seen = updatedState.Seen;
            var newItemCount = 0;
            var titleChangedCount = 0;
            var webhookUrl = resolveWebhookUrl(config.WebhookKey);
            var newItems = new List<FeedDetectedItem>();
            var titleChangedItems = new List<FeedTitleChangedItem>();

            foreach (var item in items)
            {
                if (!IsMatchTarget(item, match))
                {
                    continue;
                }

                var existing = SeenList.Find(seen, item.ItemKey);
                if (existing is null)
                {
                    var keywords = KeywordMatcher.DetectKeywords(BuildMatchText(item), match);
                    var messages = _messageBuilder.BuildNewItemMessages(config.Name, item, keywords);
                    pendingNotifications.Add(new PendingNotification
                    {
                        FeedId = config.Id,
                        WebhookUrl = webhookUrl,
                        Messages = messages
                    });

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
                    newItems.Add(new FeedDetectedItem
                    {
                        Title = item.Title,
                        Url = item.Url
                    });
                    continue;
                }

                if (!string.Equals(existing.Title, item.Title, StringComparison.Ordinal))
                {
                    var messageUrl = item.Url ?? config.Url;
                    var messages = _messageBuilder.BuildTitleChangedMessages(config.Name, messageUrl, existing.Title, item.Title);
                    pendingNotifications.Add(new PendingNotification
                    {
                        FeedId = config.Id,
                        WebhookUrl = webhookUrl,
                        Messages = messages
                    });

                    seen = SeenList.UpdateTitle(seen, item.ItemKey, item.Title);
                    titleChangedCount++;
                    titleChangedItems.Add(new FeedTitleChangedItem
                    {
                        Url = messageUrl,
                        OldTitle = existing.Title,
                        NewTitle = item.Title
                    });
                }
            }

            var candidateState = updatedState with
            {
                Seen = seen
            };

            return new FeedProcessResult
            {
                FeedId = config.Id,
                FeedName = config.Name,
                Succeeded = true,
                NewItemCount = newItemCount,
                TitleChangedCount = titleChangedCount,
                NewItems = newItems,
                TitleChangedItems = titleChangedItems,
                PendingNotifications = pendingNotifications,
                BaseState = updatedState,
                    CandidateState = candidateState
            };
        }
        catch (RssUnexpectedHtmlException)
        {
            var mismatchCount = state.RssHtmlMismatchCount + 1;
            var shouldNotify = mismatchCount >= RssHtmlNotifyThreshold &&
                               (state.RssHtmlLastNotifiedCount <= 0 ||
                                mismatchCount - state.RssHtmlLastNotifiedCount >= RssHtmlNotifySuppressCount);

            var pendingNotifications = shouldNotify
                ? BuildRssHtmlMismatchNotifications(config, errorWebhookUrl, mismatchCount)
                : [];

            var candidateState = state with
            {
                FeedUrl = config.Url,
                FeedType = config.Type,
                UpdatedUtc = DateTimeOffset.UtcNow,
                RssHtmlMismatchCount = mismatchCount,
                RssHtmlLastNotifiedCount = shouldNotify ? mismatchCount : state.RssHtmlLastNotifiedCount,
                RssHtmlLastNotifiedUtc = shouldNotify ? DateTimeOffset.UtcNow : state.RssHtmlLastNotifiedUtc
            };

            return new FeedProcessResult
            {
                FeedId = config.Id,
                FeedName = config.Name,
                Succeeded = true,
                WarningMessage = shouldNotify
                    ? $"rss-html-mismatch: 連続 {mismatchCount} 回検知。通知送信対象。"
                    : $"rss-html-mismatch: 連続 {mismatchCount} 回検知。通知は抑制中。",
                PendingNotifications = pendingNotifications,
                BaseState = candidateState,
                CandidateState = candidateState
            };
        }
        catch (Exception ex)
        {
            if (IsTransientFeedError(ex))
            {
                var transientFailedState = BuildFailureState(config, state);
                var shouldNotify = transientFailedState.ConsecutiveFailures >= CircuitOpenThreshold &&
                                   (state.ConsecutiveFailureLastNotifiedCount <= 0 ||
                                    transientFailedState.ConsecutiveFailures - state.ConsecutiveFailureLastNotifiedCount >= CircuitOpenThreshold);

                var candidateState = transientFailedState with
                {
                    ConsecutiveFailureLastNotifiedCount = shouldNotify
                        ? transientFailedState.ConsecutiveFailures
                        : state.ConsecutiveFailureLastNotifiedCount,
                    ConsecutiveFailureLastNotifiedUtc = shouldNotify
                        ? DateTimeOffset.UtcNow
                        : state.ConsecutiveFailureLastNotifiedUtc
                };

                var pendingNotifications = shouldNotify
                    ? BuildConsecutiveFailureNotifications(config, errorWebhookUrl, candidateState.ConsecutiveFailures, ex.Message)
                    : [];

                return new FeedProcessResult
                {
                    FeedId = config.Id,
                    FeedName = config.Name,
                    Succeeded = true,
                    WarningMessage = shouldNotify
                        ? $"feed-error: 連続 {candidateState.ConsecutiveFailures} 回失敗。通知送信対象。"
                        : $"feed-error: 連続 {candidateState.ConsecutiveFailures} 回失敗。通知は抑制中。",
                    PendingNotifications = pendingNotifications,
                    BaseState = candidateState,
                    CandidateState = candidateState
                };
            }

            var failedState = BuildFailureState(config, state);
            await _stateStore.SaveAsync(config.Id, failedState, cancellationToken).ConfigureAwait(false);

            return new FeedProcessResult
            {
                FeedId = config.Id,
                FeedName = config.Name,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 一時障害として扱う例外かを判定する。
    /// 通信失敗やタイムアウトは連続回数管理の対象にし、単発では通知しない。
    /// </summary>
    private static bool IsTransientFeedError(Exception ex)
    {
        return ex is HttpRequestException or TimeoutException or TaskCanceledException;
    }

    /// <summary>
    /// RSS 想定 URL で HTML 応答が続いた場合の通知メッセージを作る。
    /// 通知先はエラー Webhook に固定し、運用者が障害として確認できるようにする。
    /// </summary>
    private IReadOnlyList<PendingNotification> BuildRssHtmlMismatchNotifications(
        FeedConfig config,
        string errorWebhookUrl,
        int mismatchCount)
    {
        var messages = _messageBuilder.BuildRssHtmlMismatchMessages(
            config.Name,
            config.Url,
            mismatchCount,
            RssHtmlNotifyThreshold);
        if (messages.Count == 0)
        {
            return [];
        }

        return
        [
            new PendingNotification
            {
                FeedId = config.Id,
                WebhookUrl = errorWebhookUrl,
                Messages = messages
            }
        ];
    }

    /// <summary>
    /// 通信系の連続失敗通知を作る。
    /// しきい値到達時のみ呼ばれる想定で、通知先はエラー Webhook に固定する。
    /// </summary>
    private IReadOnlyList<PendingNotification> BuildConsecutiveFailureNotifications(
        FeedConfig config,
        string errorWebhookUrl,
        int failureCount,
        string errorMessage)
    {
        var messages = _messageBuilder.BuildFeedConsecutiveFailureMessages(
            config.Name,
            config.Id,
            config.Url,
            failureCount,
            CircuitOpenThreshold,
            errorMessage);
        if (messages.Count == 0)
        {
            return [];
        }

        return
        [
            new PendingNotification
            {
                FeedId = config.Id,
                WebhookUrl = errorWebhookUrl,
                Messages = messages
            }
        ];
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
    /// タイトル文字列だけを検索対象にし、本文に近い情報で判定する。
    /// </summary>
    private static bool IsMatchTarget(FeedItem item, MatchConfig match)
    {
        var text = BuildMatchText(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            // タイトルが無い項目は、キーワード判定の対象にしない。
            return false;
        }

        return KeywordMatcher.IsMatch(text, match);
    }

    /// <summary>
    /// キーワード判定用に、タイトル文字列だけを返す。
    /// URL ではなく、タイトル本文だけを判定対象にする。
    /// </summary>
    private static string BuildMatchText(FeedItem item)
    {
        return item.Title ?? string.Empty;
    }

    /// <summary>
    /// リダイレクトで最終 URL が変わった場合の警告通知を組み立てる。
    /// 実送信は MonitorRunner 側でまとめて行うため、ここでは送信予定として返す。
    /// </summary>
    private IReadOnlyList<PendingNotification> BuildUrlChangedNotifications(
        FeedConfig config,
        string finalUrl,
        string errorWebhookUrl)
    {
        if (string.Equals(config.Url, finalUrl, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var messages = _messageBuilder.BuildUrlChangedMessages(config.Name, config.Url, finalUrl);
        if (messages.Count == 0)
        {
            return [];
        }

        return
        [
            new PendingNotification
            {
                FeedId = config.Id,
                WebhookUrl = errorWebhookUrl,
                Messages = messages
            }
        ];
    }

    /// <summary>
    /// フィード種別に応じて本文取得手段を切り替える。
    /// browser モードのみ Playwright 取得を使い、それ以外は従来の HTTP 取得を使う。
    /// </summary>
    private async Task<FetchResult> FetchFeedContentAsync(
        FeedConfig config,
        FeedState state,
        CancellationToken cancellationToken)
    {
        if (string.Equals(config.Type, "browser", StringComparison.OrdinalIgnoreCase))
        {
            return await _browserFeedHttpClient.FetchAsync(config, cancellationToken).ConfigureAwait(false);
        }

        return await _feedHttpClient.FetchAsync(config.Url, state.HttpCache, cancellationToken).ConfigureAwait(false);
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
