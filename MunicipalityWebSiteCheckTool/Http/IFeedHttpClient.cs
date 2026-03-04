using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Http;

public interface IFeedHttpClient
{
    /// <summary>
    /// 監視対象 URL を取得し、304 を含む取得結果を返す。
    /// 条件付き GET に使うキャッシュ情報は呼び出し側から渡す。
    /// </summary>
    Task<FetchResult> FetchAsync(string url, HttpCacheInfo? cache, CancellationToken cancellationToken);
}
