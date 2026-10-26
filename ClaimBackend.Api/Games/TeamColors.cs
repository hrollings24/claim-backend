namespace ClaimBackend.Api.Games;

/// <summary>
/// The colours a team can pick, which colour its boroughs on the map. Orange and white are never
/// in this list: the map uses them for the hot borough and for boroughs still on the board, so a
/// team can't be mistaken for either. The app holds the matching hex values under the same ids.
/// </summary>
public static class TeamColors
{
    public static readonly IReadOnlyList<string> All =
    [
        "red", "blue", "green", "purple", "pink", "teal",
        "yellow", "cyan", "lime", "indigo", "brown", "magenta",
    ];

    public static bool IsValid(string color) => All.Contains(color);

    /// <summary>The first colour nobody in the game has, or null once all twelve are taken.</summary>
    public static string? FirstFree(IEnumerable<GameTeam> teams)
    {
        var taken = teams.Select(t => t.Color).ToHashSet();
        return All.FirstOrDefault(c => !taken.Contains(c));
    }
}
