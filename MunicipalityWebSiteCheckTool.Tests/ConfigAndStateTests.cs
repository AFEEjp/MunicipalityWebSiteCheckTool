using System.Text.Json;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Configuration;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.State;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class ConfigAndStateTests : IDisposable
{
    private readonly string _rootDirectory;

    public ConfigAndStateTests()
    {
        // テストごとに独立した一時ディレクトリを用意し、ファイル競合を避ける。
        _rootDirectory = Path.Combine(Path.GetTempPath(), "MunicipalityWebSiteCheckToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task ConfigFileLoader_LoadFeedsAsync_ReadConfigFiles()
    {
        // feed 設定を複数読み込めることを確認する。
        var feedsDirectory = Path.Combine(_rootDirectory, "feeds");
        Directory.CreateDirectory(feedsDirectory);
        await File.WriteAllTextAsync(Path.Combine(feedsDirectory, "a.json"), """
            {
              "id": "feed-a",
              "name": "A",
              "url": "https://example.com/a.xml",
              "type": "rss",
              "match": { "first": [], "second": [], "exclude": [] },
              "temporaryDisabled": false
            }
            """);

        var feeds = await ConfigFileLoader.LoadFeedsAsync(feedsDirectory, CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.Equal("feed-a", feed.Id);
    }

    [Fact]
    public async Task ConfigFileLoader_LoadFeedsAsync_ThrowWithFilePathWhenJsonIsBroken()
    {
        // JSON 構文エラー時にファイルパス付きで失敗することを確認する。
        var feedsDirectory = Path.Combine(_rootDirectory, "feeds");
        Directory.CreateDirectory(feedsDirectory);
        var brokenFile = Path.Combine(feedsDirectory, "broken.json");
        await File.WriteAllTextAsync(brokenFile, "{ invalid json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConfigFileLoader.LoadFeedsAsync(feedsDirectory, CancellationToken.None));

        Assert.Contains(brokenFile, ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task ConfigFileLoader_LoadPagesAsync_ThrowWhenIdsAreDuplicated()
    {
        // page 設定の id 重複を起動前に検出できることを確認する。
        var pagesDirectory = Path.Combine(_rootDirectory, "pages");
        Directory.CreateDirectory(pagesDirectory);
        await File.WriteAllTextAsync(Path.Combine(pagesDirectory, "a.json"), """
            {
              "id": "dup",
              "name": "A",
              "url": "https://example.com/a",
              "temporaryDisabled": false,
              "webhookSecretKey": "HOOK_A"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(pagesDirectory, "b.json"), """
            {
              "id": "dup",
              "name": "B",
              "url": "https://example.com/b",
              "temporaryDisabled": false,
              "webhookSecretKey": "HOOK_B"
            }
            """);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConfigFileLoader.LoadPagesAsync(pagesDirectory, CancellationToken.None));

        Assert.Contains("重複", ex.Message);
    }

    [Fact]
    public async Task StateStore_SaveAndLoadAsync_PersistFeedState()
    {
        // feed state を保存して読み戻せることを確認する。
        var stateDirectory = Path.Combine(_rootDirectory, "state");
        var store = new StateStore();
        store.Initialize(stateDirectory);

        var state = new FeedState
        {
            FeedUrl = "https://example.com/feed",
            FeedType = "rss",
            UpdatedUtc = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero)
        };

        await store.SaveAsync("feed-a", state, CancellationToken.None);
        var loaded = await store.LoadAsync("feed-a", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(state.FeedUrl, loaded!.FeedUrl);
        Assert.Equal(state.FeedType, loaded.FeedType);
    }

    [Fact]
    public async Task StateStore_SavePageAndLoadPageAsync_PersistPageState()
    {
        // page state も保存して読み戻せることを確認する。
        var stateDirectory = Path.Combine(_rootDirectory, "state");
        var store = new StateStore();
        store.Initialize(stateDirectory);

        var state = new PageState
        {
            PageUrl = "https://example.com/page",
            UpdatedUtc = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero),
            LastCheckedAt = new DateTimeOffset(2026, 3, 4, 12, 5, 0, TimeSpan.Zero),
            Content = "本文",
            ContentHash = "hash"
        };

        await store.SavePageAsync("page-a", state, CancellationToken.None);
        var loaded = await store.LoadPageAsync("page-a", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("本文", loaded!.Content);
        Assert.Equal("hash", loaded.ContentHash);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
