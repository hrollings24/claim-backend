namespace ClaimBackend.Api.Games;

/// <summary>
/// A player as the lobby shows them. Cognito subs stay server-side — the client only needs to
/// know who to label as host and which row is the caller.
/// </summary>
public record GamePlayerDto(string Name, bool IsHost, bool IsYou);

public record GameDto(
    string Code,
    string Status,
    bool YouAreHost,
    IReadOnlyList<GamePlayerDto> Players);

/// <summary>
/// The display name comes from the caller because it lives on the Cognito *id* token, while the
/// API is called with the *access* token, which carries no name claim. That makes it
/// self-asserted: good enough for a lobby, but it can't be treated as verified identity. To
/// harden it, the API would have to call Cognito's GetUser with the caller's access token.
/// </summary>
public record GameMembershipRequest(string? DisplayName);
