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
/// A borough a team holds. Locked territories are protected from being stolen until the timer
/// runs out, after which they are held exactly as before.
/// </summary>
public class Territory
{
    public required string BoroughId { get; init; }

    /// <summary>Null once the borough is contestable again.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;
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
