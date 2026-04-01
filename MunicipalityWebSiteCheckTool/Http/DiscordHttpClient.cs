using System.Text;
using System.Net.Http.Headers;

namespace MunicipalityWebSiteCheckTool.Http;

public class DiscordHttpClient(HttpClient httpClient) : IDiscordHttpClient
{
    /// <summary>
    /// Discord Webhook へ JSON ペイロードをそのまま POST する。
    /// ステータスコードだけで成否を返し、詳細なリトライ制御は上位層に委ねる。
    /// </summary>
    public async Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(webhookUrl, content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Discord Webhook へ payload_json と添付ファイルを multipart で送信する。
    /// 添付失敗時のフォールバック判断は上位層へ委ね、ここではHTTP成否のみ返す。
    /// </summary>
    public async Task<bool> PostMultipartAsync(
        string webhookUrl,
        string payloadJson,
        string fileName,
        string contentType,
        byte[] fileContent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(fileContent);

        using var multipart = new MultipartFormDataContent();
        using var payloadContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        using var filePart = new ByteArrayContent(fileContent);
        filePart.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        multipart.Add(payloadContent, "payload_json");
        multipart.Add(filePart, "files[0]", fileName);

        using var response = await httpClient.PostAsync(webhookUrl, multipart, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
