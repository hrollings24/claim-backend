using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using ClaimBackend.Api.Challenges;
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
    BoroughLocked,
    GameNotRunning,
    NoCounterWindow,
    TeamNotFound,
    DuplicateTeamName,
    TooManyTeams,
    Conflict,
}

public record GameMutationResult(GameMutationStatus Status, Game? Game);

/// <summary>
/// Stores each game as a single DynamoDB item keyed by its code. Writes are guarded by a
/// version attribute, so concurrent joins and leaves queue up behind each other instead of
/// silently dropping one another's changes to the roster.
/// </summary>
public class GameStore(IAmazonDynamoDB dynamo, IOptions<GamesOptions> options, GameEngine engine)
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
        if (!engine.Advance(game, DateTimeOffset.UtcNow))
        {
            return game;
        }

        game.Version = expectedVersion + 1;
        try
        {
            await WriteAsync(game, expectedVersion, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            // Another request advanced it first, to the same place.
        }

        return game;
    }

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
                CreatedAt = now,
                Version = 1,
                Players = [new GamePlayer { Sub = hostSub, Name = hostName, JoinedAt = now }],
                Teams = [],
                Hands = [],
                CounterWindows = [],
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

            // If the host walks out, hand the game to whoever is next in line, otherwise the
            // remaining players are stuck in a lobby nobody can start.
            if (game.IsHost(sub) && game.Players.Count > 0)
            {
                game.HostSub = game.Players[0].Sub;
            }

            return GameMutationStatus.Success;
        }, cancellationToken);

    public Task<GameMutationResult> StartAsync(
        string code, string sub, IReadOnlyList<Challenge> deck, CancellationToken cancellationToken) =>
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

            // Dealing the board and the hands is the engine's job — starting is where a lobby
            // stops being a list of names and becomes a game.
            return engine.Start(game, deck, DateTimeOffset.UtcNow);
        }, cancellationToken);

    /// <summary>
    /// Plays a card from the caller's team hand at a borough, reporting the outcome of the
    /// challenge. The engine decides what that means — claim, steal, or a counter-attack.
    /// </summary>
    public async Task<(GameMutationResult Result, PlayEffect Effect)> PlayAsync(
        string code,
        string sub,
        string cardId,
        string boroughId,
        bool succeeded,
        IReadOnlyList<Challenge> deck,
        CancellationToken cancellationToken)
    {
        var effect = PlayEffect.Nothing;

        var result = await MutateAsync(code, game =>
        {
            var player = game.FindPlayer(sub);
            if (player is null)
            {
                return GameMutationStatus.NotInGame;
            }

            if (player.TeamId is null)
            {
                return GameMutationStatus.NotOnATeam;
            }

            var play = engine.Play(
                game, player.TeamId, cardId, boroughId, succeeded, deck, DateTimeOffset.UtcNow);

            // A retry re-runs this, so the effect always reflects the attempt that stuck.
            effect = play.Effect;
            return play.Status;
        }, cancellationToken);

        return (result, effect);
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

            var team = new GameTeam { Id = Guid.NewGuid().ToString("n"), Name = name, Territories = [] };
            game.Teams.Add(team);
            player.TeamId = team.Id;

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
    /// is deleted rather than stored, since there is no lobby left to show anyone.
    /// </summary>
    private async Task WriteAsync(Game game, long expectedVersion, CancellationToken cancellationToken)
    {
        const string condition = "#version = :expectedVersion";
        var names = new Dictionary<string, string> { ["#version"] = "Version" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":expectedVersion"] = Number(expectedVersion),
        };

        if (game.Players.Count == 0)
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
        // Measured from this write rather than from creation, so every action pushes the expiry
        // out and only genuinely idle lobbies age away. Basing it on CreatedAt would delete a
        // game mid-play once it had been running for the TTL — reachable now that a game can be
        // set to last as long as a day.
        ["ExpiresAt"] = Number(
            DateTimeOffset.UtcNow.AddHours(_options.TimeToLiveHours).ToUnixTimeSeconds()),
        ["Players"] = new AttributeValue
        {
            L = game.Players
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
        },
        // Board, hands and territories nest several levels deep and are never queried into —
        // only ever read back whole — so they are stored as JSON rather than as five levels of
        // nested DynamoDB maps and lists.
        ["Board"] = Json(game.Board),
        ["Hands"] = Json(game.Hands),
        ["CounterWindows"] = Json(game.CounterWindows),
        ["StartedAt"] = OptionalTimestamp(game.StartedAt),
        ["EndsAt"] = OptionalTimestamp(game.EndsAt),
        ["Teams"] = new AttributeValue
        {
            L = game.Teams
                .Select(team => new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["Id"] = new AttributeValue(team.Id),
                        ["Name"] = new AttributeValue(team.Name),
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
        Players = item["Players"].L
            .Select(player => new GamePlayer
            {
                Sub = player.M["Sub"].S,
                Name = player.M["Name"].S,
                JoinedAt = DateTimeOffset.Parse(player.M["JoinedAt"].S, CultureInfo.InvariantCulture),
                TeamId = OptionalString(player.M, "TeamId"),
            })
            .ToList(),
        // Games created before teams existed have no Teams attribute at all.
        Teams = item.TryGetValue("Teams", out var teams)
            ? teams.L
                .Select(team => new GameTeam
                {
                    Id = team.M["Id"].S,
                    Name = team.M["Name"].S,
                    Territories = FromJson<List<Territory>>(team.M, "Territories") ?? [],
                    BonusPoints = team.M.TryGetValue("BonusPoints", out var bonus)
                        ? int.Parse(bonus.N, CultureInfo.InvariantCulture)
                        : 0,
                })
                .ToList()
            : [],
        Board = FromJson<GameBoard>(item, "Board"),
        Hands = FromJson<List<TeamHand>>(item, "Hands") ?? [],
        CounterWindows = FromJson<List<CounterWindow>>(item, "CounterWindows") ?? [],
        StartedAt = OptionalDate(item, "StartedAt"),
        EndsAt = OptionalDate(item, "EndsAt"),
    };

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

    /// <summary>Reads an attribute that may be absent, or present and explicitly null.</summary>
    private static string? OptionalString(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;
}
