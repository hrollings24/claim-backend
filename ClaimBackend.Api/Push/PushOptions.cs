namespace ClaimBackend.Api.Push;

public class PushOptions
{
    public const string SectionName = "Push";

    public required string TableName { get; set; }

    /// <summary>
    /// The VAPID public key, handed to browsers so they can create a subscription bound to this
    /// server. Public by design — it ships in the app.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The VAPID private key. A secret: it is what proves a push came from us, so it is supplied
    /// by environment variable and never committed. Push is skipped entirely when it is unset,
    /// which is what keeps local development from needing it.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Contact for the push service to reach if this sender misbehaves. VAPID requires a mailto:
    /// or https: URL.
    /// </summary>
    public string Subject { get; set; } = "mailto:noreply@londonboroughconquest.app";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
