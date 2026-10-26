namespace ClaimBackend.Api.Notifications;

public class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public required string TableName { get; set; }

    /// <summary>How long a notification stays in a player's inbox before DynamoDB's TTL removes it.</summary>
    public int KeepDays { get; set; } = 7;

    /// <summary>The most a player's inbox returns at once, newest first.</summary>
    public int MaxListed { get; set; } = 100;
}
