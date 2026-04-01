namespace MunicipalityWebSiteCheckTool.Messaging;

public sealed record DiscordAttachment
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Content { get; init; }
}
