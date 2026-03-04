using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Feeds;
using MunicipalityWebSiteCheckTool.Http;
using MunicipalityWebSiteCheckTool.Messaging;
using MunicipalityWebSiteCheckTool.Processors;
using MunicipalityWebSiteCheckTool.State;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class ProcessorTests : IDisposable
{
    private readonly string _rootDirectory;

    public ProcessorTests()
    {
        // Processor テストも state を使うため、各テストごとに専用の一時ディレクトリを切る。
        _rootDirectory = Path.Combine(Path.GetTempPath(), "MunicipalityWebSiteCheckToolProcessorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task FeedProcessor_ProcessAsync_AddNewSeenEntry()
    {
        // 新規項目を 1 件検出したとき、通知相当の処理を経て seen が保存されることを確認する。
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <rss version="2.0">
                      <channel>
                        <item>
                          <title>意見募集 条例案</title>
                          <link>https://example.com/item1</link>
                        </item>
                      </channel>
                    </rss>
                    """, Encoding.UTF8, "application/xml")
            });

        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        var processor = new FeedProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)),
            [new RssFeedSource(), new HtmlFeedSource()]);

        var result = await processor.ProcessAsync(
            CreateFeedConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/pubcom",
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.NewItemCount);

        var state = await stateStore.LoadAsync("feed-test", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Single(state!.Seen);
        Assert.Equal("意見募集 条例案", state.Seen[0].Title);
    }

    [Fact]
    public async Task FeedProcessor_ProcessAsync_SkipWhenCircuitIsOpen()
    {
        // サーキットブレーカー中は HTTP 取得せずにスキップ扱いになることを確認する。
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("呼ばれない想定"));
        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        await stateStore.SaveAsync("feed-test", new Domain.FeedState
        {
            FeedUrl = "https://example.com/feed.xml",
            FeedType = "rss",
            UpdatedUtc = DateTimeOffset.UtcNow,
            CircuitOpenUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        }, CancellationToken.None);

        var processor = new FeedProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)),
            [new RssFeedSource(), new HtmlFeedSource()]);

        var result = await processor.ProcessAsync(
            CreateFeedConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/pubcom",
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.SkippedByCircuitBreaker);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FeedProcessor_ProcessAsync_DoNotMatchKeywordsOnlyInUrl()
    {
        // URL にだけ含まれるキーワードでは検出しないことを確認する。
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <rss version="2.0">
                      <channel>
                        <item>
                          <title>お知らせ</title>
                          <link>https://example.com/意見募集/条例</link>
                        </item>
                      </channel>
                    </rss>
                    """, Encoding.UTF8, "application/xml")
            });

        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        var processor = new FeedProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)),
            [new RssFeedSource(), new HtmlFeedSource()]);

        var result = await processor.ProcessAsync(
            CreateFeedConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/pubcom",
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.NewItemCount);

        var state = await stateStore.LoadAsync("feed-test", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Empty(state!.Seen);
    }

    [Fact]
    public async Task PageProcessor_ProcessAsync_DetectContentChange()
    {
        // 本文が変わった場合に changed=true で state が更新されることを確認する。
        var firstResponse = CreateHtmlResponse("""
            <html>
              <body>
                <main>変更前</main>
              </body>
            </html>
            """);
        var secondResponse = CreateHtmlResponse("""
            <html>
              <body>
                <main>変更後</main>
              </body>
            </html>
            """);

        var handlerResponses = new Queue<HttpResponseMessage>([firstResponse, secondResponse]);
        var handler = new StubHttpMessageHandler(_ => handlerResponses.Dequeue());
        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        var processor = new PageProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)));

        var first = await processor.ProcessAsync(
            CreatePageConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/page",
            dryRun: true,
            CancellationToken.None);
        var second = await processor.ProcessAsync(
            CreatePageConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/page",
            dryRun: true,
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(first.Changed);
        Assert.True(second.Succeeded);
        Assert.True(second.Changed);

        var state = await stateStore.LoadPageAsync("page-test", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal("変更後", state!.Content);
    }

    [Fact]
    public async Task PageProcessor_ProcessAsync_PreserveLineBreaksInExtractedContent()
    {
        // 段落ごとの改行を保持したまま本文を state に保存することを確認する。
        var response = CreateHtmlResponse("""
            <html>
              <body>
                <main>
                  <p>一行目</p>
                  <p>二行目</p>
                  <div>三行目<br>四行目</div>
                </main>
              </body>
            </html>
            """);

        var handler = new StubHttpMessageHandler(_ => response);
        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        var processor = new PageProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)));

        var result = await processor.ProcessAsync(
            CreatePageConfig(),
            "https://example.invalid/error",
            _ => "https://example.invalid/page",
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);

        var state = await stateStore.LoadPageAsync("page-test", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal("一行目\n二行目\n三行目\n四行目", state!.Content);
    }

    [Fact]
    public async Task PageProcessor_ProcessAsync_ReuseStoredPageUrlWhenTopPageIsNotModified()
    {
        // followLink ありでトップページが 304 の場合、保存済みの本文 URL を使って再取得することを確認する。
        var topNotModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        topNotModified.Headers.ETag = new EntityTagHeaderValue("\"etag-top\"");

        var contentResponse = CreateHtmlResponse("""
            <html>
              <body>
                <main>本文</main>
              </body>
            </html>
            """);

        var handlerResponses = new Queue<HttpResponseMessage>([topNotModified, contentResponse]);
        var requestedUrls = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestedUrls.Add(request.RequestUri!.ToString());
            return handlerResponses.Dequeue();
        });

        using var httpClient = new HttpClient(handler);
        using var discordClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var stateStore = CreateStateStore();
        stateStore.Initialize(Path.Combine(_rootDirectory, "state-follow"));
        await stateStore.SavePageAsync("page-test", new Domain.PageState
        {
            PageUrl = "https://example.com/detail",
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastCheckedAt = DateTimeOffset.UtcNow,
            TopPageHttpCache = new Domain.HttpCacheInfo
            {
                ETag = "\"etag-top\""
            },
            ContentPageHttpCache = new Domain.HttpCacheInfo
            {
                ETag = "\"etag-content\""
            }
        }, CancellationToken.None);

        var processor = new PageProcessor(
            new FeedHttpClient(httpClient),
            stateStore,
            new MessageBuilder(),
            new DiscordNotifier(new DiscordHttpClient(discordClient)));

        var result = await processor.ProcessAsync(
            CreatePageConfigWithFollowLink(),
            "https://example.invalid/error",
            _ => "https://example.invalid/page",
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["https://example.com/list", "https://example.com/detail"], requestedUrls);
    }

    private StateStore CreateStateStore()
    {
        var stateStore = new StateStore();
        stateStore.Initialize(Path.Combine(_rootDirectory, Guid.NewGuid().ToString("N")));
        return stateStore;
    }

    private static FeedConfig CreateFeedConfig()
    {
        return new FeedConfig
        {
            Id = "feed-test",
            Name = "Feed Test",
            Url = "https://example.com/feed.xml",
            Type = "rss",
            Match = new MatchConfig
            {
                First = ["意見募集"],
                Second = ["条例"],
                Exclude = []
            },
            TemporaryDisabled = false
        };
    }

    private static PageConfig CreatePageConfig()
    {
        return new PageConfig
        {
            Id = "page-test",
            Name = "Page Test",
            Url = "https://example.com/list",
            TemporaryDisabled = false,
            WebhookSecretKey = "DISCORD_WEBHOOK_PAGE_TEST",
            ContentSelector = "main"
        };
    }

    private static PageConfig CreatePageConfigWithFollowLink()
    {
        return new PageConfig
        {
            Id = "page-test",
            Name = "Page Test",
            Url = "https://example.com/list",
            TemporaryDisabled = false,
            WebhookSecretKey = "DISCORD_WEBHOOK_PAGE_TEST",
            ContentSelector = "main",
            FollowLink = new FollowLinkConfig
            {
                TextMatch = "次回の開催について"
            }
        };
    }

    private static HttpResponseMessage CreateHtmlResponse(string html)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        /// <summary>
        /// 受け取ったリクエストごとに、テスト側で用意したレスポンスを返す。
        /// 外部通信を発生させずに、Processor の分岐だけを検証するための簡易ハンドラ。
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
