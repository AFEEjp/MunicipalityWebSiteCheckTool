using System.Collections.Immutable;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;
using MunicipalityWebSiteCheckTool.Processing;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class ProcessingTests
{
    [Fact]
    public void UrlNormalizer_ToItemKey_IgnoreKnownUrlNoise()
    {
        // ref=rss とスキーム差分を吸収して、同じ項目キーになることを確認する。
        var first = UrlNormalizer.ToItemKey("https://example.com/path?ref=rss");
        var second = UrlNormalizer.ToItemKey("http://example.com/path");

        Assert.Equal(first, second);
    }

    [Fact]
    public void KeywordMatcher_IsMatch_ApplyFirstSecondExclude()
    {
        // 第1語と第2語を満たし、除外語が無い場合だけ一致にする。
        var match = new MatchConfig
        {
            First = ["意見募集"],
            Second = ["条例"],
            Exclude = ["終了"]
        };

        Assert.True(KeywordMatcher.IsMatch("意見募集に関する条例案", match));
        Assert.False(KeywordMatcher.IsMatch("意見募集は終了しました", match));
        Assert.False(KeywordMatcher.IsMatch("条例案のみ", match));
    }

    [Fact]
    public void KeywordMatcher_DetectKeywords_ReturnDistinctMatches()
    {
        // 同じ語が複数回出ても一度だけ返すことを確認する。
        var match = new MatchConfig
        {
            First = ["意見募集"],
            Second = ["条例", "意見募集"],
            Exclude = []
        };

        var result = KeywordMatcher.DetectKeywords("意見募集と条例に関する意見募集", match);

        Assert.Equal(["意見募集", "条例"], result);
    }

    [Fact]
    public void SeenList_Add_TrimOldEntriesByMaxSeen()
    {
        // 上限を超えたら古い項目から削ることを確認する。
        var seen = ImmutableList<SeenEntry>.Empty;
        seen = SeenList.Add(seen, CreateSeenEntry("a", "A"), maxSeen: 2);
        seen = SeenList.Add(seen, CreateSeenEntry("b", "B"), maxSeen: 2);
        seen = SeenList.Add(seen, CreateSeenEntry("c", "C"), maxSeen: 2);

        Assert.Equal(2, seen.Count);
        Assert.DoesNotContain(seen, entry => entry.Key == "a");
        Assert.Contains(seen, entry => entry.Key == "b");
        Assert.Contains(seen, entry => entry.Key == "c");
    }

    [Fact]
    public void SeenList_UpdateTitle_UpdateMatchedEntryOnly()
    {
        // 対象キーに一致する項目だけタイトルが変わることを確認する。
        var seen = ImmutableList.Create(
            CreateSeenEntry("a", "Old A"),
            CreateSeenEntry("b", "Old B"));

        var updated = SeenList.UpdateTitle(seen, "b", "New B");

        Assert.Equal("Old A", updated[0].Title);
        Assert.Equal("New B", updated[1].Title);
    }

    private static SeenEntry CreateSeenEntry(string key, string title)
    {
        return new SeenEntry
        {
            Key = key,
            Title = title,
            FirstSeenAt = DateTimeOffset.UtcNow
        };
    }
}
