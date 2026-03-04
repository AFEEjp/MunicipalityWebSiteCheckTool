using System.Text.Json;
using MunicipalityWebSiteCheckTool.Http;
using MunicipalityWebSiteCheckTool.Messaging;

namespace MunicipalityWebSiteCheckTool.Tests;

public sealed class DiscordNotifierTests
{
    [Fact]
    public async Task SendMessagesAsync_SendDiscordContentPayload()
    {
        // Discord 向け JSON が "content" で送られ、例外なく通ることを確認する。
        var client = new StubDiscordHttpClient();
        var notifier = new DiscordNotifier(client);

        var result = await notifier.SendMessagesAsync(
            "https://example.invalid/webhook",
            ["テスト通知"],
            CancellationToken.None);

        Assert.True(result);
        Assert.Single(client.PostedPayloads);
        using var document = JsonDocument.Parse(client.PostedPayloads[0]);
        Assert.Equal("テスト通知", document.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendMessagesAsync_ReturnFalseWhenAllRetriesFail()
    {
        // 送信失敗が続いた場合は false を返して上位へ伝えることを確認する。
        var client = new StubDiscordHttpClient(alwaysFail: true);
        var notifier = new DiscordNotifier(client);

        var result = await notifier.SendMessagesAsync(
            "https://example.invalid/webhook",
            ["テスト通知"],
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(3, client.CallCount);
    }

    private sealed class StubDiscordHttpClient(bool alwaysFail = false) : IDiscordHttpClient
    {
        public List<string> PostedPayloads { get; } = [];

        public int CallCount { get; private set; }

        /// <summary>
        /// 送信された JSON を記録し、テスト条件に応じて成功・失敗を返す。
        /// 実際の HTTP 通信は行わず、Notifier の制御だけを確認するためのスタブ。
        /// </summary>
        public Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken)
        {
            CallCount++;
            PostedPayloads.Add(json);
            return Task.FromResult(!alwaysFail);
        }
    }
}
