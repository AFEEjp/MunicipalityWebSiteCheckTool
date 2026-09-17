using System.Collections.Immutable;
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

        var feeds = await ConfigFileLoader.LoadFeedsAsync(feedsDirectory, settings: null, CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.Equal("feed-a", feed.Id);
    }

    [Fact]
    public async Task ConfigFileLoader_LoadFeedsAsync_ReadConfigFilesInSubDirectories()
    {
        // feeds 配下のサブフォルダも再帰的に読めることを確認する。
        var feedsDirectory = Path.Combine(_rootDirectory, "feeds-nested");
        var rssDirectory = Path.Combine(feedsDirectory, "rss");
        Directory.CreateDirectory(rssDirectory);
        await File.WriteAllTextAsync(Path.Combine(rssDirectory, "a.json"), """
            {
              "id": "feed-nested-a",
              "name": "Nested A",
              "url": "https://example.com/a.xml",
              "type": "rss",
              "match": { "first": [], "second": [], "exclude": [] },
              "temporaryDisabled": false
            }
            """);

        var feeds = await ConfigFileLoader.LoadFeedsAsync(feedsDirectory, settings: null, CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.Equal("feed-nested-a", feed.Id);
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
            ConfigFileLoader.LoadFeedsAsync(feedsDirectory, settings: null, CancellationToken.None));

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
    public async Task ConfigFileLoader_LoadFeedsAsync_ApplyDefaultMatchWhenFeedMatchIsMissing()
    {
        // 個別 match が無い場合だけ共通 defaultMatch が補われることを確認する。
        var feedsDirectory = Path.Combine(_rootDirectory, "feeds-default");
        Directory.CreateDirectory(feedsDirectory);
        await File.WriteAllTextAsync(Path.Combine(feedsDirectory, "a.json"), """
            {
              "id": "feed-a",
              "name": "A",
              "url": "https://example.com/a.xml",
              "type": "rss",
              "temporaryDisabled": false
            }
            """);

        var settings = new FeedSettingsConfig
        {
            DefaultMatch = new MatchConfig
            {
                First = ["意見募集"],
                Second = ["条例"],
                Exclude = ["終了"]
            }
        };

        var feeds = await ConfigFileLoader.LoadFeedsAsync(feedsDirectory, settings, CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.NotNull(feed.Match);
        Assert.Equal(["意見募集"], feed.Match!.First);
        Assert.Equal(["条例"], feed.Match.Second);
    }

    [Fact]
    public async Task ConfigFileLoader_LoadFeedsAsync_PrioritizeFeedSpecificMatch()
    {
        // 個別 match がある場合は、共通 defaultMatch より個別設定を優先する。
        var feedsDirectory = Path.Combine(_rootDirectory, "feeds-priority");
        Directory.CreateDirectory(feedsDirectory);
        await File.WriteAllTextAsync(Path.Combine(feedsDirectory, "a.json"), """
            {
              "id": "feed-a",
              "name": "A",
              "url": "https://example.com/a.xml",
              "type": "rss",
              "match": {
                "first": ["個別第一語"],
                "second": ["個別第二語"],
                "exclude": ["個別除外語"]
              },
              "temporaryDisabled": false
            }
            """);

        var settings = new FeedSettingsConfig
        {
            DefaultMatch = new MatchConfig
            {
                First = ["共通第一語"],
                Second = ["共通第二語"],
                Exclude = ["共通除外語"]
            }
        };

        var feeds = await ConfigFileLoader.LoadFeedsAsync(feedsDirectory, settings, CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.Equal(["個別第一語"], feed.Match!.First);
        Assert.Equal(["個別第二語"], feed.Match.Second);
        Assert.Equal(["個別除外語"], feed.Match.Exclude);
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
            UpdatedUtc = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero),
            Seen = ImmutableList.Create(new SeenEntry
            {
                Key = "https://example.com/public-comment",
                Title = "第5次千葉市男女共同参画基本計画（案）",
                FirstUrl = "https://example.com/public-comment/first",
                CurrentUrl = "https://example.com/public-comment/current",
                FirstSeenAt = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero)
            })
        };

        await store.SaveAsync("feed-a", state, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "feed-a.json"));
        var loaded = await store.LoadAsync("feed-a", CancellationToken.None);

        Assert.Contains("\"title\": \"第5次千葉市男女共同参画基本計画（案）\"", json);
        Assert.Contains("\"firstUrl\": \"https://example.com/public-comment/first\"", json);
        Assert.Contains("\"currentUrl\": \"https://example.com/public-comment/current\"", json);
        Assert.DoesNotContain("\\u7B2C", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(loaded);
        Assert.Equal(state.FeedUrl, loaded!.FeedUrl);
        Assert.Equal(state.FeedType, loaded.FeedType);
        Assert.Equal(state.Seen, loaded.Seen);
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
            Content = "東京都青少年健全育成審議会",
            ContentHash = "hash"
        };

        await store.SavePageAsync("page-a", state, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "page-a.json"));
        var loaded = await store.LoadPageAsync("page-a", CancellationToken.None);

        Assert.Contains("\"content\": \"東京都青少年健全育成審議会\"", json);
        Assert.DoesNotContain("\\u6771", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(loaded);
        Assert.Equal("東京都青少年健全育成審議会", loaded!.Content);
        Assert.Equal("hash", loaded.ContentHash);
    }

    [Fact]
    public async Task StateStore_LoadAsync_ReadEscapedUnicodeFromExistingState()
    {
        // 既存の \uXXXX 形式の state も従来どおり読み込めることを確認する。
        var stateDirectory = Path.Combine(_rootDirectory, "state");
        var store = new StateStore();
        store.Initialize(stateDirectory);

        await File.WriteAllTextAsync(Path.Combine(stateDirectory, "feed-a.json"), """
            {
              "version": 1,
              "feedUrl": "https://example.com/feed",
              "feedType": "rss",
              "updatedUtc": "2026-03-04T12:00:00+00:00",
              "seen": [
                {
                  "key": "https://example.com/public-comment",
                  "title": "\u7B2C5\u6B21\u5343\u8449\u5E02",
                  "firstSeenAt": "2026-03-04T12:00:00+00:00"
                }
              ]
            }
            """);

        var loaded = await store.LoadAsync("feed-a", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("第5次千葉市", Assert.Single(loaded!.Seen).Title);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
