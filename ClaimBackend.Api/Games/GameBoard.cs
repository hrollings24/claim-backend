using ClaimBackend.Api.Challenges;

namespace ClaimBackend.Api.Games;

/// <summary>
/// A borough currently in play as a claimable target. Everything in the active pool is
/// unclaimed by definition — claiming one moves it into the claiming team's territory and a
/// replacement takes its place.
/// </summary>
public class ActiveBorough
{
    public required string BoroughId { get; init; }
}

/// <summary>
/// A borough a team holds. Any of them can be stolen — steals being rare is what stops boroughs
/// bouncing back and forth — unless its holder played Fortify on it.
/// </summary>
public class Territory
{
    public required string BoroughId { get; init; }

    /// <summary>Set by Fortify; it can't be stolen for the rest of the game.</summary>
    public bool Fortified { get; set; }
}

/// <summary>
/// One claim challenge in a team's hand, copied from the challenge it was dealt from so a later
/// edit to the challenge doesn't change a card already in someone's hand.
/// </summary>
public class HandCard
{
    public required string Id { get; init; }
    public required string ChallengeId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string FurtherDetails { get; init; }

    /// <summary>
    /// How many power cards completing it draws. Not required, so games dealt before power cards
    /// existed still read back.
    /// </summary>
    public int PowerDraws { get; init; } = Challenge.DefaultPowerDraws;
}

public class TeamHand
{
    public required string TeamId { get; init; }
    public required List<HandCard> Cards { get; init; }
}

public class GameBoard
{
    /// <summary>The boroughs on offer. Always unclaimed, always topped back up to six.</summary>
    public required List<ActiveBorough> Active { get; init; }

    /// <summary>Boroughs not yet in play, drawn from when the pool is topped up.</summary>
    public required List<string> Remaining { get; init; }

    /// <summary>
    /// Null when nothing on the board is eligible — every active borough has already been hot.
    /// It comes back as soon as a fresh borough is drawn onto the board.
    /// </summary>
    public string? HotBoroughId { get; set; }

    /// <summary>
    /// Every borough that has been hot and moved on, whether it was claimed or rotated away. None
    /// of them can be hot again. The current one is added when it moves, not when it is chosen,
    /// so games dealt before this existed pick up their current hot borough the first time it
    /// rotates.
    /// </summary>
    public List<string> FormerHotBoroughIds { get; init; } = [];

    public required DateTimeOffset HotRotatesAt { get; set; }
}
