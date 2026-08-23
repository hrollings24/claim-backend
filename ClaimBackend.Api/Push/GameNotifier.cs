using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Games;

namespace ClaimBackend.Api.Push;

/// <summary>
/// Decides what a game event is worth telling people, and which people. Deliberately quiet:
/// a phone that buzzes for everything gets silenced, and then the notifications that matter —
/// a counter window closing in fifteen minutes — go unseen with the rest.
/// </summary>
public class GameNotifier(PushSender sender)
{
    public Task PlayedAsync(
        Game game, string actingTeamId, string boroughId, PlayResult play, CancellationToken cancellationToken)
    {
        var borough = BoroughCatalogue.NameOf(boroughId);
        var actor = NameOfTeam(game, actingTeamId);

        return play.Effect switch
        {
            // Everyone else is deciding where to go next, and the board just changed.
            PlayEffect.BoroughClaimed => sender.SendAsync(
                Everyone(game, except: actingTeamId),
                new PushNotification($"{actor} claimed {borough}", "The board has moved on.", Url(game)),
                cancellationToken),

            PlayEffect.HotBoroughClaimed => sender.SendAsync(
                Everyone(game, except: actingTeamId),
                new PushNotification(
                    $"{actor} took the hot borough",
                    $"{borough} is theirs for good, and worth two. A new borough is hot.",
                    Url(game)),
                cancellationToken),

            // Losing a borough is the thing you would most want to hear about.
            PlayEffect.BoroughStolen => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} stole {borough}",
                    "It is locked to them now — that one is settled.",
                    Url(game)),
                cancellationToken),

            // Time critical: the window shuts in fifteen minutes whether or not they noticed.
            PlayEffect.StealFailed => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} failed a steal on you",
                    "You have 15 minutes to complete a claim challenge and take any borough of theirs.",
                    Url(game),
                    Tag: "counter"),
                cancellationToken),

            PlayEffect.CounterSucceeded => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} hit back and took {borough}",
                    "That was their counter-attack for the steal you missed.",
                    Url(game)),
                cancellationToken),

            _ => Task.CompletedTask,
        };
    }

    public Task StartedAsync(Game game, CancellationToken cancellationToken) =>
        sender.SendAsync(
            Everyone(game),
            new PushNotification(
                "The game has started",
                "Six boroughs are up for grabs. One of them is hot.",
                Url(game)),
            cancellationToken);

    public Task HotBoroughChangedAsync(Game game, string boroughId, CancellationToken cancellationToken) =>
        sender.SendAsync(
            Everyone(game),
            new PushNotification(
                $"{BoroughCatalogue.NameOf(boroughId)} is now hot",
                "Worth two points, and locked for good once claimed.",
                Url(game),
                // Replaces the previous hot notification rather than stacking beside it — only
                // one borough is ever hot, so an older one is just wrong.
                Tag: "hot"),
            cancellationToken);

    public Task EndingSoonAsync(Game game, int minutesLeft, CancellationToken cancellationToken) =>
        sender.SendAsync(
            Everyone(game),
            new PushNotification(
                $"{minutesLeft} minutes left",
                "Last chance to claim anything you can reach.",
                Url(game),
                Tag: "ending"),
            cancellationToken);

    private static string Url(Game game) => $"/lobby/{game.Code}";

    private static string NameOfTeam(Game game, string? teamId) =>
        game.Teams.FirstOrDefault(t => t.Id == teamId)?.Name ?? "Another team";

    private static IEnumerable<string> Everyone(Game game, string? except = null) =>
        game.Players
            .Where(p => except is null || p.TeamId != except)
            .Select(p => p.Sub);

    private static IEnumerable<string> InTeam(Game game, string? teamId) =>
        teamId is null ? [] : game.Players.Where(p => p.TeamId == teamId).Select(p => p.Sub);
}
