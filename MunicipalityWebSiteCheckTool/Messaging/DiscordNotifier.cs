using System.Text.Json;
using MunicipalityWebSiteCheckTool.Http;

namespace MunicipalityWebSiteCheckTool.Messaging;

public sealed class DiscordNotifier(IDiscordHttpClient discordHttpClient)
{
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MessageInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// 分割済みのメッセージ群を Discord Webhook へ順番に送信する。
    /// 1 件でも送信に失敗した場合は false を返し、呼び出し元でエラー扱いできるようにする。
    /// </summary>
    public async Task<bool> SendMessagesAsync(
        string webhookUrl,
        IEnumerable<string> messages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);
        ArgumentNullException.ThrowIfNull(messages);

        var normalizedMessages = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        if (normalizedMessages.Length == 0)
        {
            return true;
        }

        for (var index = 0; index < normalizedMessages.Length; index++)
        {
            var message = normalizedMessages[index];
            var succeeded = await SendSingleMessageWithRetryAsync(webhookUrl, message, cancellationToken).ConfigureAwait(false);
            if (!succeeded)
            {
                return false;
            }

            if (index < normalizedMessages.Length - 1)
            {
                // 連続投稿によるレート制限を避けるため、短い待機を挟む。
                await Task.Delay(MessageInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>
    /// 1 件の通知を短いリトライ付きで送信する。
    /// 一時的な通信失敗を吸収したいので、固定回数だけ再試行する。
    /// </summary>
    private async Task<bool> SendSingleMessageWithRetryAsync(
        string webhookUrl,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = CreatePayload(message);

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            var succeeded = await discordHttpClient.PostJsonAsync(webhookUrl, payload, cancellationToken).ConfigureAwait(false);
            if (succeeded)
            {
                return true;
            }

            if (attempt < MaxRetryCount)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Discord Webhook が受け取る最小限の JSON ペイロードを作る。
    /// 今回は本文送信だけを対象にし、username 等の拡張項目は後から追加しやすい形に留める。
    /// </summary>
    private static string CreatePayload(string message)
    {
        return JsonSerializer.Serialize(new DiscordWebhookPayload(message));
    }

    private sealed record DiscordWebhookPayload(string Content)
    {
        public string content { get; } = Content;
    }
}
