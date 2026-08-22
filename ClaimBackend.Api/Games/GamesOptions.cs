namespace ClaimBackend.Api.Games;

public class GamesOptions
{
    public const string SectionName = "Games";

    public required string TableName { get; set; }

    /// <summary>
    /// Points the client at DynamoDB Local for development. Left unset in AWS, where the
    /// default endpoint and the Lambda's execution role are what we want.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// How long a game survives without being written to. Abandoned lobbies are cleaned up by
    /// DynamoDB's TTL rather than by us, so a game nobody ever leaves properly doesn't sit in
    /// the table forever. The clock restarts on every write, not on reads — an in-progress game
    /// that nobody touches still ages out.
    /// </summary>
    public int TimeToLiveHours { get; set; } = 24;

    /// <summary>
    /// A game is stored as a single DynamoDB item (400KB limit), so the roster can't grow
    /// without bound.
    /// </summary>
    public int MaxPlayers { get; set; } = 12;

    /// <summary>
    /// Teams are deliberately not scarce, but they share the game's single item, so there is a
    /// ceiling. Raise it freely — the item limit is far above this.
    /// </summary>
    public int MaxTeams { get; set; } = 20;

    /// <summary>Starting length for a new game, in minutes. The host can change it in the lobby.</summary>
    public int DefaultDurationMinutes { get; set; } = 60;

    /// <summary>Boroughs on offer at any moment.</summary>
    public int ActiveBoroughCount { get; set; } = 6;

    public int HandSize { get; set; } = 5;

    /// <summary>How long the hot borough stands before moving. It also moves the moment it is claimed.</summary>
    public int HotRotationMinutes { get; set; } = 90;

    /// <summary>How long a team has to hit back after surviving a failed steal.</summary>
    public int CounterWindowMinutes { get; set; } = 15;
}
