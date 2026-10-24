namespace ClaimBackend.Api.Powers;

public enum PowerKind
{
    /// <summary>Played at a borough another team holds; reveals a steal challenge and starts its clock.</summary>
    Steal,

    /// <summary>
    /// Honour system. The app only records that it was played — the team tells whoever they
    /// curse over the group chat, and that team sticks to it.
    /// </summary>
    Curse,
}

public record PowerDefinition(string Id, PowerKind Kind, string Title, string Description);

/// <summary>
/// Every kind of power card there is. How many of each go into a game's deck is up to the
/// <see cref="PowerConfiguration"/> the host picks. A card already drawn keeps the wording it
/// was drawn with, so rewording one here doesn't change cards in play.
/// </summary>
public static class PowerCatalogue
{
    public static readonly IReadOnlyList<PowerDefinition> All =
    [
        new("steal", PowerKind.Steal, "Steal",
            "Pick a borough another team holds. A steal challenge is revealed and the clock starts — finish it in time and the borough is yours."),
        new("no-tube", PowerKind.Curse, "No tube",
            "The team you curse can't use the Underground for 20 minutes."),
    ];

    public static PowerDefinition? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
