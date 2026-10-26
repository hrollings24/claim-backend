using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using ClaimBackend.Api.Challenges;
using ClaimBackend.Api.Powers;
using ClaimBackend.Api.Push;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Games;

public enum GameMutationStatus
{
    Success,
    NotFound,
    NotInGame,
    NotHost,
    AlreadyStarted,
    GameFull,
    NotEnoughTeams,
    NotEnoughChallenges,
    NotOnATeam,
    CardNotInHand,
    InvalidTarget,
    GameNotRunning,
    TeamNotFound,
    DuplicateTeamName,
    TooManyTeams,
    InvalidColor,
    ColorTaken,
    NotYourTeam,
    ClaimsCantFail,
    Conflict,
    AlreadyPlayed,
    NotPlayed,
    StealSettled,
    OfferNotFound,
    NotInOffer,
    TooManyKept,
    PowerHandFull,
    NoPowerConfiguration,
    PowerConfigurationNotFound,
    EmptyPowerDeck,
    RespondOnly,
    CurseNotFound,
    CannotReverse,
    NotAResponse,
    CurseIsTimed,
    BoroughLocked,
    AlreadyFortified,
    UnderAttack,
    SwapNotFound,
    NotInTheirHand,
}

public record GameMutationResult(GameMutationStatus Status, Game? Game);

