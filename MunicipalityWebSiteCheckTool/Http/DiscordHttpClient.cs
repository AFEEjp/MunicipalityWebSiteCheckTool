using System.Text;

namespace MunicipalityWebSiteCheckTool.Http;

public class DiscordHttpClient(HttpClient httpClient)
{
    public async Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(webhookUrl, content, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
