using ClaimBackend.Api.Powers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

/// <summary>
/// Named power-deck recipes, kept outside any game. Everyone sees all of them and anyone may
/// edit any of them; a host picks one in the lobby.
/// </summary>
[ApiController]
[Route("api/power-configurations")]
[Authorize]
public class PowerConfigurationsController(PowerConfigurationStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PowerConfigurationDto>>> List(CancellationToken cancellationToken)
    {
        var configurations = await store.ListAsync(cancellationToken);

        return configurations
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    /// <summary>Every card a configuration can include, for building a new one.</summary>
    [HttpGet("cards")]
    public ActionResult<IReadOnlyList<PowerCountDto>> Cards() =>
        PowerCatalogue.All
            .Select(p => new PowerCountDto(p.Id, p.Kind.ToString(), p.Title, p.Description, 0))
            .ToList();

    [HttpGet("{id}")]
    public async Task<ActionResult<PowerConfigurationDto>> Get(string id, CancellationToken cancellationToken)
    {
        var configuration = await store.FindAsync(id, cancellationToken);

        return configuration is null ? NotFoundProblem() : ToDto(configuration);
    }

    [HttpPost]
    public async Task<ActionResult<PowerConfigurationDto>> Create(
        [FromBody] SavePowerConfigurationRequest request, CancellationToken cancellationToken)
    {
        var (status, configuration) = await store.CreateAsync(request.Name.Trim(), request.Counts, cancellationToken);

        return status is PowerConfigurationMutationStatus.Success
            ? CreatedAtAction(nameof(Get), new { id = configuration!.Id }, ToDto(configuration))
            : Failure(status);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PowerConfigurationDto>> Update(
        string id, [FromBody] SavePowerConfigurationRequest request, CancellationToken cancellationToken)
    {
        var (status, configuration) = await store.UpdateAsync(id, request.Name.Trim(), request.Counts, cancellationToken);

        return status is PowerConfigurationMutationStatus.Success ? ToDto(configuration!) : Failure(status);
    }

    private ActionResult NotFoundProblem() =>
        Problem(title: "That power card configuration no longer exists", statusCode: StatusCodes.Status404NotFound);

    private ActionResult Failure(PowerConfigurationMutationStatus status) => status switch
    {
        PowerConfigurationMutationStatus.NotFound => NotFoundProblem(),

        PowerConfigurationMutationStatus.DuplicateName => Problem(
            title: "A configuration with that name already exists",
            statusCode: StatusCodes.Status409Conflict),

        PowerConfigurationMutationStatus.InvalidCounts => Problem(
            title: $"Each card needs 0–{PowerConfigurationStore.MaxCopies} copies, with at least one card in the deck",
            statusCode: StatusCodes.Status400BadRequest),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status."),
    };

    private static PowerConfigurationDto ToDto(PowerConfiguration configuration) => new(
        configuration.Id,
        configuration.Name,
        PowerCatalogue.All
            .Select(p => new PowerCountDto(p.Id, p.Kind.ToString(), p.Title, p.Description, configuration.CountOf(p.Id)))
            .ToList(),
        configuration.TotalCards);
}
