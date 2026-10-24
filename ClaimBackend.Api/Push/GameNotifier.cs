using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Games;

namespace ClaimBackend.Api.Push;

/// <summary>
/// Decides what a game event is worth telling people, and which people. Deliberately quiet:
/// a phone that buzzes for everything gets silenced, and then the notifications that matter —
/// a borough being stolen out from under you — go unseen with the rest.
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
                    $"{borough} is worth two to them, and they drew double power cards. A new borough is hot.",
                    Url(game)),
                cancellationToken),

            // Losing a borough is the thing you would most want to hear about.
            PlayEffect.BoroughStolen => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} stole {borough}",
                    "It's theirs now — unless you steal it back.",
                    Url(game)),
                cancellationToken),

            // Without this the defender wouldn't know to reach for a Veto in time.
            PlayEffect.StealStarted => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} is stealing {borough}",
                    "They're on the clock to take it off you.",
                    Url(game)),
                cancellationToken),

            PlayEffect.CurseCast => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} cursed you: {play.Detail}",
                    "It's on you now. Open the app to see it — or reverse it.",
                    Url(game)),
                cancellationToken),

            PlayEffect.BoroughFortified => sender.SendAsync(
                Everyone(game, except: actingTeamId),
                new PushNotification(
                    $"{actor} fortified {borough}",
                    "It's theirs for the rest of the game — nobody can steal it.",
                    Url(game)),
                cancellationToken),

            PlayEffect.SwapStarted => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} is going through your power cards",
                    "They're about to take one, and give you one of theirs.",
                    Url(game)),
                cancellationToken),

            PlayEffect.Swapped => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} swapped power cards with you",
                    $"They {play.Detail}.",
                    Url(game)),
                cancellationToken),

            PlayEffect.CurseReversed => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} reversed your curse",
                    $"{play.Detail} is on you now.",
                    Url(game)),
                cancellationToken),

            PlayEffect.StealFailed => sender.SendAsync(
                InTeam(game, play.CounterpartTeamId),
                new PushNotification(
                    $"{actor} failed a steal on {borough}",
                    "You held on, and drew power cards for it. Pick one to keep.",
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
                $"{game.Board?.Active.Count ?? 0} boroughs are up for grabs. One of them is hot.",
                Url(game)),
            cancellationToken);

    public Task HotBoroughChangedAsync(Game game, string boroughId, CancellationToken cancellationToken) =>
        sender.SendAsync(
            Everyone(game),
            new PushNotification(
                $"{BoroughCatalogue.NameOf(boroughId)} is now hot",
                "Worth two points, and double power cards for whoever claims it.",
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
