using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Configuration;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.Processors;

namespace MunicipalityWebSiteCheckTool.Monitoring;

public sealed class MonitorRunner
{
    private readonly FeedProcessor _feedProcessor;
    private readonly PageProcessor _pageProcessor;
    private readonly MessageBuilder _messageBuilder;
    private readonly DiscordNotifier _discordNotifier;

    public MonitorRunner(
        FeedProcessor feedProcessor,
        PageProcessor pageProcessor,
        MessageBuilder messageBuilder,
        DiscordNotifier discordNotifier)
    {
        ArgumentNullException.ThrowIfNull(feedProcessor);
        ArgumentNullException.ThrowIfNull(pageProcessor);
        ArgumentNullException.ThrowIfNull(messageBuilder);
        ArgumentNullException.ThrowIfNull(discordNotifier);

        _feedProcessor = feedProcessor;
        _pageProcessor = pageProcessor;
        _messageBuilder = messageBuilder;
        _discordNotifier = discordNotifier;
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
    /// cadence で絞り込んだ有効フィードだけを順次処理し、最後にエラー要約も送る。
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

        var errors = new List<string>();
        var hasFailure = false;

        foreach (var feed in targets)
        {
            var result = await _feedProcessor.ProcessAsync(
                             feed,
                             appOptions.DiscordWebhookError,
                             webhookKey => ResolveFeedWebhookUrl(appOptions, webhookKey),
                             options.DryRun,
                             cancellationToken)
                         .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                hasFailure = true;
                errors.Add($"feed:{result.FeedId}: {result.ErrorMessage}");
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
