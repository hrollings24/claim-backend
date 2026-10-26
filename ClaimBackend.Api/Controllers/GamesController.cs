using ClaimBackend.Api.Auth;
using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Challenges;
using ClaimBackend.Api.Games;
using ClaimBackend.Api.Powers;
using ClaimBackend.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GamesController(
    GameStore store,
    ChallengeStore challenges,
    GameNotifier notifier,
    PowerConfigurationStore powerConfigurations,
    PowerCardStore powerCards,
    GameEngine engine,
    IOptions<GamesOptions> options) : ControllerBase
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
    /// Plays a completed claim challenge at an active borough: it claims the borough and draws
    /// power cards, which arrive as an offer to choose from.
    /// </summary>
    [HttpPost("{code}/play")]
    public async Task<ActionResult<GameDto>> Play(
        string code, [FromBody] PlayCardRequest request, CancellationToken cancellationToken)
    {
        // Older versions of the app still offer "Failed". Refuse it rather than quietly treating
        // it as a claim.
        if (request.Succeeded is false)
        {
            return Failure(GameMutationStatus.ClaimsCantFail, code);
        }

        var deck = await challenges.GetDeckAsync(cancellationToken);

        var (result, play) = await store.PlayAsync(
            GameCodeGenerator.Normalize(code),
            Sub,
            request.CardId,
            request.BoroughId,
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

    /// <summary>Keeps up to the offer's allowance of its cards; the rest are discarded.</summary>
    [HttpPost("{code}/powers/offers/{offerId}/keep")]
    public async Task<ActionResult<GameDto>> KeepPowers(
        string code, string offerId, [FromBody] KeepPowersRequest request, CancellationToken cancellationToken)
    {
        var result = await store.KeepPowersAsync(
            GameCodeGenerator.Normalize(code), Sub, offerId, request.CardIds.ToList(), cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>
    /// Plays a power card, which then stays in the team's list marked as played. A Steal needs a
    /// target: the challenge is revealed and its countdown starts from this moment, with no way
    /// back short of resolving it.
    /// </summary>
    [HttpPost("{code}/powers/{cardId}/play")]
    public async Task<ActionResult<GameDto>> PlayPower(
        string code, string cardId, [FromBody] PlayPowerRequest? request, CancellationToken cancellationToken)
    {
        var deck = await challenges.GetDeckAsync(cancellationToken);

        var (result, play) = await store.PlayPowerAsync(
            GameCodeGenerator.Normalize(code), Sub, cardId, request?.BoroughId, request?.TargetTeamId, deck, cancellationToken);

        return await Played(result, play, code, cancellationToken);
    }

    /// <summary>
    /// Uses a Curse Reverse on a curse aimed at the caller's team. Never done for them — a curse
    /// stays on until they choose to answer it, or it runs out.
    /// </summary>
    [HttpPost("{code}/curses/{curseId}/respond")]
    public async Task<ActionResult<GameDto>> RespondToCurse(
        string code, string curseId, [FromBody] RespondRequest request, CancellationToken cancellationToken)
    {
        var (result, play) = await store.RespondToCurseAsync(
            GameCodeGenerator.Normalize(code), Sub, curseId, request.CardId, cancellationToken);

        return await Played(result, play, code, cancellationToken);
    }

    /// <summary>Marks a curse with no time on it as done.</summary>
    [HttpPost("{code}/curses/{curseId}/clear")]
    public async Task<ActionResult<GameDto>> ClearCurse(string code, string curseId, CancellationToken cancellationToken)
    {
        var result = await store.ClearCurseAsync(GameCodeGenerator.Normalize(code), Sub, curseId, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>Finishes a played Swap: one of their cards taken, one of yours given.</summary>
    [HttpPost("{code}/powers/{cardId}/swap")]
    public async Task<ActionResult<GameDto>> CompleteSwap(
        string code, string cardId, [FromBody] CompleteSwapRequest request, CancellationToken cancellationToken)
    {
        var (result, play) = await store.CompleteSwapAsync(
            GameCodeGenerator.Normalize(code), Sub, cardId, request.TakeCardId, request.GiveCardId, cancellationToken);

        return await Played(result, play, code, cancellationToken);
    }

    [HttpPost("{code}/board-size")]
    public async Task<ActionResult<GameDto>> SetBoardSize(
        string code, [FromBody] SetBoardSizeRequest request, CancellationToken cancellationToken)
    {
        var result = await store.SetBoardSizeAsync(
            GameCodeGenerator.Normalize(code), Sub, request.BoardSize, cancellationToken);

        return result.Status is GameMutationStatus.Success
            ? ToDto(result.Game!)
            : Failure(result.Status, code);
    }

    /// <summary>Tells whoever a move affected, then returns the game — or the reason it was refused.</summary>
    private async Task<ActionResult<GameDto>> Played(
        GameMutationResult result, PlayResult play, string code, CancellationToken cancellationToken)
    {
        if (result.Status is not GameMutationStatus.Success)
        {
            return Failure(result.Status, code);
        }

        var game = result.Game!;
        await notifier.PlayedAsync(
            game, game.FindPlayer(Sub)?.TeamId ?? string.Empty, play.BoroughId ?? string.Empty, play, cancellationToken);

        return ToDto(game);
    }

    /// <summary>Reports whether a running steal came off. Must arrive before its clock runs out.</summary>
    [HttpPost("{code}/powers/{cardId}/resolve")]
    public async Task<ActionResult<GameDto>> ResolveSteal(
        string code, string cardId, [FromBody] ResolveStealRequest request, CancellationToken cancellationToken)
    {
        var (result, play) = await store.ResolveStealAsync(
            GameCodeGenerator.Normalize(code), Sub, cardId, request.Succeeded, cancellationToken);

        if (result.Status is not GameMutationStatus.Success)
        {
            return Failure(result.Status, code);
        }

        var game = result.Game!;
        await notifier.PlayedAsync(
            game, game.FindPlayer(Sub)?.TeamId ?? string.Empty, play.BoroughId ?? string.Empty, play, cancellationToken);

        return ToDto(game);
    }

    /// <summary>Throws away an unplayed power card, to make room under the limit.</summary>
    [HttpPost("{code}/powers/{cardId}/discard")]
    public async Task<ActionResult<GameDto>> DiscardPower(
        string code, string cardId, CancellationToken cancellationToken)
    {
        var result = await store.DiscardPowerAsync(GameCodeGenerator.Normalize(code), Sub, cardId, cancellationToken);

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

    [HttpPost("{code}/power-configuration")]
    public async Task<ActionResult<GameDto>> SetPowerConfiguration(
        string code, [FromBody] SetPowerConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = await powerConfigurations.FindAsync(request.ConfigurationId, cancellationToken);
        if (configuration is null)
        {
            return Failure(GameMutationStatus.PowerConfigurationNotFound, code);
        }

        var result = await store.SetPowerConfigurationAsync(
            GameCodeGenerator.Normalize(code), Sub, configuration, cancellationToken);

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

    [HttpPost("{code}/teams/{teamId}/color")]
    public async Task<ActionResult<GameDto>> SetTeamColor(
        string code, string teamId, [FromBody] SetTeamColorRequest request, CancellationToken cancellationToken)
    {
        var result = await store.SetTeamColorAsync(
            GameCodeGenerator.Normalize(code), Sub, teamId, request.Color, cancellationToken);

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
        var normalized = GameCodeGenerator.Normalize(code);
        var deck = await challenges.GetDeckAsync(cancellationToken);

        // Looked up outside the version check, which can't await; the store checks it is still
        // the game's choice before dealing with it.
        var lobby = await store.GetAsync(normalized, cancellationToken);
        var powers = lobby?.PowerConfigurationId is { } configurationId
            ? await powerConfigurations.FindAsync(configurationId, cancellationToken)
            : null;

        var cards = await powerCards.ListAsync(cancellationToken);

        var result = await store.StartAsync(normalized, Sub, deck, powers, cards, cancellationToken);

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
        game.BoardSize,
        engine.DefaultBoardSize(game.Teams.Count),
        game.PowerConfigurationId,
        game.PowerConfigurationName,
        BoardToDto(game),
        game.Players
            .Select(player => new GamePlayerDto(
                player.Name,
                game.IsHost(player.Sub),
                player.Sub == Sub,
                player.TeamId))
            .ToList(),
        game.Teams.Select(team => new GameTeamDto(team.Id, team.Name, team.Color)).ToList());

    /// <summary>Null in the lobby; the board only exists once the game has been dealt.</summary>
    private GameBoardDto? BoardToDto(Game game)
    {
        if (game.Board is not { } board)
        {
            return null;
        }

        var yourTeamId = game.FindPlayer(Sub)?.TeamId;
        var yourPowers = yourTeamId is null ? null : game.FindPowers(yourTeamId);

        var territories = game.Teams
            .SelectMany(team => team.Territories.Select(territory => new TerritoryDto(
                territory.BoroughId,
                BoroughCatalogue.NameOf(territory.BoroughId),
                team.Id,
                team.Name,
                territory.Fortified)))
            .OrderBy(t => t.Name)
            .ToList();

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
            // Unplayed first: those are what the team can still use. Played ones keep the order
            // they were kept in.
            yourPowers?.Cards.OrderBy(c => c.IsPlayed).Select(ToPowerCardDto).ToList() ?? [],
            yourPowers?.Offers
                .Select(offer => new PowerOfferDto(
                    offer.Id, offer.Reason, offer.Keep, offer.Cards.Select(ToPowerCardDto).ToList()))
                .ToList() ?? [],
            game.Curses
                .Where(c => c.Status is CurseStatus.Active
                    && yourTeamId is not null
                    && (c.TargetTeamId == yourTeamId || c.FromTeamId == yourTeamId))
                .Select(c => new CurseDto(
                    c.Id,
                    c.Title,
                    c.Description,
                    TeamName(game, c.FromTeamId),
                    TeamName(game, c.TargetTeamId),
                    c.TargetTeamId == yourTeamId,
                    c.StartedAt,
                    c.EndsAt,
                    c.IsReversal))
                .ToList(),
            yourTeamId is null
                ? []
                : game.Powers
                    .Where(p => p.TeamId != yourTeamId)
                    .SelectMany(p => p.Cards
                        .Where(c => c.Steal is { Outcome: null } steal
                            && game.FindTeamHolding(steal.BoroughId)?.Id == yourTeamId)
                        .Select(c => new IncomingStealDto(
                            c.Id,
                            c.Steal!.BoroughId,
                            BoroughCatalogue.NameOf(c.Steal.BoroughId),
                            TeamName(game, p.TeamId),
                            c.Steal.Title,
                            c.Steal.Summary,
                            c.Steal.ExpiresAt)))
                    .ToList(),
            yourPowers?.Cards
                .Where(c => c.Swap is { CompletedAt: null })
                .Select(c => new SwapDto(
                    c.Id,
                    c.Swap!.TargetTeamId,
                    TeamName(game, c.Swap.TargetTeamId),
                    game.FindPowers(c.Swap.TargetTeamId)?.Cards
                        .Where(theirs => !theirs.IsPlayed)
                        .Select(ToPowerCardDto)
                        .ToList() ?? []))
                .ToList() ?? [],
            options.Value.MaxPowerCards,
            game.PowerDeck?.DrawPile.Count ?? 0,
            game.PowerDeck?.DiscardPile.Count ?? 0,
            game.Teams
                .Select(team => new TeamScoreDto(
                    team.Id, team.Name, team.Territories.Count, team.BonusPoints, game.ScoreFor(team)))
                .OrderByDescending(t => t.Score)
                .ToList(),
            game.EndsAt);
    }

    private static HandCardDto ToHandCardDto(HandCard card) =>
        new(card.Id, card.Title, card.Summary, card.FurtherDetails, card.PowerDraws);

    private static PowerCardDto ToPowerCardDto(PowerCard card) => new(
        card.Id,
        card.Kind.ToString(),
        card.Title,
        card.Description,
        card.DurationMinutes,
        card.PlayedAt,
        card.PlayedOn,
        card.Steal is { } steal
            ? new StealDto(
                steal.BoroughId,
                BoroughCatalogue.NameOf(steal.BoroughId),
                steal.Title,
                steal.Summary,
                steal.FurtherDetails,
                steal.ExpiresAt,
                steal.Outcome?.ToString())
            : null);

    private static string TeamName(Game game, string teamId) =>
        game.FindTeam(teamId)?.Name ?? "another team";

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
            title: "Not enough challenges to deal from — you need at least five claim challenges, and a steal challenge if the power deck has Steals",
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

        GameMutationStatus.GameNotRunning => Problem(
            title: "This game isn't running",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.ClaimsCantFail => Problem(
            title: "Claim challenges can't be failed — only play one once you've done it",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotYourTeam => Problem(
            title: "You can only change your own team's colour",
            statusCode: StatusCodes.Status403Forbidden),

        GameMutationStatus.InvalidColor => Problem(
            title: "That isn't one of the team colours",
            statusCode: StatusCodes.Status400BadRequest),

        GameMutationStatus.ColorTaken => Problem(
            title: "Another team already has that colour",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.TooManyTeams => Problem(
            title: "This game already has the maximum number of teams",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.AlreadyPlayed => Problem(
            title: "That power card has already been played",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotPlayed => Problem(
            title: "Play that steal at a borough before entering the outcome",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.StealSettled => Problem(
            title: "That steal is already over — its result is in, or its time ran out",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.OfferNotFound => Problem(
            title: "Those power cards have already been chosen from",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotInOffer => Problem(
            title: "That card wasn't one of the ones drawn",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.TooManyKept => Problem(
            title: "You can't keep that many of these power cards",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NoPowerConfiguration => Problem(
            title: "Pick a power card configuration before starting",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.PowerConfigurationNotFound => Problem(
            title: "That power card configuration no longer exists",
            statusCode: StatusCodes.Status404NotFound),

        GameMutationStatus.EmptyPowerDeck => Problem(
            title: "That power card configuration has no cards in it",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.RespondOnly => Problem(
            title: "Use that card on a curse aimed at you",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.CurseNotFound => Problem(
            title: "That curse isn't on you any more",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.CannotReverse => Problem(
            title: "That curse has already been reversed once — it can't go back again",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotAResponse => Problem(
            title: "That card can't be used on this",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.CurseIsTimed => Problem(
            title: "That curse lifts on its own when its time is up",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.BoroughLocked => Problem(
            title: "That borough is fortified — it can't be stolen",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.AlreadyFortified => Problem(
            title: "That borough is already fortified",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.UnderAttack => Problem(
            title: "A steal on that borough is already running — you can't fortify it now",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NothingToSwap => Problem(
            title: "That team has no power cards to swap",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.SwapNotFound => Problem(
            title: "That swap is already done",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.NotInTheirHand => Problem(
            title: "They don't have that card any more — pick another",
            statusCode: StatusCodes.Status409Conflict),

        GameMutationStatus.PowerHandFull => Problem(
            title: "Your team is holding as many power cards as it can — play or discard one first",
            statusCode: StatusCodes.Status409Conflict),

        // Every retry lost the version check, which means the lobby is unusually busy rather
        // than broken — the caller can simply try again.
        GameMutationStatus.Conflict => Problem(
            title: "The game was being changed by someone else. Please try again.",
            statusCode: StatusCodes.Status409Conflict),

        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled mutation status."),
    };
}
