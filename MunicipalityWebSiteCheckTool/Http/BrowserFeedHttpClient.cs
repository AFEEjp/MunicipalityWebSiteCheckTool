using Microsoft.Playwright;
using MunicipalityWebSiteCheckTool.Config;
using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Http;

public sealed class BrowserFeedHttpClient : IBrowserFeedHttpClient
{
    /// <summary>
    /// Playwright で対象ページを表示し、描画後の HTML を取得する。
    /// SPA 対象のため 304 最適化は行わず、毎回レンダリング結果を取得する。
    /// </summary>
    public async Task<FetchResult> FetchAsync(FeedConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        _ = cancellationToken;

        var browserConfig = config.Browser ??
                            throw new InvalidOperationException($"browser 設定がありません。feedId={config.Id}");

        var timeoutMs = NormalizeTimeout(browserConfig.TimeoutMs);
        var waitUntil = ResolveWaitUntil(browserConfig.WaitUntil);
        var waitForSelector = string.IsNullOrWhiteSpace(browserConfig.WaitForSelector)
            ? "body"
            : browserConfig.WaitForSelector;

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);
        var page = await browser.NewPageAsync().ConfigureAwait(false);

        await page.GotoAsync(config.Url, new PageGotoOptions
        {
            WaitUntil = waitUntil,
            Timeout = timeoutMs
        }).ConfigureAwait(false);

        await page.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
        {
            Timeout = timeoutMs
        }).ConfigureAwait(false);

        var content = await page.ContentAsync().ConfigureAwait(false);
        var finalUrl = page.Url;

        return new FetchResult
        {
            Content = content,
            FinalUrl = finalUrl,
            NewCache = new HttpCacheInfo()
        };
    }

    /// <summary>
    /// タイムアウト値を安全な範囲に丸める。
    /// 過度に短い/長い値で監視全体が不安定にならないようにする。
    /// </summary>
    private static float NormalizeTimeout(int timeoutMs)
    {
        const int min = 1000;
        const int max = 20000;

        if (timeoutMs < min)
        {
            return min;
        }

        if (timeoutMs > max)
        {
            return max;
        }

        return timeoutMs;
    }

    /// <summary>
    /// 設定値の文字列を Playwright の待機条件へ変換する。
    /// 未指定や不正値は networkidle を既定値にする。
    /// </summary>
    private static WaitUntilState ResolveWaitUntil(string? waitUntil)
    {
        return waitUntil?.Trim().ToLowerInvariant() switch
        {
            "load" => WaitUntilState.Load,
            "domcontentloaded" => WaitUntilState.DOMContentLoaded,
            "networkidle" => WaitUntilState.NetworkIdle,
            _ => WaitUntilState.NetworkIdle
        };
    }
}
