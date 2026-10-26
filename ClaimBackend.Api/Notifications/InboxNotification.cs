namespace ClaimBackend.Api.Notifications;

/// <summary>
/// One entry in a player's inbox: a copy of a notification the game sent them, kept whether or
/// not push reached any of their devices.
/// </summary>
public class InboxNotification
{
    public required string Sub { get; init; }

    /// <summary>
    /// Creation time in milliseconds, zero-padded so ids sort by time, then a random suffix so two
    /// sent in the same instant stay distinct. Safe to put in a URL as it is.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The game it's about, if any.</summary>
    public string? GameCode { get; init; }

    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>Where tapping it goes in the app.</summary>
    public string? Url { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static string NewId(DateTimeOffset createdAt) =>
        $"{createdAt.ToUnixTimeMilliseconds():D13}-{Guid.NewGuid():N}";
}

public record InboxNotificationDto(
    string Id, string? GameCode, string Title, string Body, string? Url, DateTimeOffset CreatedAt);
