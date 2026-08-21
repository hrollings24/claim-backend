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

    private static ChallengeDto ToDto(Challenge challenge) => new(
        challenge.Id,
        challenge.Type.ToString(),
        challenge.Title,
        challenge.Summary,
        challenge.FurtherDetails,
        challenge.CreatedByName,
        challenge.CreatedAt);
}
