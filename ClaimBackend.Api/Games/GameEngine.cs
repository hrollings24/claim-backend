using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Challenges;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Games;

/// <summary>What a played card actually did, so the app can say something better than "ok".</summary>
public enum PlayEffect
{
    Nothing,
    BoroughClaimed,
    HotBoroughClaimed,
    BoroughStolen,
    StealFailed,
    CounterSucceeded,
}

public record PlayResult(GameMutationStatus Status, PlayEffect Effect = PlayEffect.Nothing);

/// <summary>
/// The rules. Every method mutates the game in place and is called inside the store's version
/// check, so a play that races another team's play is re-read and re-applied rather than
/// overwriting it.
///
/// Timers are deadlines rather than scheduled work: nothing runs between requests, and
/// <see cref="Advance"/> brings the world up to date whenever the game is looked at. That suits
/// a Lambda with no scheduler, and costs nothing while a game sits idle.
/// </summary>
public class GameEngine(IOptions<GamesOptions> options)
{
    private readonly GamesOptions _options = options.Value;

    public GameMutationStatus Start(Game game, IReadOnlyList<Challenge> deck, DateTimeOffset now)
    {
        // Stealing is most of the game, and there is nobody to steal from with one team.
        if (game.Teams.Count < 2)
        {
            return GameMutationStatus.NotEnoughTeams;
        }

        if (deck.Count < _options.HandSize
            || !deck.Any(c => c.Type is ChallengeType.Claim)
            || !deck.Any(c => c.Type is ChallengeType.Steal))
        {
            return GameMutationStatus.NotEnoughChallenges;
        }

        var pool = BoroughCatalogue.All.Select(b => b.Id).OrderBy(_ => Random.Shared.Next()).ToList();
        var active = pool.Take(_options.ActiveBoroughCount).ToList();

        game.Board = new GameBoard
        {
            Active = active.Select(id => new ActiveBorough { BoroughId = id }).ToList(),
            Remaining = pool.Skip(_options.ActiveBoroughCount).ToList(),
            HotBoroughId = active[Random.Shared.Next(active.Count)],
            HotRotatesAt = now.AddMinutes(_options.HotRotationMinutes),
        };

        game.Hands.Clear();
        foreach (var team in game.Teams)
        {
            game.Hands.Add(new TeamHand
            {
                TeamId = team.Id,
                // Shuffle and take, rather than drawing each card independently: independent
                // draws repeat, and with a small deck a five card hand was coming up with two
                // pairs. Two teams can still share a challenge — with fewer cards than the
                // table needs, they have to.
                Cards = deck
                    .OrderBy(_ => Random.Shared.Next())
                    .Take(_options.HandSize)
                    .Select(ToHandCard)
                    .ToList(),
            });
        }

        game.StartedAt = now;
        game.EndsAt = now.AddMinutes(game.DurationMinutes);
        game.Status = GameStatus.InProgress;

        return GameMutationStatus.Success;
    }

    /// <summary>
    /// Brings deadlines up to date: locks lapse, counter windows close, the hot borough moves on,
    /// and the game ends when its time runs out. Safe to call repeatedly. Returns whether
    /// anything actually moved, because the caller has to persist it if so — rotating the hot
    /// borough is a random choice, and re-rolling it on every read would land somewhere new each
    /// time anyone looked.
    /// </summary>
    public bool Advance(Game game, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress || game.Board is not { } board)
        {
            return false;
        }

        if (game.EndsAt is { } endsAt && now >= endsAt)
        {
            // Freeze everything as it stands; the scores are whatever they were at the whistle.
            game.Status = GameStatus.Finished;
            return true;
        }

        // Locks are permanent, so the only deadlines left to bring forward are the counter
        // window and the hot borough.
        var changed = game.CounterWindows.RemoveAll(window => window.ExpiresAt <= now) > 0;

        // The hot borough must always be one of the active — unclaimed by definition — so it also
        // moves the moment the borough it was sitting on is claimed out from under it.
        var hotStillActive = board.HotBoroughId is { } hot
            && board.Active.Any(a => a.BoroughId == hot);

        if (!hotStillActive || now >= board.HotRotatesAt)
        {
            RotateHot(board, now);
            changed = true;
        }

