using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Configuration;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.Processors;
using MunicipalityWebSiteCheckTool.State;

namespace MunicipalityWebSiteCheckTool.Monitoring;

public sealed class MonitorRunner
{
    private const int FeedMaxDegreeOfParallelism = 4;

    private readonly FeedProcessor _feedProcessor;
    private readonly PageProcessor _pageProcessor;
    private readonly MessageBuilder _messageBuilder;
    private readonly DiscordNotifier _discordNotifier;
    private readonly StateStore _stateStore;

    public MonitorRunner(
        FeedProcessor feedProcessor,
        PageProcessor pageProcessor,
        MessageBuilder messageBuilder,
        DiscordNotifier discordNotifier,
        StateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(feedProcessor);
        ArgumentNullException.ThrowIfNull(pageProcessor);
        ArgumentNullException.ThrowIfNull(messageBuilder);
        ArgumentNullException.ThrowIfNull(discordNotifier);
        ArgumentNullException.ThrowIfNull(stateStore);

        _feedProcessor = feedProcessor;
        _pageProcessor = pageProcessor;
        _messageBuilder = messageBuilder;
        _discordNotifier = discordNotifier;
        _stateStore = stateStore;
    }

    /// <summary>
    /// 起動オプションに応じて feed/page のどちらかを実行する。
    /// 各モードの結果は最後に終了コードへ畳み込み、ワークフロー側が成否判定できるようにする。
    /// </summary>
    public async Task<int> RunAsync(
        RunOptions options,
        AppOptions appOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(appOptions);

        return options.Mode switch
        {
            "feed" => await RunFeedModeAsync(options, appOptions, cancellationToken).ConfigureAwait(false),
            "page" => await RunPageModeAsync(options, appOptions, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"未対応の mode です: {options.Mode}")
        };
    }

    /// <summary>
    /// feed モードを実行する。
    /// cadence で絞り込んだ有効フィードを最大 4 件並列で処理し、通知は最後にまとめて送る。
    /// </summary>
    private async Task<int> RunFeedModeAsync(
        RunOptions options,
        AppOptions appOptions,
        CancellationToken cancellationToken)
    {
        var settings = await ConfigFileLoader.LoadFeedSettingsAsync(options.FeedSettingsPath, cancellationToken).ConfigureAwait(false);
        var feeds = await ConfigFileLoader.LoadFeedsAsync(options.FeedsDirectory, settings, cancellationToken).ConfigureAwait(false);
        var targets = feeds
            .Where(static feed => !feed.TemporaryDisabled)
            .Where(feed => string.IsNullOrWhiteSpace(options.Cadence) ||
                           string.Equals(feed.Cadence, options.Cadence, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Console.WriteLine($"[feed] 実行対象件数: {targets.Length}");

        var errors = new List<string>();
        var hasFailure = false;
        var results = new List<FeedProcessResult>(targets.Length);

        await Parallel.ForEachAsync(
                targets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = FeedMaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                async (feed, token) =>
                {
                    var result = await _feedProcessor.ProcessAsync(
                                     feed,
                                     appOptions.DiscordWebhookError,
                                     webhookKey => ResolveFeedWebhookUrl(appOptions, webhookKey),
                                     token)
                                 .ConfigureAwait(false);

                    lock (results)
                    {
                        results.Add(result);
                    }
                })
            .ConfigureAwait(false);

        foreach (var result in results.OrderBy(result => result.FeedName, StringComparer.OrdinalIgnoreCase))
        {
            WriteFeedResultLog(result);
            if (!result.Succeeded)
            {
                hasFailure = true;
                errors.Add($"feed:{result.FeedName}({result.FeedId}): {result.ErrorMessage}");
            }
        }

        var pendingNotifications = results
            .Where(static result => result.Succeeded)
            .SelectMany(static result => result.PendingNotifications)
            .ToArray();

        var notificationErrors = await SendPendingNotificationsAsync(pendingNotifications, options.DryRun, cancellationToken)
            .ConfigureAwait(false);
        if (notificationErrors.Count > 0)
        {
            hasFailure = true;
            errors.AddRange(notificationErrors.Select(static error => error.Message));
        }

        await SaveFeedStatesAsync(results, notificationErrors.Select(static error => error.FeedId), cancellationToken)
            .ConfigureAwait(false);

        if (notificationErrors.Count > 0)
        {
            foreach (var failedFeedId in notificationErrors.Select(static error => error.FeedId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[feed:warn] 通知送信失敗のため Seen 更新を保留: feedId={failedFeedId}");
            }
        }

        await NotifyErrorSummaryAsync(appOptions.DiscordWebhookError, errors, options.DryRun, cancellationToken).ConfigureAwait(false);
        return hasFailure ? 1 : 0;
    }

    /// <summary>
    /// page モードを実行する。
    /// 有効なページ設定を順次処理し、失敗があればエラー通知チャンネルへ要約を送る。
    /// </summary>
    private async Task<int> RunPageModeAsync(
        RunOptions options,
        AppOptions appOptions,
        CancellationToken cancellationToken)
    {
        var pages = await ConfigFileLoader.LoadPagesAsync(options.PagesDirectory, cancellationToken).ConfigureAwait(false);
        var targets = pages
            .Where(static page => !page.TemporaryDisabled)
            .ToArray();

        var errors = new List<string>();
        var hasFailure = false;

        foreach (var page in targets)
        {
            var result = await _pageProcessor.ProcessAsync(
                             page,
                             appOptions.DiscordWebhookError,
                             ResolveRequiredSecret,
                             options.DryRun,
                             cancellationToken)
                         .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                hasFailure = true;
                errors.Add($"page:{result.PageId}: {result.ErrorMessage}");
            }
        }

        await NotifyErrorSummaryAsync(appOptions.DiscordWebhookError, errors, options.DryRun, cancellationToken).ConfigureAwait(false);
        return hasFailure ? 1 : 0;
    }

    /// <summary>
    /// feed 用の通知先 Webhook URL を決定する。
    /// 個別キーが無ければ共通の PubCom 通知先を使い、あれば該当 Secret を環境変数から引く。
    /// </summary>
    private static string ResolveFeedWebhookUrl(AppOptions appOptions, string? webhookKey)
    {
        if (string.IsNullOrWhiteSpace(webhookKey))
        {
            return appOptions.DiscordWebhookPubcom;
        }

        return ResolveRequiredSecret(webhookKey);
    }

    /// <summary>
    /// 必須の Webhook Secret を環境変数から取得する。
    /// 見つからない場合は設定不備として例外にし、黙って通常通知を失敗させない。
    /// </summary>
    private static string ResolveRequiredSecret(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var value = Environment.GetEnvironmentVariable(secretName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"環境変数が未設定です: {secretName}");
        }

        return value;
    }

    /// <summary>
    /// feed 処理結果を GitHub Actions のログへ出力する。
    /// 監視対象名、検出件数、検出したタイトルと URL が追える形式で記録する。
    /// </summary>
    private static void WriteFeedResultLog(FeedProcessResult result)
    {
        if (!result.Succeeded)
        {
            Console.WriteLine($"[feed:error] {result.FeedName} ({result.FeedId}) {result.ErrorMessage}");
            return;
        }

        if (result.SkippedByCircuitBreaker)
        {
            Console.WriteLine($"[feed:skip] {result.FeedName} ({result.FeedId}) サーキットブレーカー中");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.WarningMessage))
        {
            Console.WriteLine($"[feed:warn] {result.FeedName} ({result.FeedId}) {result.WarningMessage}");
        }

        Console.WriteLine(
            $"[feed:done] {result.FeedName} ({result.FeedId}) new={result.NewItemCount} titleChanged={result.TitleChangedCount}");

        foreach (var item in result.NewItems)
        {
            Console.WriteLine(
                $"[feed:new] {result.FeedName} title={item.Title ?? "(タイトルなし)"} url={item.Url ?? "(URLなし)"}");
        }

        foreach (var changed in result.TitleChangedItems)
        {
            Console.WriteLine(
                $"[feed:title-changed] {result.FeedName} old={changed.OldTitle ?? "(なし)"} new={changed.NewTitle ?? "(なし)"} url={changed.Url ?? "(URLなし)"}");
        }
    }

