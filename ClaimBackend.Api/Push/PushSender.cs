using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Options;
using AppSubscription = ClaimBackend.Api.Push.PushSubscription;

namespace ClaimBackend.Api.Push;

/// <summary>What the service worker is handed. Kept small — payloads are size limited.</summary>
public record PushNotification(string Title, string Body, string? Url = null, string? Tag = null);

public class PushSender(
    IHttpClientFactory httpClientFactory,
    PushSubscriptionStore store,
    IOptions<PushOptions> options,
    ILogger<PushSender> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PushOptions _options = options.Value;

    /// <summary>
    /// Notifies every device belonging to the given players. Never throws: a notification that
    /// fails to send must not fail the move that triggered it — the player made their claim
    /// either way.
    /// </summary>
    public async Task SendAsync(
        IEnumerable<string> subs, PushNotification notification, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            // No VAPID keys, so nothing can be signed. Normal in local development.
            return;
        }

        IReadOnlyList<AppSubscription> subscriptions;
        try
        {
            subscriptions = await store.ForPlayersAsync(subs, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read push subscriptions.");
            return;
        }

        if (subscriptions.Count == 0)
        {
            return;
        }

        var client = new PushServiceClient(httpClientFactory.CreateClient(nameof(PushSender)))
        {
            DefaultAuthentication = new VapidAuthentication(_options.PublicKey, _options.PrivateKey)
            {
                Subject = _options.Subject,
            },
        };

        var message = new PushMessage(JsonSerializer.Serialize(notification, JsonOptions));

        await Task.WhenAll(subscriptions.Select(s => DeliverAsync(client, s, message, cancellationToken)));
    }

    private async Task DeliverAsync(
        PushServiceClient client,
        AppSubscription subscription,
        PushMessage message,
        CancellationToken cancellationToken)
    {
        var target = new Lib.Net.Http.WebPush.PushSubscription
        {
            Endpoint = subscription.Endpoint,
            Keys =
            {
                ["p256dh"] = subscription.P256dh,
                ["auth"] = subscription.Auth,
            },
        };

        try
        {
            await client.RequestPushMessageDeliveryAsync(target, message, cancellationToken);
        }
        catch (PushServiceClientException exception)
            when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // The browser threw the subscription away — cleared data, uninstalled, permission
            // revoked. It will never work again, so stop carrying it.
            logger.LogInformation("Removing a push subscription the service says is gone.");
            await store.DeleteAsync(subscription.Sub, subscription.Endpoint, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not deliver a push notification.");
        }
    }
}
