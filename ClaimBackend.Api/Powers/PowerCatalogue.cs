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

    /// <summary>Used on a curse aimed at you, or a steal on one of your boroughs, to cancel it.</summary>
    Veto,

    /// <summary>Used on a curse aimed at you to send it back to the team that cast it.</summary>
    Reverse,
}

/// <param name="DurationMinutes">
/// Curse only, and optional. A timed curse lifts on its own when the time is up; one without a
/// time lasts until the cursed team marks it cleared.
/// </param>
public record PowerDefinition(
    string Id, PowerKind Kind, string Title, string Description, int? DurationMinutes = null);

/// <summary>
/// The cards that live in code rather than in the table. Every other card is written by players
/// (see <see cref="PowerCardStore"/>), and how many of each go into a game's deck is up to the
/// <see cref="PowerConfiguration"/> the host picks.
/// </summary>
public static class PowerCatalogue
{
    public const string StealId = "steal";

    /// <summary>A target, a revealed challenge and a clock.</summary>
    public static readonly PowerDefinition Steal = new(
        StealId,
        PowerKind.Steal,
        "Steal",
        "Pick a borough another team holds. A steal challenge is revealed and the clock starts — finish it in time and the borough is yours.");

    public static readonly PowerDefinition Veto = new(
        "veto",
        PowerKind.Veto,
        "Veto",
        "Cancel a curse played on you, or stop a steal on one of your boroughs. Use it from the curse or the steal itself.");

    public static readonly PowerDefinition Reverse = new(
        "reverse",
        PowerKind.Reverse,
        "Curse Reverse",
        "Send a curse played on you back to the team that cast it — it starts again on them. A curse that's already been reversed can't be reversed again.");

    /// <summary>The cards with rules behind them, which nobody can edit or delete.</summary>
    public static readonly IReadOnlyList<PowerDefinition> BuiltIn = [Steal, Veto, Reverse];

    /// <summary>
    /// Cards that shipped in code before players could write their own. Written to the table once,
    /// with these ids so configurations that already count them keep doing so, and kept here so
    /// games dealt before then still find their wording.
    /// </summary>
    public static readonly IReadOnlyList<PowerDefinition> Seed =
    [
        new("no-tube", PowerKind.Curse, "No tube", "The team you curse can't use the Underground for 20 minutes.", 20),
    ];

    /// <summary>For games dealt before a game kept its own copy of each card's wording.</summary>
    public static PowerDefinition? FindLegacy(string id) =>
        BuiltIn.Concat(Seed).FirstOrDefault(p => p.Id == id);
}
