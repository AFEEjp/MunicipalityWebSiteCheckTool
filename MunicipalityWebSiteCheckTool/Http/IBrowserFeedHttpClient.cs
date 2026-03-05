using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Http;

public interface IBrowserFeedHttpClient
{
    /// <summary>
    /// ブラウザ描画が必要な監視対象ページを取得し、描画後の HTML と最終 URL を返す。
    /// browser モードは条件付き GET を使わないため、キャッシュ情報は空で返す。
    /// </summary>
    Task<FetchResult> FetchAsync(FeedConfig config, CancellationToken cancellationToken);
}
