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
    int? BoardSize,
    int DefaultBoardSize,
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

/// <summary>Null leaves it to the game: a few more boroughs than there are teams.</summary>
public record SetBoardSizeRequest([Range(4, 10)] int? BoardSize);

/// <summary>How long the hot borough stands before moving on its own.</summary>
public record SetHotRotationRequest(
    [Range(1, 24 * 60)] int HotRotationMinutes);

public record ActiveBoroughDto(string Id, string Name, string Zone, bool IsHot);

/// <param name="IsFortified">Locked by Fortify: it can't be stolen for the rest of the game.</param>
public record TerritoryDto(string Id, string Name, string TeamId, string TeamName, bool IsFortified);

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
/// <param name="DurationMinutes">Curse only; null lasts until the cursed team clears it.</param>
/// <param name="PlayedOn">What it was played on — a team, a borough, a curse.</param>
public record PowerCardDto(
    string Id,
    string Kind,
    string Title,
    string Description,
    int? DurationMinutes,
    DateTimeOffset? PlayedAt,
    string? PlayedOn,
    StealDto? Steal);

/// <summary>
/// An active curse the caller's team cast or is suffering. <paramref name="OnYou"/> says which.
/// <paramref name="EndsAt"/> is null for one that lasts until cleared.
/// </summary>
public record CurseDto(
    string Id,
    string Title,
    string Description,
    string FromTeamName,
    string TargetTeamName,
    bool OnYou,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt,
    bool IsReversal);

/// <summary>
/// A Swap the caller's team has played and not finished: the other team's unplayed power cards,
/// revealed so one can be picked.
/// </summary>
public record SwapDto(string CardId, string TeamId, string TeamName, IReadOnlyList<PowerCardDto> TheirCards);

/// <summary>A steal running against one of the caller's team's boroughs, so they know it's coming.</summary>
public record IncomingStealDto(
    string Id,
    string BoroughId,
    string BoroughName,
    string FromTeamName,
    string Title,
    string Summary,
    DateTimeOffset ExpiresAt);

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
    IReadOnlyList<CurseDto> ActiveCurses,
    IReadOnlyList<IncomingStealDto> IncomingSteals,
    IReadOnlyList<SwapDto> YourSwaps,
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

/// <summary>
/// A Steal needs <paramref name="BoroughId"/>, a curse <paramref name="TargetTeamId"/>; a boost
/// needs neither.
/// </summary>
public record PlayPowerRequest(string? BoroughId, string? TargetTeamId);

/// <summary>The Curse Reverse to use.</summary>
public record RespondRequest([Required] string CardId);

/// <summary>
/// One of their cards to take and one of yours to give. Either may be left out only when that
/// side has nothing left to swap.
/// </summary>
public record CompleteSwapRequest(string? TakeCardId, string? GiveCardId);

public record ResolveStealRequest(bool Succeeded);
