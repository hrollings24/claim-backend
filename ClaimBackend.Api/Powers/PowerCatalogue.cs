namespace ClaimBackend.Api.Powers;

public enum PowerKind
{
    /// <summary>Played at a borough another team holds; reveals a steal challenge and starts its clock.</summary>
    Steal,

    /// <summary>
    /// Played on another team. Honour system: the app only records that it was played — the team
    /// tells whoever they curse over the group chat, and that team sticks to it.
    /// </summary>
    Curse,

    /// <summary>Played on yourself. Honour system, same as a curse.</summary>
    Boost,
}

public record PowerDefinition(string Id, PowerKind Kind, string Title, string Description);

/// <summary>
/// The cards that live in code rather than in the table. Every other card is written by players
/// (see <see cref="PowerCardStore"/>), and how many of each go into a game's deck is up to the
/// <see cref="PowerConfiguration"/> the host picks.
/// </summary>
public static class PowerCatalogue
{
    public const string StealId = "steal";

    /// <summary>
    /// The one card with rules behind it — a target, a revealed challenge, a clock — so it can't
    /// be edited or deleted.
    /// </summary>
    public static readonly PowerDefinition Steal = new(
        StealId,
        PowerKind.Steal,
        "Steal",
        "Pick a borough another team holds. A steal challenge is revealed and the clock starts — finish it in time and the borough is yours.");

    public static readonly IReadOnlyList<PowerDefinition> BuiltIn = [Steal];

    /// <summary>
    /// Cards that shipped in code before players could write their own. Written to the table once,
    /// with these ids so configurations that already count them keep doing so, and kept here so
    /// games dealt before then still find their wording.
    /// </summary>
    public static readonly IReadOnlyList<PowerDefinition> Seed =
    [
        new("no-tube", PowerKind.Curse, "No tube", "The team you curse can't use the Underground for 20 minutes."),
    ];

    /// <summary>For games dealt before a game kept its own copy of each card's wording.</summary>
    public static PowerDefinition? FindLegacy(string id) =>
        BuiltIn.Concat(Seed).FirstOrDefault(p => p.Id == id);
}