    /// <summary>
    /// 送信予定の通知を Webhook ごとにまとめて送る。
    /// 投稿間隔は DiscordNotifier 側に任せ、ここでは送信失敗を収集して戻す。
    /// </summary>
    private async Task<IReadOnlyList<NotificationDispatchError>> SendPendingNotificationsAsync(
        IReadOnlyList<PendingNotification> notifications,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (notifications.Count == 0)
        {
            return [];
        }

        if (dryRun)
        {
            Console.WriteLine($"[feed:dry-run] 送信予定メッセージ数: {notifications.Sum(static n => n.Messages.Count)}");
            return [];
        }

        var errors = new List<NotificationDispatchError>();
        var groupedByWebhook = notifications
            .GroupBy(static notification => notification.WebhookUrl, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByWebhook)
        {
            var messages = group
                .SelectMany(static notification => notification.Messages)
                .Where(static message => !string.IsNullOrWhiteSpace(message))
                .ToArray();
            if (messages.Length == 0)
            {
                continue;
            }

            var notified = await _discordNotifier.SendMessagesAsync(group.Key, messages, cancellationToken).ConfigureAwait(false);
            if (!notified)
            {
                foreach (var failedFeedId in group.Select(static item => item.FeedId).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new NotificationDispatchError
                    {
                        FeedId = failedFeedId,
                        Message = $"notify:{group.Key}: 通知送信に失敗しました。feedId={failedFeedId}"
                    });
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// feed 処理結果の state を保存する。
    /// 通知成功時は candidate state、通知失敗時は base state を保存して取りこぼしを防ぐ。
    /// </summary>
    private async Task SaveFeedStatesAsync(
        IReadOnlyList<FeedProcessResult> results,
        IEnumerable<string> notificationFailedFeedIds,
        CancellationToken cancellationToken)
    {
        var failedFeedIdSet = notificationFailedFeedIds
            .Where(static feedId => !string.IsNullOrWhiteSpace(feedId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            if (!result.Succeeded || result.SkippedByCircuitBreaker)
            {
                continue;
            }

            var stateToSave = failedFeedIdSet.Contains(result.FeedId)
                ? result.BaseState
                : result.CandidateState;
            if (stateToSave is null)
            {
                continue;
            }

            await _stateStore.SaveAsync(result.FeedId, stateToSave, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record NotificationDispatchError
    {
        public required string FeedId { get; init; }

        public required string Message { get; init; }
    }

    /// <summary>
    /// 複数の失敗をまとめたエラー要約を送る。
    /// dry-run では実送信せず、終了コードだけで失敗を表現する。
    /// </summary>
    private async Task NotifyErrorSummaryAsync(
        string errorWebhookUrl,
        IReadOnlyList<string> errors,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (errors.Count == 0 || dryRun)
        {
            return;
        }

        var messages = _messageBuilder.BuildErrorSummaryMessages(errors);
        if (messages.Count == 0)
        {
            return;
        }

        var notified = await _discordNotifier.SendMessagesAsync(errorWebhookUrl, messages, cancellationToken).ConfigureAwait(false);
        if (!notified)
        {
            throw new InvalidOperationException("エラー要約通知の送信に失敗しました。");
        }
    }
}
