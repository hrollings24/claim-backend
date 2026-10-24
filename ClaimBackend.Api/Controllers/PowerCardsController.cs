using ClaimBackend.Api.Powers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

/// <summary>
/// The power cards themselves. Everyone sees all of them and anyone may add, edit or delete one,
/// except Steal, which is built in. How many of each go into a game is a configuration's job.
/// </summary>
[ApiController]
[Route("api/power-cards")]
[Authorize]
public class PowerCardsController(PowerCardStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PowerCardDefinitionDto>>> List(CancellationToken cancellationToken)
    {
        var cards = await store.ListAsync(cancellationToken);
        return cards.Select(ToDto).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PowerCardDefinitionDto>> Get(string id, CancellationToken cancellationToken)
    {
        var card = await store.FindAsync(id, cancellationToken);
        return card is null ? NotFoundProblem() : ToDto(card);
    }

    [HttpPost]
    public async Task<ActionResult<PowerCardDefinitionDto>> Create(
        [FromBody] SavePowerCardRequest request, CancellationToken cancellationToken)
    {
        var (status, card) = await store.CreateAsync(
            request.Kind, request.Title.Trim(), request.Description.Trim(), cancellationToken);

        return status is PowerCardMutationStatus.Success
            ? CreatedAtAction(nameof(Get), new { id = card!.Id }, ToDto(card))
            : Failure(status);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PowerCardDefinitionDto>> Update(
        string id, [FromBody] SavePowerCardRequest request, CancellationToken cancellationToken)
    {
        var (status, card) = await store.UpdateAsync(
            id, request.Kind, request.Title.Trim(), request.Description.Trim(), cancellationToken);

        return status is PowerCardMutationStatus.Success ? ToDto(card!) : Failure(status);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var status = await store.DeleteAsync(id, cancellationToken);
        return status is PowerCardMutationStatus.Success ? NoContent() : Failure(status);
    }

    private ActionResult NotFoundProblem() =>
        Problem(title: "That power card no longer exists", statusCode: StatusCodes.Status404NotFound);

    private ActionResult Failure(PowerCardMutationStatus status) => status switch
    {
        PowerCardMutationStatus.NotFound => NotFoundProblem(),

        PowerCardMutationStatus.BuiltIn => Problem(
            title: "Steal is built in and can't be changed",
            statusCode: StatusCodes.Status403Forbidden),

        PowerCardMutationStatus.InvalidKind => Problem(
            title: "A power card you write has to be a Curse or a Boost",
            statusCode: StatusCodes.Status400BadRequest),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status."),
    };

    private static PowerCardDefinitionDto ToDto(PowerDefinition card) => new(
        card.Id,
        card.Kind.ToString(),
        card.Title,
        card.Description,
        PowerCatalogue.BuiltIn.Any(b => b.Id == card.Id));
}
