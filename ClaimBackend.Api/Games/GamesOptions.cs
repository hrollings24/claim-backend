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

    /// <summary>
    /// Bounds on how many boroughs are on offer at any moment. The host picks within them; left
    /// to itself a game puts <see cref="BoardSizeOverTeams"/> more than it has teams on the board,
    /// so every team has about the same amount of choice however many are playing — never more
    /// than <see cref="MaxAutomaticBoardSize"/>. Bigger boards cut travel, and so let teams
    /// claim more in a day, but they're a choice for the host rather than the default.
    /// </summary>
    public int MinBoardSize { get; set; } = 4;

    public int MaxBoardSize { get; set; } = 16;

    public int MaxAutomaticBoardSize { get; set; } = 10;

    public int BoardSizeOverTeams { get; set; } = 4;

    public int HandSize { get; set; } = 5;

    /// <summary>Unplayed power cards a team may hold at once. Played ones don't count.</summary>
    public int MaxPowerCards { get; set; } = 5;

    /// <summary>
    /// Claiming the hot borough multiplies both how many power cards are drawn and how many the
    /// team may keep.
    /// </summary>
    public int HotClaimPowerMultiplier { get; set; } = 2;

    /// <summary>
    /// Starting value for how long the hot borough stands before moving. It also moves the
    /// moment it is claimed. The host can change it in the lobby.
    /// </summary>
    public int DefaultHotRotationMinutes { get; set; } = 90;

    /// <summary>How close to the end a game has to be before players are warned it is nearly over.</summary>
    public int EndingSoonMinutes { get; set; } = 15;
}
