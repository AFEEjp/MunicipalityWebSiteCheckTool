using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Configuration;
using MunicipalityWebSiteCheckTool.Feeds;
using MunicipalityWebSiteCheckTool.Http;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.Monitoring;
using MunicipalityWebSiteCheckTool.Processors;
using MunicipalityWebSiteCheckTool.State;
using Polly;

return await ProgramEntry.MainAsync(args).ConfigureAwait(false);

internal static class ProgramEntry
{
    /// <summary>
    /// アプリケーションの実エントリーポイント。
    /// 引数解析、事前検証、DI 構築、実行までをここで束ねる。
    /// </summary>
    public static async Task<int> MainAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Ctrl+C 時は即終了ではなく、現在処理中の await を中断させる。
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var options = ParseArguments(args);
        var appOptions = LoadAppOptions(options.Mode);
        var stateStore = new StateStore();
        stateStore.Initialize(options.StateDirectory);

        await ValidateEnvironmentAsync(options, appOptions, cts.Token).ConfigureAwait(false);

        var builder = Host.CreateApplicationBuilder(args);
        ConfigureServices(builder.Services, stateStore);

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<MonitorRunner>();
        return await runner.RunAsync(options, appOptions, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// コマンドライン引数を解析し、実行オプションへ変換する。
    /// 指定が無いパスはカレントディレクトリ基準の既定値を補う。
    /// </summary>
    private static RunOptions ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? mode = null;
        string? cadence = null;
        string feedSettingsPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "feed-settings.json"));
        string feedsDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "feeds"));
        string pagesDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "pages"));
        string stateDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "state"));
        var dryRun = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--mode":
                    mode = RequireNextValue(args, ref index, "--mode");
                    break;
                case "--cadence":
                    cadence = RequireNextValue(args, ref index, "--cadence");
                    break;
                case "--feed-settings":
                    feedSettingsPath = Path.GetFullPath(RequireNextValue(args, ref index, "--feed-settings"));
                    break;
                case "--feeds-dir":
                    feedsDirectory = Path.GetFullPath(RequireNextValue(args, ref index, "--feeds-dir"));
                    break;
                case "--pages-dir":
                    pagesDirectory = Path.GetFullPath(RequireNextValue(args, ref index, "--pages-dir"));
                    break;
                case "--state-dir":
                    stateDirectory = Path.GetFullPath(RequireNextValue(args, ref index, "--state-dir"));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new InvalidOperationException($"未対応の引数です: {arg}");
            }
        }

        if (!string.Equals(mode, "feed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "page", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--mode には feed または page を指定してください。");
        }

        var validatedMode = mode!;

        return new RunOptions
        {
            Mode = validatedMode.ToLowerInvariant(),
            Cadence = cadence,
            FeedSettingsPath = feedSettingsPath,
            FeedsDirectory = feedsDirectory,
            PagesDirectory = pagesDirectory,
            StateDirectory = stateDirectory,
            DryRun = dryRun
        };
    }

    /// <summary>
    /// 現在の mode に必要な共通環境変数を読み込む。
    /// page モードでは PubCom 用 Webhook を必須にしない設計に合わせて分岐する。
    /// </summary>
    private static AppOptions LoadAppOptions(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        var errorWebhook = GetRequiredEnvironmentValue("DISCORD_WEBHOOK_ERROR");
        var pubcomWebhook = string.Equals(mode, "feed", StringComparison.OrdinalIgnoreCase)
            ? GetRequiredEnvironmentValue("DISCORD_WEBHOOK_PUBCOM")
            : string.Empty;

        return new AppOptions
        {
            DiscordWebhookPubcom = pubcomWebhook,
            DiscordWebhookError = errorWebhook
        };
    }

    /// <summary>
    /// 実行前の設定整合性を検証する。
    /// 特に page モードでは、各ページが参照する Secret 名の環境変数が存在するかを先に確認する。
    /// </summary>
    private static async Task ValidateEnvironmentAsync(
        RunOptions options,
        AppOptions appOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(appOptions);

        if (string.Equals(options.Mode, "feed", StringComparison.OrdinalIgnoreCase))
        {
            _ = appOptions.DiscordWebhookPubcom;
            _ = appOptions.DiscordWebhookError;

            // feed モードでは設定ファイル自体も先に読めることを確認しておく。
            var settings = await ConfigFileLoader.LoadFeedSettingsAsync(options.FeedSettingsPath, cancellationToken).ConfigureAwait(false);
            await ConfigFileLoader.LoadFeedsAsync(options.FeedsDirectory, settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pages = await ConfigFileLoader.LoadPagesAsync(options.PagesDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var secretName in pages
                     .Where(static page => !page.TemporaryDisabled)
                     .Select(static page => page.WebhookSecretKey)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _ = GetRequiredEnvironmentValue(secretName);
        }
    }

    /// <summary>
    /// 依存関係を DI コンテナへ登録する。
    /// HTTP クライアントは圧縮・タイムアウト・Polly リトライ込みで登録する。
    /// </summary>
    private static void ConfigureServices(IServiceCollection services, StateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stateStore);

        services.AddSingleton(stateStore);
        services.AddSingleton<MessageBuilder>();
        services.AddSingleton<DiscordNotifier>();
        services.AddSingleton<FeedProcessor>();
        services.AddSingleton<PageProcessor>();
        services.AddSingleton<MonitorRunner>();

        services.AddSingleton<IFeedSource, RssFeedSource>();
        services.AddSingleton<IFeedSource, HtmlFeedSource>();

        services
            .AddHttpClient<IFeedHttpClient, FeedHttpClient>(client =>
            {
                // 監視対象サイトは応答が遅いことがあるので、少し長めのタイムアウトにする。
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MunicipalityWebSiteCheckTool/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            })
            .AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(retryAttempt * 2)));

        services
            .AddHttpClient<IDiscordHttpClient, DiscordHttpClient>(client =>
            {
                // Discord Webhook は短時間で応答する前提なので、短めのタイムアウトにする。
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(retryAttempt)));
    }

    /// <summary>
    /// 値付き引数の次要素を取得する。
    /// 値が欠けている場合は、何の引数が不足しているか分かる例外にする。
    /// </summary>
    private static string RequireNextValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{optionName} の値が指定されていません。");
        }

        index++;
        return args[index];
    }

    /// <summary>
    /// 必須環境変数を取得する。
    /// 空文字も未設定扱いにし、運用ミスを見逃さない。
    /// </summary>
    private static string GetRequiredEnvironmentValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"環境変数が未設定です: {name}");
        }

        return value;
    }
}
