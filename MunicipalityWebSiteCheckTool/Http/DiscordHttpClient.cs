using System.Text;

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
}
