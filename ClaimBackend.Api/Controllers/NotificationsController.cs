using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

/// <summary>
/// The caller's own inbox: every notification the game has sent them in the last week, from any
/// game, whether or not it reached a device.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController(NotificationStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InboxNotificationDto>>> List(CancellationToken cancellationToken)
    {
        var notifications = await store.ListAsync(User.GetSubject(), cancellationToken);

        return notifications
            .Select(n => new InboxNotificationDto(n.Id, n.GameCode, n.Title, n.Body, n.Url, n.CreatedAt))
            .ToList();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Dismiss(string id, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(User.GetSubject(), id, cancellationToken);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> DismissAll(CancellationToken cancellationToken)
    {
        await store.DeleteAllAsync(User.GetSubject(), cancellationToken);
        return NoContent();
    }
}
