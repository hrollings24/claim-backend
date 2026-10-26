using ClaimBackend.Api.Games;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Push;

public record SweepResult(int GamesChecked, int HotRotations, int EndingSoon, int Finished);

/// <summary>
/// Moves every running game's clock forward and says what changed.
///
/// The engine advances deadlines when a game is read, which is enough while somebody is looking
/// at it. Notifications are the opposite case — the hot borough moving is worth telling people
/// about precisely when nobody has the app open — so something has to look on their behalf.
/// Whichever request moves it, the store is what announces it.
/// </summary>
public class GameSweeper(
    GameStore store,
    GameEngine engine,
    GameNotifier notifier,
    IOptions<GamesOptions> options,
    ILogger<GameSweeper> logger)
{
    private readonly GamesOptions _options = options.Value;

    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var games = await store.ListInProgressAsync(cancellationToken);
        int hotRotations = 0, endingSoon = 0, finished = 0;

        foreach (var game in games)
        {
            var expectedVersion = game.Version;
            var hotBefore = game.Board?.HotBoroughId;

            var changed = engine.Advance(game, DateTimeOffset.UtcNow);
            var warnDue = ShouldWarn(game, DateTimeOffset.UtcNow);

            if (warnDue)
            {
                game.EndingSoonNotifiedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            // The store announces a hot borough move once the write has stuck, as it does for every
            // other request — so a rotation that lost its write is never announced.
            if (!await store.TrySaveAsync(game, expectedVersion, hotBefore, cancellationToken))
            {
                logger.LogInformation("Game {Code} changed under the sweep; leaving it to the next pass.", game.Code);
                continue;
            }

            var hotAfter = game.Board?.HotBoroughId;
            if (hotAfter is not null && hotAfter != hotBefore && game.Status is GameStatus.InProgress)
            {
                hotRotations++;
            }

            if (warnDue && game.Status is GameStatus.InProgress)
            {
                endingSoon++;
                await notifier.EndingSoonAsync(game, _options.EndingSoonMinutes, cancellationToken);
            }

            if (game.Status is GameStatus.Finished)
            {
                finished++;
            }
        }

        return new SweepResult(games.Count, hotRotations, endingSoon, finished);
    }

    /// <summary>
    /// True once, when the game is inside its final stretch. The flag on the game is what stops
    /// a sweep running every few minutes from sending it repeatedly.
    /// </summary>
    private bool ShouldWarn(Game game, DateTimeOffset now) =>
        game.Status is GameStatus.InProgress
        && game.EndingSoonNotifiedAt is null
        && game.EndsAt is { } endsAt
        && endsAt > now
        && endsAt - now <= TimeSpan.FromMinutes(_options.EndingSoonMinutes);
}
