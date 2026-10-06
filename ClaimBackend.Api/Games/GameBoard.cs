using System.Text.Json.Serialization;
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
/// A borough a team holds. A locked one is out of contention for the rest of the game — taking
/// it by stealing, or claiming it while hot, settles it permanently.
/// </summary>
public class Territory
{
    public required string BoroughId { get; init; }

    public bool Locked { get; set; }

    /// <summary>
    /// Locks used to lapse after a timer. Kept only so games dealt before that changed still
    /// read back with their locked boroughs locked.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Derived, so it isn't stored — it would be a second copy of the same fact.</summary>
    [JsonIgnore]
    public bool IsLocked => Locked || LockedUntil is not null;
}

/// <summary>One card in a team's hand, copied from the challenge it was dealt from.</summary>
public class HandCard
{
    public required string Id { get; init; }
    public required string ChallengeId { get; init; }
    public required ChallengeType Type { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string FurtherDetails { get; init; }

    /// <summary>
    /// Steal only, fixed at deal time so a later edit to the challenge doesn't change a card
    /// already in someone's hand.
    /// </summary>
    public int? StealMinutes { get; init; }

    /// <summary>
    /// Steal only. Null until the team commits to a target — the challenge stays hidden and the
    /// clock hasn't started until then.
    /// </summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>The target chosen when activating. Set together with <see cref="ActivatedAt"/>.</summary>
    public string? ActivatedBoroughId { get; set; }
}

public class TeamHand
{
    public required string TeamId { get; init; }
    public required List<HandCard> Cards { get; init; }
}

/// <summary>
/// The consolation a team gets when someone fails a steal against them: a short window in which
/// a successful claim challenge takes a borough off the team that attacked them.
/// </summary>
public class CounterWindow
{
    /// <summary>The team that was attacked, and may now counter.</summary>
    public required string TeamId { get; init; }

    /// <summary>The team whose territory they may take one of.</summary>
    public required string AgainstTeamId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public class GameBoard
{
    /// <summary>The boroughs on offer. Always unclaimed, always topped back up to six.</summary>
    public required List<ActiveBorough> Active { get; init; }

    /// <summary>Boroughs not yet in play, drawn from when the pool is topped up.</summary>
    public required List<string> Remaining { get; init; }

    /// <summary>Null only if every active borough somehow ceased to exist.</summary>
    public string? HotBoroughId { get; set; }

    public required DateTimeOffset HotRotatesAt { get; set; }
}
