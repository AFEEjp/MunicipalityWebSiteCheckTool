namespace MunicipalityWebSiteCheckTool.Processors;

public sealed record FeedProcessResult
{
    public required string FeedId { get; init; }

    public required string FeedName { get; init; }

    public bool Succeeded { get; init; }

    public bool SkippedByCircuitBreaker { get; init; }

    public int NewItemCount { get; init; }

    public int TitleChangedCount { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<FeedDetectedItem> NewItems { get; init; } = [];

    public IReadOnlyList<FeedTitleChangedItem> TitleChangedItems { get; init; } = [];

    public IReadOnlyList<PendingNotification> PendingNotifications { get; init; } = [];
}

public sealed record FeedDetectedItem
{
    public string? Title { get; init; }

    public string? Url { get; init; }
}

public sealed record FeedTitleChangedItem
{
    public string? Url { get; init; }

    public string? OldTitle { get; init; }

    public string? NewTitle { get; init; }
}

public sealed record PendingNotification
{
    public required string WebhookUrl { get; init; }

    public required IReadOnlyList<string> Messages { get; init; }
}
