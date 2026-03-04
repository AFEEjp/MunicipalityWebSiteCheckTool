namespace MunicipalityWebSiteCheckTool.Http;

public interface IDiscordHttpClient
{
    /// <summary>
    /// Discord Webhook へ JSON ペイロードを POST する。
    /// 戻り値は HTTP ステータスベースの単純な成否とする。
    /// </summary>
    Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken);
}
