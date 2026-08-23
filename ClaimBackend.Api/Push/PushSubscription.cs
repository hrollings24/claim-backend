namespace ClaimBackend.Api.Push;

/// <summary>
/// One browser's permission to be notified. A player can have several — a phone and a laptop
/// are separate subscriptions — so they are keyed by player and then by endpoint.
/// </summary>
public class PushSubscription
{
    public required string Sub { get; init; }

    /// <summary>The push service URL to POST to. Issued by the browser's vendor, not by us.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Client public key, used to encrypt the payload so only that browser can read it.</summary>
    public required string P256dh { get; init; }

    public required string Auth { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
