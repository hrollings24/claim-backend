using System.Text.Json.Serialization;
using ClaimBackend.Api.Powers;

namespace ClaimBackend.Api.Games;

public enum StealOutcome
{
    Succeeded,
    Failed,

    /// <summary>The clock ran out before anyone entered a result. Counts as a failure.</summary>
    TimedOut,
}

/// <summary>
/// The steal a played Steal card set going: the target, the challenge it revealed, and the
/// clock. Copied from the challenge when played, so editing the challenge mid-steal changes
/// nothing.
/// </summary>
public class StealAttempt
{
    public required string BoroughId { get; init; }
    public required string ChallengeId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string FurtherDetails { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>What the defending team draws if the steal fails.</summary>
    public required int PowerDraws { get; init; }

    /// <summary>Null while the clock is running.</summary>
    public StealOutcome? Outcome { get; set; }
}

/// <summary>
/// A power card a team holds. Playing one doesn't remove it: it stays, marked as played, so the
/// team can still see what they used and, for a steal, how it went.
/// </summary>
public class PowerCard
{
    public required string Id { get; init; }
    public required string PowerId { get; init; }
    public required PowerKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public DateTimeOffset? PlayedAt { get; set; }

    /// <summary>Steal only, set when it is played.</summary>
    public StealAttempt? Steal { get; set; }

    [JsonIgnore]
    public bool IsPlayed => PlayedAt is not null;
}

/// <summary>
/// Power cards drawn and waiting for the team to pick which to keep. Whatever isn't kept is
/// discarded. Offers queue up, so a team that claims twice before choosing loses nothing.
/// </summary>
public class PowerOffer
{
    public required string Id { get; init; }

    /// <summary>What earned it, for the app to show — "Claimed Camden", "Survived a steal".</summary>
    public required string Reason { get; init; }

    public required List<PowerCard> Cards { get; init; }

    /// <summary>How many of <see cref="Cards"/> the team may keep.</summary>
    public required int Keep { get; init; }
}

/// <summary>
/// The game's one shared stack of power cards, built from its configuration's counts and
/// shuffled at the start. Cards a team doesn't keep — or later discards — go on the discard
/// pile, which is shuffled back in when the draw pile runs out. Cards kept or played never come
/// back, so a deck of five Steals never puts more than five into play.
/// </summary>
public class PowerDeck
{
    /// <summary>Power card ids, top of the pile first.</summary>
    public required List<string> DrawPile { get; init; }

    public required List<string> DiscardPile { get; init; }

    /// <summary>
    /// The wording of every card in the deck, copied when the game starts, so a card edited or
    /// deleted mid-game still reads as it did when it was dealt. Empty for games dealt before
    /// this was kept, which fall back to <see cref="PowerCatalogue.FindLegacy"/>.
    /// </summary>
    public List<PowerDefinition> Cards { get; init; } = [];

    public PowerDefinition? Find(string id) =>
        Cards.FirstOrDefault(c => c.Id == id) ?? PowerCatalogue.FindLegacy(id);
}

public class TeamPowers
{
    public required string TeamId { get; init; }
    public required List<PowerCard> Cards { get; init; }
    public required List<PowerOffer> Offers { get; init; }

    [JsonIgnore]
    public int UnplayedCount => Cards.Count(c => !c.IsPlayed);
}
