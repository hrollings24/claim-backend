using System.ComponentModel.DataAnnotations;

namespace ClaimBackend.Api.Games;

/// <summary>
/// A player as the lobby shows them. Cognito subs stay server-side — the client only needs to
/// know who to label as host and which row is the caller.
/// </summary>
public record GamePlayerDto(string Name, bool IsHost, bool IsYou, string? TeamId);

public record GameTeamDto(string Id, string Name);

public record GameDto(
    string Code,
    string Status,
    bool YouAreHost,
    string? YourTeamId,
    int DurationMinutes,
    int HotRotationMinutes,
    string? PowerConfigurationId,
    string? PowerConfigurationName,
    GameBoardDto? Board,
    IReadOnlyList<GamePlayerDto> Players,
    IReadOnlyList<GameTeamDto> Teams);

/// <summary>
/// The display name comes from the caller because it lives on the Cognito *id* token, while the
/// API is called with the *access* token, which carries no name claim. That makes it
/// self-asserted: good enough for a lobby, but it can't be treated as verified identity. To
/// harden it, the API would have to call Cognito's GetUser with the caller's access token.
/// </summary>
public record GameMembershipRequest(string? DisplayName);

public record CreateTeamRequest(
    [Required, StringLength(40, MinimumLength = 1)] string Name);

/// <summary>
/// A single total in minutes rather than separate hours and minutes fields — the lobby splits
/// it for display, and one number can't express a contradictory pair.
/// </summary>
public record SetDurationRequest(
    [Range(1, 24 * 60)] int DurationMinutes);

public record SetPowerConfigurationRequest([Required] string ConfigurationId);

/// <summary>How long the hot borough stands before moving on its own.</summary>
public record SetHotRotationRequest(
    [Range(1, 24 * 60)] int HotRotationMinutes);

public record ActiveBoroughDto(string Id, string Name, string Zone, bool IsHot);

public record TerritoryDto(string Id, string Name, string TeamId, string TeamName);

/// <param name="PowerDraws">How many power cards completing it draws; doubled on the hot borough.</param>
public record HandCardDto(
    string Id,
    string Title,
    string Summary,
    string FurtherDetails,
    int PowerDraws);

public record TeamScoreDto(string TeamId, string Name, int Territories, int BonusPoints, int Score);

/// <summary>
/// The challenge a played Steal revealed, and how it stands. <paramref name="Outcome"/> is null
/// while the clock is running, then Succeeded, Failed or TimedOut.
/// </summary>
public record StealDto(
    string BoroughId,
    string BoroughName,
    string Title,
    string Summary,
    string FurtherDetails,
    DateTimeOffset ExpiresAt,
    string? Outcome);

/// <summary>
/// A power card. Played ones stay in the list, marked by <paramref name="PlayedAt"/>, so the team
/// can see what they have used.
/// </summary>
public record PowerCardDto(
    string Id,
    string Kind,
    string Title,
    string Description,
    DateTimeOffset? PlayedAt,
    StealDto? Steal);

/// <summary>Cards drawn and waiting for the team to keep up to <paramref name="Keep"/> of them.</summary>
public record PowerOfferDto(string Id, string Reason, int Keep, IReadOnlyList<PowerCardDto> Cards);

/// <summary>
/// Everything a player needs to decide their next move: what's on offer, who holds what, the
/// cards in their own hand, their power cards, and the clock. Hand and power cards are only ever
/// the caller's own team's.
/// </summary>
/// <param name="MaxPowerCards">How many unplayed power cards a team may hold at once.</param>
/// <param name="PowerDeckCount">Cards left to draw before the discard pile is shuffled back in.</param>
/// <param name="PowerDiscardCount">Cards waiting on the discard pile.</param>
public record GameBoardDto(
    IReadOnlyList<ActiveBoroughDto> Active,
    string? HotBoroughId,
    DateTimeOffset HotRotatesAt,
    IReadOnlyList<TerritoryDto> Territories,
    IReadOnlyList<HandCardDto> YourHand,
    IReadOnlyList<PowerCardDto> YourPowers,
    IReadOnlyList<PowerOfferDto> YourPowerOffers,
    int MaxPowerCards,
    int PowerDeckCount,
    int PowerDiscardCount,
    IReadOnlyList<TeamScoreDto> Scores,
    DateTimeOffset? EndsAt);

public record PlayCardRequest(
    [Required] string CardId,
    [Required] string BoroughId,
    bool Succeeded);

/// <summary>The ids of the offered cards to keep. Empty turns the whole offer down.</summary>
public record KeepPowersRequest([Required] IReadOnlyList<string> CardIds);

/// <summary><paramref name="BoroughId"/> is the target, and is required for a Steal only.</summary>
public record PlayPowerRequest(string? BoroughId);

public record ResolveStealRequest(bool Succeeded);
