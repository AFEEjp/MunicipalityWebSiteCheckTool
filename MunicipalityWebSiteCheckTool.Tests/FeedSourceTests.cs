using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Feeds;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class FeedSourceTests
{
    [Fact]
    public void RssFeedSource_ParseItems_ReadRssItems()
    {
        // RSS 2.0 の item を複数件読み取れることを確認する。
        const string content = """
            <rss version="2.0">
              <channel>
                <item>
                  <title>  意見募集   条例案  </title>
                  <link>https://example.com/a</link>
                  <pubDate>Wed, 04 Mar 2026 12:00:00 +0900</pubDate>
                </item>
                <item>
                  <title>計画案</title>
                  <link>https://example.com/b</link>
                </item>
              </channel>
            </rss>
            """;

        var source = new RssFeedSource();
        var items = source.ParseItems(CreateFeedConfig("rss"), content, "https://example.com/feed.xml");

        Assert.Equal(2, items.Count);
        Assert.Equal("意見募集 条例案", items[0].Title);
        Assert.Equal("https://example.com/a", items[0].Url);
        Assert.NotNull(items[0].PublishedAt);
    }

    [Fact]
    public void RssFeedSource_ParseItems_ReadAtomEntries()
    {
        // Atom の entry でも URL とタイトルが取れることを確認する。
        const string content = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>意見募集</title>
                <id>tag:example.com,2026:1</id>
                <updated>2026-03-04T12:00:00+09:00</updated>
                <link rel="alternate" href="/notice/1" />
              </entry>
            </feed>
            """;

        var source = new RssFeedSource();
        var items = source.ParseItems(CreateFeedConfig("rss"), content, "https://example.com/feed");

        Assert.Single(items);
        Assert.Equal("https://example.com/notice/1", items[0].Url);
        Assert.Equal("意見募集", items[0].Title);
    }

    [Fact]
    public void HtmlFeedSource_ParseItems_ReadAnchorsAndSkipMailto()
    {
        // a タグを抽出し、mailto は除外することを確認する。
        const string content = """
            <html>
              <body>
                <a href="/notice/1">意見募集</a>
                <a href="mailto:test@example.com">mail</a>
              </body>
            </html>
            """;

        var source = new HtmlFeedSource();
        var items = source.ParseItems(CreateFeedConfig("html"), content, "https://example.com/list");

        Assert.Single(items);
        Assert.Equal("https://example.com/notice/1", items[0].Url);
        Assert.Equal("意見募集", items[0].Title);
    }

    [Fact]
    public void HtmlFeedSource_ParseItems_RestoreUrlFromOnclick()
    {
        // onclick に埋め込まれた URL を復元できることを確認する。
        const string content = """
            <html>
              <body>
                <a href="javascript:void(0)" onclick="window.open('/detail/2');">詳細ページ</a>
              </body>
            </html>
            """;

        var source = new HtmlFeedSource();
        var items = source.ParseItems(CreateFeedConfig("html"), content, "https://example.com/list");

        Assert.Single(items);
        Assert.Equal("https://example.com/detail/2", items[0].Url);
        Assert.Equal("詳細ページ", items[0].Title);
    }

    private static FeedConfig CreateFeedConfig(string type)
    {
        return new FeedConfig
        {
            Id = "test",
            Name = "test",
            Url = "https://example.com",
            Type = type,
            Match = new MatchConfig
            {
                First = [],
                Second = [],
                Exclude = []
            }
        };
    }
}
