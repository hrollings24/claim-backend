using System.Globalization;
using Amazon.DynamoDBv2;
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
    Conflict,
}

public record GameMutationResult(GameMutationStatus Status, Game? Game);

/// <summary>
/// Stores each game as a single DynamoDB item keyed by its code. Writes are guarded by a
/// version attribute, so concurrent joins and leaves queue up behind each other instead of
/// silently dropping one another's changes to the roster.
/// </summary>
public class GameStore(IAmazonDynamoDB dynamo, IOptions<GamesOptions> options)
{
    /// <summary>Retries for a write losing the version check to another player's write.</summary>
    private const int MaxWriteAttempts = 5;

    /// <summary>Retries for a freshly generated code colliding with a live game.</summary>
    private const int MaxCodeAttempts = 5;

    private readonly GamesOptions _options = options.Value;

    public async Task<Game?> GetAsync(string code, CancellationToken cancellationToken)
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
                CreatedAt = now,
                Version = 1,
                Players = [new GamePlayer { Sub = hostSub, Name = hostName, JoinedAt = now }],
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

    public Task<GameMutationResult> StartAsync(string code, string sub, CancellationToken cancellationToken) =>
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

            game.Status = GameStatus.InProgress;
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
            var game = await GetAsync(code, cancellationToken);
            if (game is null)
            {
                return new GameMutationResult(GameMutationStatus.NotFound, null);
            }

            var status = mutate(game);
            if (status is not GameMutationStatus.Success)
            {
                return new GameMutationResult(status, game);
            }

            var expectedVersion = game.Version;
            game.Version = expectedVersion + 1;

            var versionCondition = new
            {
                Expression = "#version = :expectedVersion",
                Names = new Dictionary<string, string> { ["#version"] = "Version" },
                Values = new Dictionary<string, AttributeValue>
                {
                    [":expectedVersion"] = Number(expectedVersion),
                },
            };

            try
            {
                if (game.Players.Count == 0)
                {
                    // The last player left, so there is no lobby left to show anyone.
                    await dynamo.DeleteItemAsync(
                        new DeleteItemRequest
                        {
                            TableName = _options.TableName,
                            Key = KeyFor(game.Code),
                            ConditionExpression = versionCondition.Expression,
                            ExpressionAttributeNames = versionCondition.Names,
                            ExpressionAttributeValues = versionCondition.Values,
                        },
                        cancellationToken);
                }
                else
                {
                    await dynamo.PutItemAsync(
                        new PutItemRequest
                        {
                            TableName = _options.TableName,
                            Item = ToItem(game),
                            ConditionExpression = versionCondition.Expression,
                            ExpressionAttributeNames = versionCondition.Names,
                            ExpressionAttributeValues = versionCondition.Values,
                        },
                        cancellationToken);
                }

                return new GameMutationResult(GameMutationStatus.Success, game);
            }
            catch (ConditionalCheckFailedException)
            {
                // Another player wrote first. Re-read and reapply the change on top of theirs.
            }
        }

        return new GameMutationResult(GameMutationStatus.Conflict, null);
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
        ["ExpiresAt"] = Number(
            game.CreatedAt.AddHours(_options.TimeToLiveHours).ToUnixTimeSeconds()),
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
        Players = item["Players"].L
            .Select(player => new GamePlayer
            {
                Sub = player.M["Sub"].S,
                Name = player.M["Name"].S,
                JoinedAt = DateTimeOffset.Parse(player.M["JoinedAt"].S, CultureInfo.InvariantCulture),
            })
            .ToList(),
    };
}
