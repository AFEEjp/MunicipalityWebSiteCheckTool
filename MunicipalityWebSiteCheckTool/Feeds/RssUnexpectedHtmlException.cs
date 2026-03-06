namespace MunicipalityWebSiteCheckTool.Feeds;

public sealed class RssUnexpectedHtmlException : Exception
{
    public RssUnexpectedHtmlException(string feedId, Exception innerException)
        : base($"RSS 想定の取得先が HTML を返しました。feedId={feedId}", innerException)
    {
        FeedId = feedId;
    }

    public string FeedId { get; }
}