        return changed;
    }

    public PlayResult Play(
        Game game,
        string teamId,
        string cardId,
        string boroughId,
        bool succeeded,
        IReadOnlyList<Challenge> deck,
        DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress || game.Board is not { } board)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var team = game.Teams.FirstOrDefault(t => t.Id == teamId);
        var hand = game.FindHand(teamId);
        if (team is null || hand is null)
        {
            return new PlayResult(GameMutationStatus.NotOnATeam);
        }

        var card = hand.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null)
        {
            return new PlayResult(GameMutationStatus.CardNotInHand);
        }

        var result = card.Type is ChallengeType.Claim
            ? PlayClaim(game, board, team, boroughId, succeeded, now)
            : PlaySteal(game, team, boroughId, succeeded, now);

        // A card is spent whether or not the challenge came off, and the hand is topped back up.
        if (result.Status is GameMutationStatus.Success)
        {
            // Chosen before the played card leaves the hand, so the challenge just attempted
            // isn't handed straight back.
            var replacement = DrawReplacement(deck, hand);
            hand.Cards.Remove(card);
            hand.Cards.Add(replacement);
        }

        return result;
    }

    private PlayResult PlayClaim(
        Game game, GameBoard board, GameTeam team, string boroughId, bool succeeded, DateTimeOffset now)
    {
        var active = board.Active.FirstOrDefault(a => a.BoroughId == boroughId);

        if (active is null)
        {
            // Not on the board — the only other legal use of a claim card is hitting back at the
            // team that just failed a steal against you.
            return PlayCounter(game, team, boroughId, succeeded, now);
        }

        if (!succeeded)
        {
            // A failed claim costs the card and nothing else.
            return new PlayResult(GameMutationStatus.Success);
        }

        var wasHot = board.HotBoroughId == boroughId;
        var territory = new Territory { BoroughId = boroughId };

        if (wasHot)
        {
            team.BonusPoints++;
            territory.Locked = true;
        }

        team.Territories.Add(territory);
        board.Active.Remove(active);
        TopUp(board, boroughId);

        if (wasHot)
        {
            RotateHot(board, now);
        }

        return new PlayResult(
            GameMutationStatus.Success,
            wasHot ? PlayEffect.HotBoroughClaimed : PlayEffect.BoroughClaimed);
    }

    private PlayResult PlaySteal(
        Game game, GameTeam team, string boroughId, bool succeeded, DateTimeOffset now)
    {
        var holder = game.FindTeamHolding(boroughId);
        if (holder is null || holder.Id == team.Id)
        {
            return new PlayResult(GameMutationStatus.InvalidTarget);
        }

        var territory = holder.FindTerritory(boroughId)!;
        if (territory.IsLocked)
        {
            return new PlayResult(GameMutationStatus.BoroughLocked);
        }

        if (!succeeded)
        {
            // The defender gets a window to take one of the attacker's boroughs instead. They
            // choose which, and need not travel for it.
            game.CounterWindows.Add(new CounterWindow
            {
                TeamId = holder.Id,
                AgainstTeamId = team.Id,
                ExpiresAt = now.AddMinutes(_options.CounterWindowMinutes),
            });

            return new PlayResult(GameMutationStatus.Success, PlayEffect.StealFailed);
        }

        holder.Territories.Remove(territory);
        territory.Locked = true;
        team.Territories.Add(territory);

        return new PlayResult(GameMutationStatus.Success, PlayEffect.BoroughStolen);
    }

    private static PlayResult PlayCounter(
        Game game, GameTeam team, string boroughId, bool succeeded, DateTimeOffset now)
    {
        var holder = game.FindTeamHolding(boroughId);
        if (holder is null || holder.Id == team.Id)
        {
            return new PlayResult(GameMutationStatus.InvalidTarget);
        }

        var window = game.CounterWindows.FirstOrDefault(
            w => w.TeamId == team.Id && w.AgainstTeamId == holder.Id && w.ExpiresAt > now);

        if (window is null)
        {
            return new PlayResult(GameMutationStatus.NoCounterWindow);
        }

        var territory = holder.FindTerritory(boroughId)!;
        if (territory.IsLocked)
        {
            return new PlayResult(GameMutationStatus.BoroughLocked);
        }

        if (!succeeded)
        {
            // The window stays open — they can try again with another card until it lapses.
            return new PlayResult(GameMutationStatus.Success);
        }

        holder.Territories.Remove(territory);
        team.Territories.Add(territory);
        game.CounterWindows.Remove(window);

        return new PlayResult(GameMutationStatus.Success, PlayEffect.CounterSucceeded);
    }

    /// <summary>Replaces a resolved borough with one from the same zone, keeping the spread of travel.</summary>
    private void TopUp(GameBoard board, string resolvedBoroughId)
    {
        var zone = BoroughCatalogue.Find(resolvedBoroughId)?.Zone;

        var replacement =
            board.Remaining.FirstOrDefault(id => BoroughCatalogue.Find(id)?.Zone == zone)
            ?? board.Remaining.FirstOrDefault();

        if (replacement is null)
        {
            // Every borough in London is in play or held; the board just runs smaller.
            return;
        }

        board.Remaining.Remove(replacement);
        board.Active.Add(new ActiveBorough { BoroughId = replacement });
    }

    private void RotateHot(GameBoard board, DateTimeOffset now)
    {
        board.HotBoroughId = board.Active.Count > 0
            ? board.Active[Random.Shared.Next(board.Active.Count)].BoroughId
            : null;

        board.HotRotatesAt = now.AddMinutes(_options.HotRotationMinutes);
    }

    /// <summary>
    /// Draws a card the team isn't already holding. If they hold the whole deck — possible when
    /// there are barely more challenges than a hand needs — a repeat is unavoidable.
    /// </summary>
    private static HandCard DrawReplacement(IReadOnlyList<Challenge> deck, TeamHand hand)
    {
        var held = hand.Cards.Select(c => c.ChallengeId).ToHashSet();
        var unheld = deck.Where(challenge => !held.Contains(challenge.Id)).ToList();
        var pool = unheld.Count > 0 ? unheld : deck;

        return ToHandCard(pool[Random.Shared.Next(pool.Count)]);
    }

    private static HandCard ToHandCard(Challenge challenge) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        ChallengeId = challenge.Id,
        Type = challenge.Type,
        Title = challenge.Title,
        Summary = challenge.Summary,
        FurtherDetails = challenge.FurtherDetails,
    };

}
