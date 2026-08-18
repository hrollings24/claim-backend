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

    /// <summary>The team this player picked, or null while they are still undecided.</summary>
    public string? TeamId { get; set; }
}

public class GameTeam
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public class Game
{
    public required string Code { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<GamePlayer> Players { get; init; }

    public required List<GameTeam> Teams { get; init; }

    /// <summary>Cognito `sub` of the player who may start the game. Moves on if they leave.</summary>
    public required string HostSub { get; set; }

    public required GameStatus Status { get; set; }

    /// <summary>
    /// How long the game is meant to run, held as total minutes so there is one number to
    /// store and compare; the lobby presents it as hours and minutes.
    /// </summary>
    public required int DurationMinutes { get; set; }

    /// <summary>
    /// Incremented on every write and used as the condition on the next one, so two players
    /// acting at the same time can't overwrite each other's change to the roster.
    /// </summary>
    public required long Version { get; set; }

    public bool IsHost(string sub) => HostSub == sub;

    public GamePlayer? FindPlayer(string sub) => Players.FirstOrDefault(p => p.Sub == sub);

    public GameTeam? FindTeam(string teamId) => Teams.FirstOrDefault(t => t.Id == teamId);

    public bool HasTeamNamed(string name) =>
        Teams.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
