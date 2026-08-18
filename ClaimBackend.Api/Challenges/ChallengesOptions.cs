namespace ClaimBackend.Api.Challenges;

public class ChallengesOptions
{
    public const string SectionName = "Challenges";

    public required string TableName { get; set; }

    public int DefaultPageSize { get; set; } = 25;

    public int MaxPageSize { get; set; } = 100;
}
