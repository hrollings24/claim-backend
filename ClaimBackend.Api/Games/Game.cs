namespace ClaimBackend.Api.Games;

public enum GameStatus
{
    Lobby,
    InProgress,
}

public class GamePlayer
{
    public required string Sub { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset JoinedAt { get; init; }
}

public class Game
{
    public required string Code { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<GamePlayer> Players { get; init; }

    /// <summary>Cognito `sub` of the player who may start the game. Moves on if they leave.</summary>
    public required string HostSub { get; set; }

    public required GameStatus Status { get; set; }

    /// <summary>
    /// Incremented on every write and used as the condition on the next one, so two players
    /// acting at the same time can't overwrite each other's change to the roster.
    /// </summary>
    public required long Version { get; set; }

    public bool IsHost(string sub) => HostSub == sub;

    public GamePlayer? FindPlayer(string sub) => Players.FirstOrDefault(p => p.Sub == sub);
}
