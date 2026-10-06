using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Challenges;
using ClaimBackend.Api.Games;
using ClaimBackend.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GamesController(
    GameStore store,
    ChallengeStore challenges,
    GameNotifier notifier) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<GameDto>> Create(
        [FromBody] GameMembershipRequest? request, CancellationToken cancellationToken)
    {
        var game = await store.CreateAsync(Sub, CallerIdentity.DisplayNameOrDefault(request?.DisplayName), cancellationToken);
        return CreatedAtAction(nameof(Get), new { code = game.Code }, ToDto(game));
    }

    /// <summary>Every game the caller still has a seat in, for the join page to offer a way back.</summary>
    [HttpGet("mine")]
    public async Task<ActionResult<IReadOnlyList<GameDto>>> Mine(CancellationToken cancellationToken)
    {
        var games = await store.ListForPlayerAsync(Sub, cancellationToken);
        return games.Select(ToDto).ToList();
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

    /// <summary>
    /// Plays a card at a borough and reports whether the challenge came off. Which of claim,
    /// steal or counter-attack that amounts to is the engine's decision, from the card's kind
    /// and who holds the borough.
    /// </summary>
    [HttpPost("{code}/play")]
    public async Task<ActionResult<GameDto>> Play(
        string code, [FromBody] PlayCardRequest request, CancellationToken cancellationToken)
    {
        var deck = await challenges.GetDeckAsync(cancellationToken);

        var (result, play) = await store.PlayAsync(
            GameCodeGenerator.Normalize(code),
            Sub,
            request.CardId,
            request.BoroughId,
            request.Succeeded,
            deck,
            cancellationToken);

        if (result.Status is not GameMutationStatus.Success)
        {
            return Failure(result.Status, code);
        }

        var game = result.Game!;
        await notifier.PlayedAsync(
            game, game.FindPlayer(Sub)?.TeamId ?? string.Empty, request.BoroughId, play, cancellationToken);

        return ToDto(game);
    }

    /// <summary>
    /// Commits a steal card to a target. From this moment the challenge is revealed to the
    /// team's own view of the game and the countdown is running — there is no way back short of
    /// resolving it.
    /// </summary>
    [HttpPost("{code}/activate-steal")]
    public async Task<ActionResult<GameDto>> ActivateSteal(
        string code, [FromBody] ActivateStealRequest request, CancellationToken cancellationToken)
    {
        var result = await store.ActivateStealAsync(
            GameCodeGenerator.Normalize(code), Sub, request.CardId, request.BoroughId, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>Reports whether an already-activated steal came off.</summary>
    [HttpPost("{code}/resolve-steal")]
    public async Task<ActionResult<GameDto>> ResolveSteal(
        string code, [FromBody] ResolveStealRequest request, CancellationToken cancellationToken)
    {
        var deck = await challenges.GetDeckAsync(cancellationToken);

        var (result, play) = await store.ResolveStealAsync(
            GameCodeGenerator.Normalize(code), Sub, request.CardId, request.Succeeded, deck, cancellationToken);

        if (result.Status is not GameMutationStatus.Success)
        {
            return Failure(result.Status, code);
        }

        var game = result.Game!;
        await notifier.PlayedAsync(
            game, game.FindPlayer(Sub)?.TeamId ?? string.Empty, play.BoroughId ?? string.Empty, play, cancellationToken);

        return ToDto(game);
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

    [HttpPost("{code}/hot-rotation")]
    public async Task<ActionResult<GameDto>> SetHotRotation(
        string code, [FromBody] SetHotRotationRequest request, CancellationToken cancellationToken)
    {
        var result = await store.SetHotRotationAsync(
            GameCodeGenerator.Normalize(code), Sub, request.HotRotationMinutes, cancellationToken);

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

    /// <summary>
    /// Deals the board and the hands. Cards come from the challenges everyone has written, so a
    /// game can't start until there are enough of them, with at least one of each kind.
    /// </summary>
    [HttpPost("{code}/start")]
    public async Task<ActionResult<GameDto>> Start(string code, CancellationToken cancellationToken)
    {
        var deck = await challenges.GetDeckAsync(cancellationToken);
        var result = await store.StartAsync(GameCodeGenerator.Normalize(code), Sub, deck, cancellationToken);

        if (result.Status is GameMutationStatus.Success)
        {
            await notifier.StartedAsync(result.Game!, cancellationToken);
        }

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
        game.HotRotationMinutes,
        BoardToDto(game),
        game.Players
            .Select(player => new GamePlayerDto(
                player.Name,
                game.IsHost(player.Sub),
                player.Sub == Sub,
                player.TeamId))
            .ToList(),
        game.Teams.Select(team => new GameTeamDto(team.Id, team.Name)).ToList());

    /// <summary>Null in the lobby; the board only exists once the game has been dealt.</summary>
    private GameBoardDto? BoardToDto(Game game)
    {
        if (game.Board is not { } board)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var yourTeamId = game.FindPlayer(Sub)?.TeamId;

        var territories = game.Teams
            .SelectMany(team => team.Territories.Select(territory => new TerritoryDto(
                territory.BoroughId,
                BoroughCatalogue.NameOf(territory.BoroughId),
                team.Id,
                team.Name,
                territory.IsLocked)))
            .OrderBy(t => t.Name)
            .ToList();

        var counter = game.CounterWindows.FirstOrDefault(w => w.TeamId == yourTeamId);

        return new GameBoardDto(
            board.Active
                .Select(a => new ActiveBoroughDto(
                    a.BoroughId,
                    BoroughCatalogue.NameOf(a.BoroughId),
                    BoroughCatalogue.Find(a.BoroughId)?.Zone.ToString() ?? "Outer",
                    a.BoroughId == board.HotBoroughId))
                .ToList(),
            board.HotBoroughId,
            board.HotRotatesAt,
            territories,
            yourTeamId is null
                ? []
                : game.FindHand(yourTeamId)?.Cards.Select(ToHandCardDto).ToList() ?? [],
            game.Teams
                .Select(team => new TeamScoreDto(
                    team.Id, team.Name, team.Territories.Count, team.BonusPoints, game.ScoreFor(team)))
                .OrderByDescending(t => t.Score)
                .ToList(),
            counter is null
                ? null
                : new CounterWindowDto(
                    counter.AgainstTeamId,
                    game.Teams.FirstOrDefault(t => t.Id == counter.AgainstTeamId)?.Name ?? "another team",
                    counter.ExpiresAt),
            game.EndsAt);
    }

    /// <summary>
    /// An unactivated steal is hidden — the real challenge never reaches the client — until the
    /// team has committed to a target. A claim, or an activated steal, is always shown in full.
    /// </summary>
    private static HandCardDto ToHandCardDto(HandCard card)
    {
        var hidden = card.Type is ChallengeType.Steal && card.ActivatedAt is null;

        return new HandCardDto(
            card.Id,
            card.Type.ToString(),
            hidden ? "Steal challenge" : card.Title,
            hidden ? "Pick a target to reveal it and start the clock." : card.Summary,
            hidden ? string.Empty : card.FurtherDetails,
            card.StealMinutes,
            card.ActivatedAt is { } activatedAt && card.StealMinutes is { } minutes
                ? activatedAt.AddMinutes(minutes)
                : null);
    }

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

        GameMutationStatus.NotEnoughTeams => Problem(
            title: "A game needs at least two teams",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotEnoughChallenges => Problem(
            title: "Not enough challenges to deal from — you need at least five, including at least three claim and one steal",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotOnATeam => Problem(
            title: "Join a team before playing",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.CardNotInHand => Problem(
            title: "That card isn't in your team's hand",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.InvalidTarget => Problem(
            title: "That card can't be played on that borough",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.BoroughLocked => Problem(
            title: "That borough is locked and can't be contested yet",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.GameNotRunning => Problem(
            title: "This game isn't running",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NoCounterWindow => Problem(
            title: "You have no counter-attack open against that team",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.TooManyTeams => Problem(
            title: "This game already has the maximum number of teams",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.AlreadyActivated => Problem(
            title: "That steal has already been activated",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotActivated => Problem(
            title: "Pick a target to activate that steal before entering the outcome",
            statusCode: StatusCodes.Status409Conflict),

        // Every retry lost the version check, which means the lobby is unusually busy rather
        // than broken — the caller can simply try again.
        GameMutationStatus.Conflict => Problem(
            title: "The game was being changed by someone else. Please try again.",
            statusCode: StatusCodes.Status409Conflict),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled mutation status."),
    };
}
