using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Games;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GamesController(GameStore store) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<GameDto>> Create(
        [FromBody] GameMembershipRequest? request, CancellationToken cancellationToken)
    {
        var game = await store.CreateAsync(Sub, CallerIdentity.DisplayNameOrDefault(request?.DisplayName), cancellationToken);
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
            GameCodeGenerator.Normalize(code), Sub, CallerIdentity.DisplayNameOrDefault(request?.DisplayName), cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    [HttpPost("{code}/duration")]
    public async Task<ActionResult<GameDto>> SetDuration(
        string code, [FromBody] SetDurationRequest request, CancellationToken cancellationToken)
    {
        var result = await store.SetDurationAsync(
            GameCodeGenerator.Normalize(code), Sub, request.DurationMinutes, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    [HttpPost("{code}/teams")]
    public async Task<ActionResult<GameDto>> CreateTeam(
        string code, [FromBody] CreateTeamRequest request, CancellationToken cancellationToken)
    {
        var result = await store.CreateTeamAsync(
            GameCodeGenerator.Normalize(code), Sub, request.Name.Trim(), cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>Also how a player switches teams — their team is set to whichever they pick.</summary>
    [HttpPost("{code}/teams/{teamId}/join")]
    public async Task<ActionResult<GameDto>> JoinTeam(
        string code, string teamId, CancellationToken cancellationToken)
    {
        var result = await store.JoinTeamAsync(
            GameCodeGenerator.Normalize(code), Sub, teamId, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    [HttpPost("{code}/teams/leave")]
    public async Task<ActionResult<GameDto>> LeaveTeam(string code, CancellationToken cancellationToken)
    {
        var result = await store.LeaveTeamAsync(
            GameCodeGenerator.Normalize(code), Sub, cancellationToken);

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

    private string Sub => User.GetSubject();

    private GameDto ToDto(Game game) => new(
        game.Code,
        game.Status.ToString(),
        game.IsHost(Sub),
        game.FindPlayer(Sub)?.TeamId,
        game.DurationMinutes,
        game.Players
            .Select(player => new GamePlayerDto(
                player.Name,
                game.IsHost(player.Sub),
                player.Sub == Sub,
                player.TeamId))
            .ToList(),
        game.Teams.Select(team => new GameTeamDto(team.Id, team.Name)).ToList());

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
            title: "Only the host can change this",
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

        GameMutationStatus.TeamNotFound => Problem(
            title: "That team no longer exists",
            statusCode: StatusCodes.Status404NotFound),

        GameMutationStatus.DuplicateTeamName => Problem(
            title: "A team with that name already exists",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.TooManyTeams => Problem(
            title: "This game already has the maximum number of teams",
            statusCode: StatusCodes.Status409Conflict),

        // Every retry lost the version check, which means the lobby is unusually busy rather
        // than broken — the caller can simply try again.
        GameMutationStatus.Conflict => Problem(
            title: "The game was being changed by someone else. Please try again.",
            statusCode: StatusCodes.Status409Conflict),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled mutation status."),
    };
}
