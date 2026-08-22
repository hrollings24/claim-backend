using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Challenges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChallengesController(ChallengeStore store) : ControllerBase
{
    /// <summary>
    /// Newest first. The list pages rather than returning everything, so it stays a bounded
    /// response however many challenges accumulate; pass the previous response's cursor to
    /// continue.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ChallengePageDto>> List(
        [FromQuery] int? pageSize, [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        ChallengePage page;
        try
        {
            page = await store.ListAsync(pageSize, cursor, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Problem(
                title: "The paging cursor is not valid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return new ChallengePageDto(page.Challenges.Select(ToDto).ToList(), page.NextCursor);
    }

    [HttpPost]
    public async Task<ActionResult<ChallengeDto>> Create(
        [FromBody] CreateChallengeRequest request, CancellationToken cancellationToken)
    {
        var challenge = await store.CreateAsync(
            new Challenge
            {
                Id = Guid.NewGuid().ToString("n"),
                Type = request.Type,
                Title = request.Title.Trim(),
                Summary = request.Summary.Trim(),
                FurtherDetails = request.FurtherDetails.Trim(),
                CreatedBySub = User.GetSubject(),
                CreatedByName = CallerIdentity.DisplayNameOrDefault(request.DisplayName),
                CreatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);

        return CreatedAtAction(nameof(List), ToDto(challenge));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ChallengeDto>> Get(string id, CancellationToken cancellationToken)
    {
        var challenge = await store.FindAsync(id, cancellationToken);

        return challenge is null ? ChallengeNotFound() : ToDto(challenge);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ChallengeDto>> Update(
        string id, [FromBody] UpdateChallengeRequest request, CancellationToken cancellationToken)
    {
        var status = await store.UpdateAsync(
            id,
            User.GetSubject(),
            request.Type,
            request.Title.Trim(),
            request.Summary.Trim(),
            request.FurtherDetails.Trim(),
            cancellationToken);

        if (status is not ChallengeMutationStatus.Success)
        {
            return Failure(status);
        }

        var updated = await store.FindAsync(id, cancellationToken);

        return updated is null ? ChallengeNotFound() : ToDto(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var status = await store.DeleteAsync(id, User.GetSubject(), cancellationToken);

        return status is ChallengeMutationStatus.Success ? NoContent() : Failure(status);
    }

    private ActionResult ChallengeNotFound() =>
        Problem(title: "That challenge no longer exists", statusCode: StatusCodes.Status404NotFound);

    private ActionResult Failure(ChallengeMutationStatus status) => status switch
    {
        ChallengeMutationStatus.NotFound => ChallengeNotFound(),

        // Anyone may read the deck; only the author may change what their card says.
        ChallengeMutationStatus.NotYours => Problem(
            title: "Only the person who wrote a challenge can change or remove it",
            statusCode: StatusCodes.Status403Forbidden),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status."),
    };

    private ChallengeDto ToDto(Challenge challenge) => new(
        challenge.Id,
        challenge.Type.ToString(),
        challenge.Title,
        challenge.Summary,
        challenge.FurtherDetails,
        challenge.CreatedByName,
        challenge.CreatedAt,
        challenge.CreatedBySub == User.GetSubject());
}