/// <summary>
/// Stores each game as a single DynamoDB item keyed by its code. Writes are guarded by a
/// version attribute, so concurrent joins and leaves queue up behind each other instead of
/// silently dropping one another's changes to the roster.
/// </summary>
public class GameStore(
    IAmazonDynamoDB dynamo, IOptions<GamesOptions> options, GameEngine engine, GameNotifier notifier)
{
    /// <summary>Retries for a write losing the version check to another player's write.</summary>
    private const int MaxWriteAttempts = 5;

    /// <summary>Retries for a freshly generated code colliding with a live game.</summary>
    private const int MaxCodeAttempts = 5;

    private readonly GamesOptions _options = options.Value;

    /// <summary>
    /// Reads the game and brings its deadlines up to date. Advancing can change the game — a
    /// lock lapses, the hot borough moves, the clock runs out — so anything it changes is
    /// written back. A losing race here is harmless: whoever won already applied the same
    /// expiries, so the update is simply dropped.
    /// </summary>
    public async Task<Game?> GetAsync(string code, CancellationToken cancellationToken)
    {
        var game = await LoadAsync(code, cancellationToken);
        if (game is null)
        {
            return null;
        }

        var expectedVersion = game.Version;
        var hotBefore = HotOf(game);
        if (!engine.Advance(game, DateTimeOffset.UtcNow))
        {
            return game;
        }

        game.Version = expectedVersion + 1;
        try
        {
            await WriteAsync(game, expectedVersion, cancellationToken);
            await AnnounceHotAsync(hotBefore, game, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            // Another request advanced it first, to the same place — and announced it.
        }

        return game;
    }

    /// <summary>The hot borough of a game already under way, or null if it isn't.</summary>
    private static string? HotOf(Game game) =>
        game.Status is GameStatus.InProgress ? game.Board?.HotBoroughId : null;

    /// <summary>
    /// The hot borough moves on a timer, from whichever request happens to look at the game first
    /// — nearly always a phone refreshing the game screen — or the moment it is claimed. Announced
    /// here, only after the move has been saved, so it goes out once whichever way it happened:
    /// a request that lost the race to save it never announces anything. Nothing goes out for the
    /// first hot borough of a game, which "The game has started" already covers.
    /// </summary>
    private Task AnnounceHotAsync(string? hotBefore, Game game, CancellationToken cancellationToken) =>
        hotBefore is not null && HotOf(game) is { } hot && hot != hotBefore
            ? notifier.HotBoroughChangedAsync(game, hot, cancellationToken)
            : Task.CompletedTask;

    private async Task<Game?> LoadAsync(string code, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = _options.TableName,
                Key = KeyFor(code),
                // The lobby polls immediately after joining, and an eventually consistent read
                // can still be showing the roster from before the join landed.
                ConsistentRead = true,
            },
            cancellationToken);

        return response.Item is null || response.Item.Count == 0 ? null : FromItem(response.Item);
    }

    /// <summary>
    /// Every game still being played. Used by the scheduled sweep, which is the only thing that
    /// moves a game's clock when nobody is looking at it. A scan, because games are keyed by
    /// code and there is no index on status — fine for a table this size, and it runs a handful
    /// of times an hour.
    /// </summary>
    public async Task<IReadOnlyList<Game>> ListInProgressAsync(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            var response = await dynamo.ScanAsync(
                new ScanRequest
                {
                    TableName = _options.TableName,
                    FilterExpression = "#status = :status",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "Status" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":status"] = new AttributeValue(nameof(GameStatus.InProgress)),
                    },
                    ExclusiveStartKey = start,
                },
                cancellationToken);

            games.AddRange(response.Items.Select(FromItem));
            start = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (start is not null);

        return games;
    }

    /// <summary>
    /// Every game this player still has a seat in, regardless of status. Used to offer a way
    /// back into a game after the player has navigated somewhere that forgot its code. Players is
    /// a list of maps, which DynamoDB can't filter into on its own, so — same trade-off as the
    /// sweep's scan — this reads the whole table and matches here instead, once each item is
    /// loaded.
    /// </summary>
    public async Task<IReadOnlyList<Game>> ListForPlayerAsync(string sub, CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            var response = await dynamo.ScanAsync(
                new ScanRequest
                {
                    TableName = _options.TableName,
                    ExclusiveStartKey = start,
                },
                cancellationToken);

            games.AddRange(response.Items.Select(FromItem).Where(game => game.FindPlayer(sub) is not null));
            start = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (start is not null);

        return games;
    }

    /// <summary>
    /// Applies a change made outside a request — the sweep bringing a clock forward. Returns
    /// false if the game moved on in the meantime, in which case the next sweep will see it.
    /// </summary>
    /// <param name="hotBefore">The hot borough before the change, so a move can be announced.</param>
    public async Task<bool> TrySaveAsync(
        Game game, long expectedVersion, string? hotBefore, CancellationToken cancellationToken)
    {
        game.Version = expectedVersion + 1;
        try
        {
            await WriteAsync(game, expectedVersion, cancellationToken);
            await AnnounceHotAsync(hotBefore, game, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<Game> CreateAsync(string hostSub, string hostName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var game = new Game
            {
                Code = GameCodeGenerator.Next(),
                HostSub = hostSub,
                Status = GameStatus.Lobby,
                DurationMinutes = _options.DefaultDurationMinutes,
                HotRotationMinutes = _options.DefaultHotRotationMinutes,
                CreatedAt = now,
                Version = 1,
                Players = [new GamePlayer { Sub = hostSub, Name = hostName, JoinedAt = now }],
                FormerPlayers = [],
                Teams = [],
                Hands = [],
                Powers = [],
                Curses = [],
            };

            try
            {
                await dynamo.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = _options.TableName,
                        Item = ToItem(game),
                        ConditionExpression = "attribute_not_exists(#code)",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#code"] = "Code" },
                    },
                    cancellationToken);

                return game;
            }
            catch (ConditionalCheckFailedException)
            {
                // That code is already in play — generate another one.
            }
        }

        throw new InvalidOperationException(
            $"Could not allocate an unused game code after {MaxCodeAttempts} attempts.");
    }

    public Task<GameMutationResult> JoinAsync(
        string code, string sub, string name, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            // Re-joining is a no-op rather than an error: a player who reloads the lobby page
            // shouldn't be told they can't get back into a game they're already in.
            if (game.FindPlayer(sub) is not null)
            {
                return GameMutationStatus.Success;
            }

            // Someone who left a game under way can come back, onto the team they left; nobody
            // else can join one.
            if (game.FormerPlayers.FirstOrDefault(p => p.Sub == sub) is { } former)
            {
                game.FormerPlayers.Remove(former);
                game.Players.Add(new GamePlayer
                {
                    Sub = sub,
                    Name = name,
                    JoinedAt = former.JoinedAt,
                    TeamId = former.TeamId,
                });

                // If everyone had left, the host role left with the last of them.
                if (game.FindPlayer(game.HostSub) is null)
                {
                    game.HostSub = sub;
                }

                return GameMutationStatus.Success;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            if (game.Players.Count >= _options.MaxPlayers)
            {
                return GameMutationStatus.GameFull;
            }

            game.Players.Add(new GamePlayer { Sub = sub, Name = name, JoinedAt = DateTimeOffset.UtcNow });
            return GameMutationStatus.Success;
        }, cancellationToken);

    public Task<GameMutationResult> LeaveAsync(string code, string sub, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            game.Players.Remove(player);

            // In the lobby a player who leaves can simply join again. Once the game is under way
            // nobody new can, so they are remembered, team and all, for when they come back.
            if (game.Status is not GameStatus.Lobby)
            {
                game.FormerPlayers.RemoveAll(p => p.Sub == sub);
                game.FormerPlayers.Add(player);
            }

            // If the host walks out, hand the game to whoever is next in line, otherwise the
            // remaining players are stuck in a lobby nobody can start.
            if (game.IsHost(sub) && game.Players.Count > 0)
            {
                game.HostSub = game.Players[0].Sub;
            }

            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>
    /// <paramref name="powers"/> is the configuration the caller looked up for the game; it is
    /// checked against the game's choice inside the version check, in case the host changed it
    /// in between.
    /// </summary>
    public Task<GameMutationResult> StartAsync(
        string code,
        string sub,
        IReadOnlyList<Challenge> deck,
        PowerConfiguration? powers,
        IReadOnlyList<PowerDefinition> cards,
        CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            if (!game.IsHost(sub))
            {
                return GameMutationStatus.NotHost;
            }

            // A game is only really started once it has a board. Anything flagged in progress
            // without one was never dealt, and can still be started.
            if (game.Status is not GameStatus.Lobby && game.Board is not null)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            if (game.PowerConfigurationId is null)
            {
                return GameMutationStatus.NoPowerConfiguration;
            }

            if (powers is null || powers.Id != game.PowerConfigurationId)
            {
                return GameMutationStatus.PowerConfigurationNotFound;
            }

            game.PowerConfigurationName = powers.Name;

            // Dealing the board and the hands is the engine's job — starting is where a lobby
            // stops being a list of names and becomes a game.
            return engine.Start(game, deck, powers, cards, DateTimeOffset.UtcNow);
        }, cancellationToken);

    /// <summary>
    /// Plays a completed claim challenge from the caller's team hand at a borough.
    /// </summary>
    public Task<(GameMutationResult Result, PlayResult Play)> PlayAsync(
        string code,
        string sub,
        string cardId,
        string boroughId,
        IReadOnlyList<Challenge> deck,
        CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) =>
            engine.Play(game, teamId, cardId, boroughId, deck, DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Keeps some, or none, of a power-card offer; the rest are discarded.</summary>
    public Task<GameMutationResult> KeepPowersAsync(
        string code, string sub, string offerId, IReadOnlyCollection<string> cardIds, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) => engine.KeepPowers(game, teamId, offerId, cardIds), cancellationToken);

    public Task<GameMutationResult> DiscardPowerAsync(
        string code, string sub, string cardId, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) => engine.DiscardPower(game, teamId, cardId), cancellationToken);

    /// <summary>
    /// Plays a power card. For a steal this commits the target, reveals the challenge and starts
    /// the countdown.
    /// </summary>
    public Task<(GameMutationResult Result, PlayResult Play)> PlayPowerAsync(
        string code,
        string sub,
        string cardId,
        string? boroughId,
        string? targetTeamId,
        IReadOnlyList<Challenge> deck,
        CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) =>
            engine.PlayPower(game, teamId, cardId, boroughId, targetTeamId, deck, DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Uses a Curse Reverse on a curse aimed at the caller's team.</summary>
    public Task<(GameMutationResult Result, PlayResult Play)> RespondToCurseAsync(
        string code, string sub, string curseId, string cardId, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) =>
            engine.RespondToCurse(game, teamId, curseId, cardId, DateTimeOffset.UtcNow),
            cancellationToken);

    public Task<GameMutationResult> ClearCurseAsync(
        string code, string sub, string curseId, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) =>
            engine.ClearCurse(game, teamId, curseId, DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Finishes a played Swap — one card taken, one given.</summary>
    public Task<(GameMutationResult Result, PlayResult Play)> CompleteSwapAsync(
        string code, string sub, string swapCardId, string? takeCardId, string? giveCardId, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) =>
            engine.CompleteSwap(game, teamId, swapCardId, takeCardId, giveCardId, DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Reports the outcome of a steal already played against its target.</summary>
    public Task<(GameMutationResult Result, PlayResult Play)> ResolveStealAsync(
        string code, string sub, string cardId, bool succeeded, CancellationToken cancellationToken) =>
        MutateAsTeamAsync(code, sub, (game, teamId) => engine.ResolveSteal(game, teamId, cardId, succeeded), cancellationToken);

    private Task<GameMutationResult> MutateAsTeamAsync(
        string code, string sub, Func<Game, string, GameMutationStatus> mutate, CancellationToken cancellationToken) =>
        MutateAsync(code, game => TeamOf(game, sub, out var teamId) is { } failure ? failure : mutate(game, teamId!), cancellationToken);

    /// <summary>
    /// For moves that report what they did. A retry re-runs the move, so the result always
    /// reflects the attempt that stuck.
    /// </summary>
    private async Task<(GameMutationResult Result, PlayResult Play)> MutateAsTeamAsync(
        string code, string sub, Func<Game, string, PlayResult> mutate, CancellationToken cancellationToken)
    {
        var play = new PlayResult(GameMutationStatus.Conflict);

        var result = await MutateAsTeamAsync(code, sub, (game, teamId) =>
        {
            play = mutate(game, teamId);
            return play.Status;
        }, cancellationToken);

        return (result, play);
    }

    /// <summary>Null when the caller can act for a team, otherwise why not.</summary>
    private static GameMutationStatus? TeamOf(Game game, string sub, out string? teamId)
    {
        teamId = null;

        var player = game.FindPlayer(sub);
        if (player is null)
        {
            return GameMutationStatus.NotInGame;
        }

        if (player.TeamId is null)
        {
            return GameMutationStatus.NotOnATeam;
        }

        teamId = player.TeamId;
        return null;
    }

    /// <summary>
    /// The game's length is a setting rather than a player choice, so it follows the same rule
    /// as starting: the host decides.
    /// </summary>
    public Task<GameMutationResult> SetDurationAsync(
        string code, string sub, int durationMinutes, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            if (!game.IsHost(sub))
            {
                return GameMutationStatus.NotHost;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            game.DurationMinutes = durationMinutes;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>Null hands the size back to the game, which fits it to the teams when it starts.</summary>
    public Task<GameMutationResult> SetBoardSizeAsync(
        string code, string sub, int? boardSize, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            if (!game.IsHost(sub))
            {
                return GameMutationStatus.NotHost;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            game.BoardSize = boardSize;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>Which power deck the game is dealt with is a setting, so the host picks it, in the lobby.</summary>
    public Task<GameMutationResult> SetPowerConfigurationAsync(
        string code, string sub, PowerConfiguration configuration, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            if (!game.IsHost(sub))
            {
                return GameMutationStatus.NotHost;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            game.PowerConfigurationId = configuration.Id;
            game.PowerConfigurationName = configuration.Name;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>How often the hot borough moves is a setting rather than a player choice, so it follows the same rule as duration: the host decides, and only before the game starts.</summary>
    public Task<GameMutationResult> SetHotRotationAsync(
        string code, string sub, int hotRotationMinutes, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            if (!game.IsHost(sub))
            {
                return GameMutationStatus.NotHost;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            game.HotRotationMinutes = hotRotationMinutes;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>
    /// Creates a team and puts the caller in it: someone naming a team almost always wants to
    /// be on it, and switching away afterwards is a single call.
    /// </summary>
    public Task<GameMutationResult> CreateTeamAsync(
        string code, string sub, string name, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            if (game.Teams.Count >= _options.MaxTeams)
            {
                return GameMutationStatus.TooManyTeams;
            }

            // Two teams with the same name are indistinguishable in the lobby.
            if (game.HasTeamNamed(name))
            {
                return GameMutationStatus.DuplicateTeamName;
            }

            var team = new GameTeam
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = name,
                Color = TeamColors.FirstFree(game.Teams),
                Territories = [],
            };
            game.Teams.Add(team);
            player.TeamId = team.Id;

            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>
    /// Any member can change their team's colour, in the lobby, to one no other team has. A
    /// colour is what tells teams apart on the map, so two can never share one.
    /// </summary>
    public Task<GameMutationResult> SetTeamColorAsync(
        string code, string sub, string teamId, string color, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            if (game.FindTeam(teamId) is not { } team)
            {
                return GameMutationStatus.TeamNotFound;
            }

            if (player.TeamId != team.Id)
            {
                return GameMutationStatus.NotYourTeam;
            }

            if (!TeamColors.IsValid(color))
            {
                return GameMutationStatus.InvalidColor;
            }

            if (game.Teams.Any(t => t.Id != team.Id && t.Color == color))
            {
                return GameMutationStatus.ColorTaken;
            }

            team.Color = color;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>
    /// Picking a team and changing your mind are the same operation — the player's team is
    /// simply set to the one they chose.
    /// </summary>
    public Task<GameMutationResult> JoinTeamAsync(
        string code, string sub, string teamId, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            if (game.FindTeam(teamId) is null)
            {
                return GameMutationStatus.TeamNotFound;
            }

            player.TeamId = teamId;
            return GameMutationStatus.Success;
        }, cancellationToken);

    public Task<GameMutationResult> LeaveTeamAsync(
        string code, string sub, CancellationToken cancellationToken) =>
        MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            if (game.Status is not GameStatus.Lobby)
            {
                return GameMutationStatus.AlreadyStarted;
            }

            player.TeamId = null;
            return GameMutationStatus.Success;
        }, cancellationToken);

    /// <summary>
    /// Reads the game, applies <paramref name="mutate"/>, and writes it back only if nobody
    /// else has written since the read. The rules live inside the loop on purpose: a retry
    /// means the game changed underneath us, so a decision taken against the previous version
    /// (there was room, the game hadn't started) has to be taken again against the new one.
    /// </summary>
    private async Task<GameMutationResult> MutateAsync(
        string code, Func<Game, GameMutationStatus> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var game = await LoadAsync(code, cancellationToken);
            if (game is null)
            {
                return new GameMutationResult(GameMutationStatus.NotFound, null);
            }

            var expectedVersion = game.Version;
            var hotBefore = HotOf(game);

            // Bring deadlines up to date before the rules look at the game, so a play is judged
            // against a board where lapsed locks are open and the hot borough has moved on.
            engine.Advance(game, DateTimeOffset.UtcNow);

            var status = mutate(game);
            if (status is not GameMutationStatus.Success)
            {
                return new GameMutationResult(status, game);
            }

            game.Version = expectedVersion + 1;

            try
            {
                await WriteAsync(game, expectedVersion, cancellationToken);
                await AnnounceHotAsync(hotBefore, game, cancellationToken);
                return new GameMutationResult(GameMutationStatus.Success, game);
            }
            catch (ConditionalCheckFailedException)
            {
                // Another player wrote first. Re-read and reapply the change on top of theirs.
            }
        }

        return new GameMutationResult(GameMutationStatus.Conflict, null);
    }

    /// <summary>
    /// Writes the game back only if nobody else has written since it was read. An emptied game
    /// is deleted rather than stored, since there is no lobby left to show anyone — unless
    /// someone who left mid-game could still come back to it, in which case TTL clears it once
    /// nobody does.
    /// </summary>
    private async Task WriteAsync(Game game, long expectedVersion, CancellationToken cancellationToken)
    {
        const string condition = "#version = :expectedVersion";
        var names = new Dictionary<string, string> { ["#version"] = "Version" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":expectedVersion"] = Number(expectedVersion),
        };

        if (game.Players.Count == 0 && game.FormerPlayers.Count == 0)
        {
            await dynamo.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = _options.TableName,
                    Key = KeyFor(game.Code),
                    ConditionExpression = condition,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                },
                cancellationToken);

            return;
        }

        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = _options.TableName,
                Item = ToItem(game),
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            },
            cancellationToken);
    }

    private static Dictionary<string, AttributeValue> KeyFor(string code) =>
        new() { ["Code"] = new AttributeValue(code) };

    private static AttributeValue Number(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private Dictionary<string, AttributeValue> ToItem(Game game) => new()
    {
        ["Code"] = new AttributeValue(game.Code),
        ["HostSub"] = new AttributeValue(game.HostSub),
        ["Status"] = new AttributeValue(game.Status.ToString()),
        ["CreatedAt"] = new AttributeValue(game.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        ["Version"] = Number(game.Version),
        ["DurationMinutes"] = Number(game.DurationMinutes),
        ["HotRotationMinutes"] = Number(game.HotRotationMinutes),
        ["BoardSize"] = game.BoardSize is { } boardSize ? Number(boardSize) : new AttributeValue { NULL = true },
        // Measured from this write rather than from creation, so every action pushes the expiry
        // out and only genuinely idle lobbies age away. Basing it on CreatedAt would delete a
        // game mid-play once it had been running for the TTL — reachable now that a game can be
        // set to last as long as a day.
        ["ExpiresAt"] = Number(
            DateTimeOffset.UtcNow.AddHours(_options.TimeToLiveHours).ToUnixTimeSeconds()),
        ["Players"] = PlayersToAttribute(game.Players),
        ["FormerPlayers"] = PlayersToAttribute(game.FormerPlayers),
        // Board, hands and territories nest several levels deep and are never queried into —
        // only ever read back whole — so they are stored as JSON rather than as five levels of
        // nested DynamoDB maps and lists.
        ["Board"] = Json(game.Board),
        ["Hands"] = Json(game.Hands),
        ["Powers"] = Json(game.Powers),
        ["PowerDeck"] = Json(game.PowerDeck),
        ["Curses"] = Json(game.Curses),
        ["PowerConfigurationId"] = OptionalAttribute(game.PowerConfigurationId),
        ["PowerConfigurationName"] = OptionalAttribute(game.PowerConfigurationName),
        ["StartedAt"] = OptionalTimestamp(game.StartedAt),
        ["EndsAt"] = OptionalTimestamp(game.EndsAt),
        ["EndingSoonNotifiedAt"] = OptionalTimestamp(game.EndingSoonNotifiedAt),
        ["Teams"] = new AttributeValue
        {
            L = game.Teams
                .Select(team => new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["Id"] = new AttributeValue(team.Id),
                        ["Name"] = new AttributeValue(team.Name),
                        ["Color"] = team.Color is null ? new AttributeValue { NULL = true } : new AttributeValue(team.Color),
                        ["Territories"] = Json(team.Territories),
                        ["BonusPoints"] = Number(team.BonusPoints),
                    },
                })
                .ToList(),
        },
    };

    private static Game FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        Code = item["Code"].S,
        HostSub = item["HostSub"].S,
        Status = Enum.Parse<GameStatus>(item["Status"].S),
        CreatedAt = DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture),
        Version = long.Parse(item["Version"].N, CultureInfo.InvariantCulture),
        // Games created before the duration setting existed fall back to an hour.
        DurationMinutes = item.TryGetValue("DurationMinutes", out var duration)
            ? int.Parse(duration.N, CultureInfo.InvariantCulture)
            : 60,
        // Games created before the hot-rotation setting existed fall back to the old fixed value.
        HotRotationMinutes = item.TryGetValue("HotRotationMinutes", out var hotRotation)
            ? int.Parse(hotRotation.N, CultureInfo.InvariantCulture)
            : 90,
        BoardSize = item.TryGetValue("BoardSize", out var boardSize) && boardSize.N is { } size
            ? int.Parse(size, CultureInfo.InvariantCulture)
            : null,
        Players = PlayersFromAttribute(item["Players"]),
        // Games written before players could come back have no FormerPlayers attribute at all.
        FormerPlayers = item.TryGetValue("FormerPlayers", out var formerPlayers)
            ? PlayersFromAttribute(formerPlayers)
            : [],
        // Games created before teams existed have no Teams attribute at all.
        Teams = item.TryGetValue("Teams", out var teams)
            ? teams.L
                .Select(team => new GameTeam
                {
                    Id = team.M["Id"].S,
                    Name = team.M["Name"].S,
                    Color = OptionalString(team.M, "Color"),
                    Territories = FromJson<List<Territory>>(team.M, "Territories") ?? [],
                    BonusPoints = team.M.TryGetValue("BonusPoints", out var bonus)
                        ? int.Parse(bonus.N, CultureInfo.InvariantCulture)
                        : 0,
                })
                .ToList()
            : [],
        Board = FromJson<GameBoard>(item, "Board"),
        Hands = FromJson<List<TeamHand>>(item, "Hands") ?? [],
        Powers = FromJson<List<TeamPowers>>(item, "Powers") ?? [],
        PowerDeck = FromJson<PowerDeck>(item, "PowerDeck"),
        Curses = FromJson<List<ActiveCurse>>(item, "Curses") ?? [],
        PowerConfigurationId = OptionalString(item, "PowerConfigurationId"),
        PowerConfigurationName = OptionalString(item, "PowerConfigurationName"),
        StartedAt = OptionalDate(item, "StartedAt"),
        EndsAt = OptionalDate(item, "EndsAt"),
        EndingSoonNotifiedAt = OptionalDate(item, "EndingSoonNotifiedAt"),
    };

    private static AttributeValue PlayersToAttribute(IEnumerable<GamePlayer> players) => new()
    {
        L = players
            .Select(player => new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    ["Sub"] = new AttributeValue(player.Sub),
                    ["Name"] = new AttributeValue(player.Name),
                    ["JoinedAt"] = new AttributeValue(
                        player.JoinedAt.ToString("O", CultureInfo.InvariantCulture)),
                    ["TeamId"] = player.TeamId is null
                        ? new AttributeValue { NULL = true }
                        : new AttributeValue(player.TeamId),
                },
            })
            .ToList(),
    };

    private static List<GamePlayer> PlayersFromAttribute(AttributeValue players) =>
        players.L
            .Select(player => new GamePlayer
            {
                Sub = player.M["Sub"].S,
                Name = player.M["Name"].S,
                JoinedAt = DateTimeOffset.Parse(player.M["JoinedAt"].S, CultureInfo.InvariantCulture),
                TeamId = OptionalString(player.M, "TeamId"),
            })
            .ToList();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AttributeValue Json<T>(T value) =>
        value is null
            ? new AttributeValue { NULL = true }
            : new AttributeValue(JsonSerializer.Serialize(value, JsonOptions));

    private static T? FromJson<T>(Dictionary<string, AttributeValue> item, string name)
        where T : class =>
        item.TryGetValue(name, out var value) && value.S is { } json
            ? JsonSerializer.Deserialize<T>(json, JsonOptions)
            : null;

    private static AttributeValue OptionalTimestamp(DateTimeOffset? value) =>
        value is null
            ? new AttributeValue { NULL = true }
            : new AttributeValue(value.Value.ToString("O", CultureInfo.InvariantCulture));

    private static DateTimeOffset? OptionalDate(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.S is { } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
            : null;

    private static AttributeValue OptionalAttribute(string? value) =>
        value is null ? new AttributeValue { NULL = true } : new AttributeValue(value);

    /// <summary>Reads an attribute that may be absent, or present and explicitly null.</summary>
    private static string? OptionalString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;
}
