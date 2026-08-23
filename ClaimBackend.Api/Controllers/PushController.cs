using System.ComponentModel.DataAnnotations;
using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Controllers;

public record PushKeyDto(string PublicKey, bool Enabled);

public record SubscribeRequest(
    [Required] string Endpoint,
    [Required] string P256dh,
    [Required] string Auth);

public record UnsubscribeRequest([Required] string Endpoint);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PushController(
    PushSubscriptionStore store,
    IOptions<PushOptions> options) : ControllerBase
{
    /// <summary>
    /// The key a browser needs to create a subscription bound to this server. Also reports
    /// whether push is configured at all, so the app can hide the option rather than offering
    /// something that will silently never arrive.
    /// </summary>
    [HttpGet("key")]
    public ActionResult<PushKeyDto> Key() =>
        new PushKeyDto(options.Value.PublicKey, options.Value.IsConfigured);

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request, CancellationToken cancellationToken)
    {
        await store.SaveAsync(
            new PushSubscription
            {
                Sub = User.GetSubject(),
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);

        return NoContent();
    }

    [HttpDelete("subscriptions")]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] UnsubscribeRequest request, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(User.GetSubject(), request.Endpoint, cancellationToken);

        return NoContent();
    }
}
