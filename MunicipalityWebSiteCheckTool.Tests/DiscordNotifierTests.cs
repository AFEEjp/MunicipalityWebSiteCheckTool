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

    [Fact]
    public async Task SendMessagesWithOptionalAttachmentAsync_SendAttachmentOnFirstMessage()
    {
        // 添付付き通知では、先頭メッセージが multipart 送信されることを確認する。
        var client = new StubDiscordHttpClient();
        var notifier = new DiscordNotifier(client);
        var attachment = new DiscordAttachment
        {
            FileName = "diagnostic.zip",
            ContentType = "application/zip",
            Content = [1, 2, 3]
        };

        var result = await notifier.SendMessagesWithOptionalAttachmentAsync(
            "https://example.invalid/webhook",
            ["テスト通知1", "テスト通知2"],
            attachment,
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, client.MultipartCallCount);
        Assert.Single(client.MultipartPayloads);
        Assert.Equal("diagnostic.zip", client.MultipartPayloads[0].FileName);
        Assert.Equal(1, client.JsonCallCount);
    }

    [Fact]
    public async Task SendMessagesWithOptionalAttachmentAsync_FallbackToPlainTextWhenAttachmentFails()
    {
        // 添付送信が失敗しても、本文送信へフォールバックして通知を継続することを確認する。
        var client = new StubDiscordHttpClient(alwaysFailMultipart: true);
        var notifier = new DiscordNotifier(client);
        var attachment = new DiscordAttachment
        {
            FileName = "diagnostic.zip",
            ContentType = "application/zip",
            Content = [1, 2, 3]
        };

        var result = await notifier.SendMessagesWithOptionalAttachmentAsync(
            "https://example.invalid/webhook",
            ["テスト通知"],
            attachment,
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, client.MultipartCallCount);
        Assert.Equal(1, client.JsonCallCount);
        Assert.Single(client.PostedPayloads);
    }

    private sealed class StubDiscordHttpClient(bool alwaysFail = false, bool alwaysFailMultipart = false) : IDiscordHttpClient
    {
        public List<string> PostedPayloads { get; } = [];
        public List<MultipartRecord> MultipartPayloads { get; } = [];

        public int CallCount { get; private set; }
        public int JsonCallCount { get; private set; }
        public int MultipartCallCount { get; private set; }

        /// <summary>
        /// 送信された JSON を記録し、テスト条件に応じて成功・失敗を返す。
        /// 実際の HTTP 通信は行わず、Notifier の制御だけを確認するためのスタブ。
        /// </summary>
        public Task<bool> PostJsonAsync(string webhookUrl, string json, CancellationToken cancellationToken)
        {
            CallCount++;
            JsonCallCount++;
            PostedPayloads.Add(json);
            return Task.FromResult(!alwaysFail);
        }

        public Task<bool> PostMultipartAsync(
            string webhookUrl,
            string payloadJson,
            string fileName,
            string contentType,
            byte[] fileContent,
            CancellationToken cancellationToken)
        {
            CallCount++;
            MultipartCallCount++;
            MultipartPayloads.Add(new MultipartRecord
            {
                PayloadJson = payloadJson,
                FileName = fileName,
                ContentType = contentType,
                FileLength = fileContent.Length
            });

            return Task.FromResult(!alwaysFailMultipart && !alwaysFail);
        }

        public sealed record MultipartRecord
        {
            public required string PayloadJson { get; init; }

            public required string FileName { get; init; }

            public required string ContentType { get; init; }

            public required int FileLength { get; init; }
        }
    }
}
