namespace ClaimBackend.Api.Challenges;

public class Challenge
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string FurtherDetails { get; init; }
    public required string CreatedBySub { get; init; }
    public required string CreatedByName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Sort key within the single challenges partition. Time first so a query returns newest
    /// first, with the id appended to keep two challenges created in the same instant distinct.
    /// </summary>
    public string SortKey => $"{CreatedAt:O}#{Id}";
}

public record ChallengePage(IReadOnlyList<Challenge> Challenges, string? NextCursor);
