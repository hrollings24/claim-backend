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

public record ActiveBoroughDto(string Id, string Name, string Zone, bool IsHot);

/// <summary>A locked borough is settled for the rest of the game, so there is no time to show.</summary>
public record TerritoryDto(
    string Id, string Name, string TeamId, string TeamName, bool IsLocked);

public record HandCardDto(
    string Id, string Type, string Title, string Summary, string FurtherDetails);

public record TeamScoreDto(string TeamId, string Name, int Territories, int BonusPoints, int Score);

/// <summary>Only ever the caller's own window — you can't see who else is about to hit back.</summary>
public record CounterWindowDto(string AgainstTeamId, string AgainstTeamName, DateTimeOffset ExpiresAt);

/// <summary>
/// Everything a player needs to decide their next move: what's on offer, who holds what, the
/// cards in their own hand, and the clock.
/// </summary>
public record GameBoardDto(
    IReadOnlyList<ActiveBoroughDto> Active,
    string? HotBoroughId,
    DateTimeOffset HotRotatesAt,
    IReadOnlyList<TerritoryDto> Territories,
    IReadOnlyList<HandCardDto> YourHand,
    IReadOnlyList<TeamScoreDto> Scores,
    CounterWindowDto? YourCounterWindow,
    DateTimeOffset? EndsAt);

public record PlayCardRequest(
    [Required] string CardId,
    [Required] string BoroughId,
    bool Succeeded);
