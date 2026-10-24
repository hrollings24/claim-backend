using System.ComponentModel.DataAnnotations;

namespace ClaimBackend.Api.Powers;

/// <summary>
/// A named recipe for a game's power deck — how many of each card go in. Shared by everyone and
/// editable by anyone. A game copies the counts when it starts, so editing one never changes a
/// game already under way.
/// </summary>
public class PowerConfiguration
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    /// <summary>Power card id to how many copies. Ids of cards since deleted are ignored.</summary>
    public required Dictionary<string, int> Counts { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int CountOf(string powerId) => Counts.GetValueOrDefault(powerId);

    /// <summary>Cards the deck will actually hold — only those that still exist.</summary>
    public int TotalCards(IEnumerable<PowerDefinition> cards) => cards.Sum(c => CountOf(c.Id));
}

/// <summary>One row per card that exists, zeros included, so the editor can list them all.</summary>
public record PowerCountDto(string PowerId, string Kind, string Title, string Description, int Count);

public record PowerConfigurationDto(string Id, string Name, IReadOnlyList<PowerCountDto> Cards, int TotalCards);

/// <summary>
/// <paramref name="Counts"/> maps power card id to copies, each 0–<see cref="PowerConfigurationStore.MaxCopies"/>.
/// It replaces the whole set, so a card left out counts as zero.
/// </summary>
public record SavePowerConfigurationRequest(
    [Required, StringLength(40, MinimumLength = 1)] string Name,
    [Required] Dictionary<string, int> Counts);

/// <param name="IsBuiltIn">Steal, Veto and Curse Reverse have rules behind them, so nobody can edit or delete them.</param>
/// <param name="DurationMinutes">Curse only: how long it lasts. Null lasts until the cursed team clears it.</param>
public record PowerCardDefinitionDto(
    string Id, string Kind, string Title, string Description, int? DurationMinutes, bool IsBuiltIn);

/// <summary>
/// <paramref name="Kind"/> is Curse or Boost — the others are built in.
/// <paramref name="DurationMinutes"/> is kept for a curse only.
/// </summary>
public record SavePowerCardRequest(
    [Required] PowerKind Kind,
    [Required, StringLength(60, MinimumLength = 1)] string Title,
    [Required, StringLength(500, MinimumLength = 1)] string Description,
    [Range(1, 240)] int? DurationMinutes);
