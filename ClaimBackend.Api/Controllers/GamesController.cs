using System.Security.Claims;
using ClaimBackend.Api.Games;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GamesController(GameStore store) : ControllerBase
{
    private const int MaxDisplayNameLength = 40;

    [HttpPost]
    public async Task<ActionResult<GameDto>> Create(
        [FromBody] GameMembershipRequest? request, CancellationToken cancellationToken)
    {
        var game = await store.CreateAsync(Sub, DisplayName(request), cancellationToken);
        return CreatedAtAction(nameof(Get), new { code = game.Code }, ToDto(game));
    }

    [HttpGet("{code}")]
    public async Task<ActionResult<GameDto>> Get(string code, CancellationToken cancellationToken)
    {
        var game = await store.GetAsync(GameCodeGenerator.Normalize(code), cancellationToken);
        return game is null ? GameNotFound(code) : ToDto(game);
    }

    [HttpPost("{code}/join")]
    public async Task<ActionResult<GameDto>> Join(
        string code, [FromBody] GameMembershipRequest? request, CancellationToken cancellationToken)
    {
        var result = await store.JoinAsync(
            GameCodeGenerator.Normalize(code), Sub, DisplayName(request), cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>
    /// Returns no content rather than the game: the caller is no longer a member, and if they
    /// were the last one out the game no longer exists at all.
    /// </summary>
    [HttpPost("{code}/leave")]
    public async Task<IActionResult> Leave(string code, CancellationToken cancellationToken)
    {
        var result = await store.LeaveAsync(GameCodeGenerator.Normalize(code), Sub, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? NoContent()
            : Failure(result.Status, code);
    }

    [HttpPost("{code}/start")]
    public async Task<ActionResult<GameDto>> Start(string code, CancellationToken cancellationToken)
    {
        var result = await store.StartAsync(GameCodeGenerator.Normalize(code), Sub, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>
    /// The Cognito subject of the caller. JwtBearer may or may not have renamed `sub` to the
    /// ClaimTypes equivalent depending on inbound claim mapping, so both spellings are checked.
    /// </summary>
    private string Sub =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated caller has no subject claim.");

    private static string DisplayName(GameMembershipRequest? request)
    {
        var name = request?.DisplayName?.Trim();

        return string.IsNullOrEmpty(name)
            ? "Player"
            : name[..Math.Min(name.Length, MaxDisplayNameLength)];
    }

    private GameDto ToDto(Game game) => new(
        game.Code,
        game.Status.ToString(),
        game.IsHost(Sub),
        game.Players
            .Select(player => new GamePlayerDto(
                player.Name,
                game.IsHost(player.Sub),
                player.Sub == Sub))
            .ToList());

    private ActionResult GameNotFound(string code) =>
        NotFound(new ProblemDetails
        {
            Title = "Game not found",
            Detail = $"No game is running with the code '{GameCodeGenerator.Normalize(code)}'.",
            Status = StatusCodes.Status404NotFound,
        });

    private ActionResult Failure(GameMutationStatus status, string code) => status switch
    {
        GameMutationStatus.NotFound => GameNotFound(code),

        GameMutationStatus.NotHost => Problem(
            title: "Only the host can start the game",
            statusCode: StatusCodes.Status403Forbidden),

        GameMutationStatus.NotInGame => Problem(
            title: "You are not in this game",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.AlreadyStarted => Problem(
            title: "This game has already started",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.GameFull => Problem(
            title: "This game is full",
            statusCode: StatusCodes.Status409Conflict),

        // Every retry lost the version check, which means the lobby is unusually busy rather
        // than broken — the caller can simply try again.
        GameMutationStatus.Conflict => Problem(
            title: "The game was being changed by someone else. Please try again.",
            statusCode: StatusCodes.Status409Conflict),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled mutation status."),
    };
}
