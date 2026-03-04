using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MunicipalityWebSiteCheckTool.Domain;
using UtfUnknown;

namespace MunicipalityWebSiteCheckTool.Http;

public class FeedHttpClient(HttpClient httpClient)
{
    static FeedHttpClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<FetchResult> FetchAsync(string url, HttpCacheInfo? cache, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(cache?.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        if (cache?.LastModifiedUtc is not null)
        {
            request.Headers.IfModifiedSince = new DateTimeOffset(DateTime.SpecifyKind(cache.LastModifiedUtc.Value, DateTimeKind.Utc));
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var newCache = new HttpCacheInfo
        {
            ETag = response.Headers.ETag?.Tag,
            LastModifiedUtc = response.Content.Headers.LastModified?.UtcDateTime
        };

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new FetchResult
            {
                Content = null,
                FinalUrl = finalUrl,
                NewCache = new HttpCacheInfo
                {
                    ETag = newCache.ETag ?? cache?.ETag,
                    LastModifiedUtc = newCache.LastModifiedUtc ?? cache?.LastModifiedUtc
                }
            };
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var detected = CharsetDetector.DetectFromStream(memoryStream);
        memoryStream.Position = 0;

        var encoding = detected.Detected?.Encoding ?? Encoding.UTF8;
        using var reader = new StreamReader(memoryStream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var content = await reader.ReadToEndAsync(cancellationToken);

        return new FetchResult
        {
            Content = content,
            FinalUrl = finalUrl,
            NewCache = newCache
        };
    }
}
