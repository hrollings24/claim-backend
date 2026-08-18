using System.Security.Claims;

namespace ClaimBackend.Api.Auth;

public static class CallerIdentity
{
    private const int MaxDisplayNameLength = 40;

    /// <summary>
    /// The Cognito subject of the caller. JwtBearer may or may not have renamed `sub` to the
    /// ClaimTypes equivalent depending on inbound claim mapping, so both spellings are checked.
    /// </summary>
    public static string GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("sub")
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated caller has no subject claim.");

    /// <summary>
    /// Display names arrive from the client, because the API is called with the Cognito access
    /// token and the name lives on the id token. Self-asserted, so it is trimmed and bounded
    /// rather than trusted.
    /// </summary>
    public static string DisplayNameOrDefault(string? displayName)
    {
        var name = displayName?.Trim();

        return string.IsNullOrEmpty(name)
            ? "Player"
            : name[..Math.Min(name.Length, MaxDisplayNameLength)];
    }
}
