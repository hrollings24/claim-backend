namespace ClaimBackend.Api.Challenges;

public class ChallengesOptions
{
    public const string SectionName = "Challenges";

    public required string TableName { get; set; }

    public int DefaultPageSize { get; set; } = 25;

    public int MaxPageSize { get; set; } = 100;

    /// <summary>Ceiling on how many challenges are pulled into memory to deal a game's hands.</summary>
    public int MaxDeckSize { get; set; } = 500;
}
