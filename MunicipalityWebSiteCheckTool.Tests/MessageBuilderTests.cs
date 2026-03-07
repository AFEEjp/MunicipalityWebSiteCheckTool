using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Messaging;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class MessageBuilderTests
{
    [Fact]
    public void BuildNewItemMessages_IncludeBasicFields()
    {
        // 通常の新規通知にタイトル・URL・キーワードが入ることを確認する。
        var builder = new MessageBuilder();
        var item = new FeedItem
        {
            ItemKey = "abc",
            Title = "意見募集",
            Url = "https://example.com/1",
            PublishedAt = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.FromHours(9))
        };

        var messages = builder.BuildNewItemMessages("サンプル", item, ["条例", "計画"]);

        var message = Assert.Single(messages);
        Assert.Contains("[新規検出] サンプル", message);
        Assert.Contains("件名: 意見募集", message);
        Assert.Contains("URL: https://example.com/1", message);
        Assert.Contains("一致キーワード: 条例, 計画", message);
    }

    [Fact]
    public void BuildPageChangedMessages_RenderDiffCodeBlock()
    {
        // ページ差分通知が diff コードブロックで返ることを確認する。
        var builder = new MessageBuilder();

        var messages = builder.BuildPageChangedMessages(
            "審議会",
            "https://example.com/page",
            "旧本文\n変更前",
            "旧本文\n変更後",
            "旧タイトル",
            "新タイトル");

        var joined = string.Join("\n---\n", messages);
        Assert.Contains("変更前タイトル: 旧タイトル", joined);
        Assert.Contains("変更後タイトル: 新タイトル", joined);
        Assert.Contains("```diff", joined);
        Assert.Contains("- 変更前", joined);
        Assert.Contains("+ 変更後", joined);
    }

    [Fact]
    public void SplitMessage_SplitLongTextByMaxLength()
    {
        // 指定文字数を超える本文は複数チャンクへ分割されることを確認する。
        var builder = new MessageBuilder();
        var message = new string('a', 25);

        var chunks = builder.SplitMessage(message, maxLength: 10);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 10));
    }

    [Fact]
    public void BuildRssHtmlMismatchMessages_IncludeCountAndThreshold()
    {
        // RSS想定URLでHTML応答が続いた通知文に、回数としきい値が含まれることを確認する。
        var builder = new MessageBuilder();

        var messages = builder.BuildRssHtmlMismatchMessages(
            "サンプル自治体",
            "https://example.com/feed.xml",
            mismatchCount: 4,
            notifyThreshold: 3);

        var message = Assert.Single(messages);
        Assert.Contains("[RSS取得異常]", message);
        Assert.Contains("対象: サンプル自治体", message);
        Assert.Contains("連続検知回数: 4 回", message);
        Assert.Contains("通知しきい値: 3 回", message);
    }

    [Fact]
    public void BuildFeedConsecutiveFailureMessages_IncludeNameAndId()
    {
        // 連続失敗通知に自治体名とIDを含め、調査対象を判別できることを確認する。
        var builder = new MessageBuilder();

        var messages = builder.BuildFeedConsecutiveFailureMessages(
            "東京都",
            "tokyo",
            "https://example.com/feed.xml",
            failureCount: 5,
            notifyThreshold: 3,
            errorMessage: "The request timed out.");

        var message = Assert.Single(messages);
        Assert.Contains("対象: 東京都 (tokyo)", message);
        Assert.Contains("連続失敗回数: 5 回", message);
        Assert.Contains("最新エラー: The request timed out.", message);
    }
}
