namespace ClaimBackend.Api.Powers;

/// <summary>
/// Stored as numbers inside each game, so every value is pinned: removing or reordering one must
/// never renumber the rest. 3 was Veto, since removed — don't reuse it.
/// </summary>
public enum PowerKind
{
    /// <summary>Played at a borough another team holds; reveals a steal challenge and starts its clock.</summary>
    Steal = 0,

    /// <summary>
    /// Played on another team. Honour system: the app only records that it was played — the team
    /// tells whoever they curse over the group chat, and that team sticks to it.
    /// </summary>
    Curse = 1,

    /// <summary>Played on yourself. Honour system, same as a curse.</summary>
    Boost = 2,

    /// <summary>Used on a curse aimed at you to send it back to the team that cast it.</summary>
    Reverse = 4,

    /// <summary>Played on one of your boroughs to lock it for the rest of the game.</summary>
    Fortify = 5,

    /// <summary>Played on another team: see their power cards, take one, give one back.</summary>
    Swap = 6,
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

    public static readonly PowerDefinition Reverse = new(
        "reverse",
        PowerKind.Reverse,
        "Curse Reverse",
        "Send a curse played on you back to the team that cast it — it starts again on them. A curse that's already been reversed can't be reversed again.");

    public static readonly PowerDefinition Fortify = new(
        "fortify",
        PowerKind.Fortify,
        "Fortify",
        "Lock one of your boroughs for the rest of the game — nobody can steal it. Not while a steal on it is already running.");

    public static readonly PowerDefinition Swap = new(
        "swap",
        PowerKind.Swap,
        "Swap",
        "Pick another team. Their power cards are revealed to you: take the one you want, and give them one of yours.");

    /// <summary>The cards with rules behind them, which nobody can edit or delete.</summary>
    public static readonly IReadOnlyList<PowerDefinition> BuiltIn = [Steal, Reverse, Fortify, Swap];

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
