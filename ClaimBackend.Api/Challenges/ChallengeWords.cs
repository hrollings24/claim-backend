namespace ClaimBackend.Api.Challenges;

/// <summary>
/// Fills in <c>{word}</c> in a steal challenge with a word picked when the steal is revealed — so
/// "Find the word" is set by the game rather than by the team being stolen from, who could stall
/// or pick something impossible. The same word goes everywhere it appears in that challenge.
/// </summary>
public static class ChallengeWords
{
    public const string Placeholder = "{word}";

    /// <summary>
    /// Words you'd expect to come across on London's streets — shopfronts, signs, posters, menus,
    /// pub names — from everywhere to only-if-you-look. None so rare it's hopeless in ten minutes.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "COFFEE", "MARKET", "STATION", "GARDEN", "CHURCH", "BAKERY", "LIBRARY", "PHARMACY",
        "BRIDGE", "SQUARE", "CHICKEN", "BARBER", "DENTIST", "FLORIST", "LAUNDRETTE", "ESTATE",
        "EXPRESS", "FAMILY", "VINTAGE", "ORGANIC", "PREMIUM", "QUALITY", "HERITAGE", "VICTORIA",
        "PALACE", "CROWN", "ANCHOR", "DRAGON", "LION", "ROSE", "GOLDEN", "ROYAL", "QUEEN", "KING",
        "CASTLE", "TIGER", "ORANGE", "SILVER", "RIVER", "PARK", "HOUSE", "HOTEL", "KITCHEN",
        "NOODLE", "KEBAB", "GRILL", "SUSHI", "VEGAN", "FRESH", "BURGER", "PIZZA", "TAILOR",
        "JEWELLERS", "SOLICITORS", "OPTICIANS", "GALLERY", "THEATRE", "CINEMA", "MUSEUM", "SCHOOL",
    ];

    public static bool HasPlaceholder(Challenge challenge) =>
        challenge.Title.Contains(Placeholder)
        || challenge.Summary.Contains(Placeholder)
        || challenge.FurtherDetails.Contains(Placeholder);

    public static string Pick() => All[Random.Shared.Next(All.Count)];

    public static string Fill(string text, string? word) =>
        word is null ? text : text.Replace(Placeholder, word);
}
