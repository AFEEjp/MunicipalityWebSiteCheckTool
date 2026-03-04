using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Feeds;

public interface IFeedSource
{
    string Type { get; }

    /// <summary>
    /// 取得済みの本文を監視対象ごとのフィード項目一覧へ変換する。
    /// HTTP 取得自体は別責務に切り出し、ここでは内容の解釈だけを担当する。
    /// </summary>
    IReadOnlyList<FeedItem> ParseItems(FeedConfig config, string content, string requestUrl);
}
