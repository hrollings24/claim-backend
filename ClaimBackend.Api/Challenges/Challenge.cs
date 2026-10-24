namespace ClaimBackend.Api.Challenges;

/// <summary>
/// Which half of the deck a challenge belongs to. CLAIM cards are played on unclaimed
/// boroughs, STEAL cards on boroughs another team already holds.
/// </summary>
public enum ChallengeType
{
    Claim,
    Steal,
}

public class Challenge
{
    public required string Id { get; init; }
    public required ChallengeType Type { get; init; }

    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string FurtherDetails { get; init; }

    /// <summary>
    /// Steal only. How long the countdown runs once a team activates this steal in a game. Null
    /// for challenges written before this existed, or left blank since — <see cref="EffectiveStealMinutes"/>
    /// is what dealing actually reads.
    /// </summary>
    public int? StealMinutes { get; init; }

    /// <summary>
    /// How many power cards completing this challenge draws — harder challenges should draw
    /// more. The team keeps one of them. For a steal, it is what the defending team draws if the
    /// steal fails. Null for challenges written before this existed; see <see cref="EffectivePowerDraws"/>.
    /// </summary>
    public int? PowerDraws { get; init; }

    public required string CreatedBySub { get; init; }
    public required string CreatedByName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public const int DefaultStealMinutes = 5;

    public int EffectiveStealMinutes => StealMinutes ?? DefaultStealMinutes;

    public const int DefaultPowerDraws = 2;

    public int EffectivePowerDraws => PowerDraws ?? DefaultPowerDraws;

    /// <summary>
    /// Sort key within the single challenges partition. Time first so a query returns newest
    /// first, with the id appended to keep two challenges created in the same instant distinct.
    /// </summary>
    public string SortKey => $"{CreatedAt:O}#{Id}";
}

public record ChallengePage(IReadOnlyList<Challenge> Challenges, string? NextCursor);
