namespace MunicipalityWebSiteCheckTool.Http;

public interface IDiscordHttpClient
{
    /// <summary>
    /// Discord Webhook へ JSON ペイロードを POST する。
    /// 戻り値は HTTP ステータスベースの単純な成否とする。
    /// </summary>
    Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken);

    /// <summary>
    /// Discord Webhook へ multipart/form-data を POST する。
    /// payload_json と添付ファイルを同時送信する用途で使う。
    /// </summary>
    Task<bool> PostMultipartAsync(
        string webhookUrl,
        string payloadJson,
        string fileName,
        string contentType,
        byte[] fileContent,
        CancellationToken cancellationToken);
}
