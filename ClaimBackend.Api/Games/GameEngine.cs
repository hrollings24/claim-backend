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

/// <summary>
/// <paramref name="CounterpartTeamId"/> is the other team involved — the one robbed, or the one
/// handed a counter window. Carried out of the engine because by the time the caller sees the
/// game the move has already been applied, and who it happened to is no longer recoverable.
/// <paramref name="BoroughId"/> is set only by <see cref="GameEngine.ResolveSteal"/>, whose caller
/// has no borough of its own to notify about — a steal's target was chosen back when it was
/// activated, not on the request that resolves it.
/// </summary>
public record PlayResult(
    GameMutationStatus Status,
    PlayEffect Effect = PlayEffect.Nothing,
    string? CounterpartTeamId = null,
    string? BoroughId = null);

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
            || deck.Count(c => c.Type is ChallengeType.Claim) < _options.MinClaimCardsInHand
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
            HotRotatesAt = now.AddMinutes(game.HotRotationMinutes),
        };

        game.Hands.Clear();
        foreach (var team in game.Teams)
        {
            game.Hands.Add(new TeamHand
            {
                TeamId = team.Id,
                Cards = DealHand(deck, _options.HandSize, _options.MinClaimCardsInHand),
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
            RotateHot(board, game.HotRotationMinutes, now);
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

        // A steal is hidden until activated and runs on its own timer, so it can only be resolved
        // through ActivateSteal/ResolveSteal — never played outright the way a claim is.
        if (card.Type is not ChallengeType.Claim)
        {
            return new PlayResult(GameMutationStatus.InvalidTarget);
        }

        var result = PlayClaim(game, board, team, boroughId, succeeded, now);

        // A card is spent whether or not the challenge came off, and the hand is topped back up.
        if (result.Status is GameMutationStatus.Success)
        {
            // Chosen before the played card leaves the hand, so the challenge just attempted
            // isn't handed straight back.
            var replacement = DrawReplacement(deck, hand, card, _options.MinClaimCardsInHand);
            hand.Cards.Remove(card);
            hand.Cards.Add(replacement);
        }

        return result;
    }

    /// <summary>
    /// Commits a steal to a target: the countdown starts here, and the challenge is revealed to
    /// the team from this moment on. There is no way back from this short of resolving it.
    /// </summary>
    public GameMutationStatus ActivateSteal(
        Game game, string teamId, string cardId, string boroughId, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return GameMutationStatus.GameNotRunning;
        }

        var hand = game.FindHand(teamId);
        var card = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (hand is null || card is null)
        {
            return GameMutationStatus.CardNotInHand;
        }

        if (card.Type is not ChallengeType.Steal)
        {
            return GameMutationStatus.InvalidTarget;
        }

        if (card.ActivatedAt is not null)
        {
            return GameMutationStatus.AlreadyActivated;
        }

        var holder = game.FindTeamHolding(boroughId);
        if (holder is null || holder.Id == teamId)
        {
            return GameMutationStatus.InvalidTarget;
        }

        if (holder.FindTerritory(boroughId)!.IsLocked)
        {
            return GameMutationStatus.BoroughLocked;
        }

        card.ActivatedAt = now;
        card.ActivatedBoroughId = boroughId;

        return GameMutationStatus.Success;
    }

    /// <summary>
    /// Reports the outcome of an already-activated steal. Spent either way — a card is spent
    /// whether or not the challenge came off, same as any other — and also if the target stopped
    /// being a legal one while the clock ran (claimed, stolen, or locked by someone else in the
    /// meantime): there is nothing left to do with it but let it go.
    /// </summary>
    public PlayResult ResolveSteal(
        Game game, string teamId, string cardId, bool succeeded, IReadOnlyList<Challenge> deck, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var team = game.Teams.FirstOrDefault(t => t.Id == teamId);
        var hand = game.FindHand(teamId);
        var card = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (team is null || hand is null || card is null)
        {
            return new PlayResult(GameMutationStatus.CardNotInHand);
        }

        if (card.ActivatedAt is null || card.ActivatedBoroughId is not { } boroughId)
        {
            return new PlayResult(GameMutationStatus.NotActivated);
        }

        var result = PlaySteal(game, team, boroughId, succeeded, now);
        if (result.Status is not (GameMutationStatus.Success or GameMutationStatus.InvalidTarget or GameMutationStatus.BoroughLocked))
        {
            return result;
        }

        var replacement = DrawReplacement(deck, hand, card, _options.MinClaimCardsInHand);
        hand.Cards.Remove(card);
        hand.Cards.Add(replacement);

        return result.Status is GameMutationStatus.Success
            ? result with { BoroughId = boroughId }
            : new PlayResult(GameMutationStatus.Success, BoroughId: boroughId);
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
        TopUp(board);

        if (wasHot)
        {
            RotateHot(board, game.HotRotationMinutes, now);
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

            return new PlayResult(GameMutationStatus.Success, PlayEffect.StealFailed, holder.Id);
        }

        holder.Territories.Remove(territory);
        territory.Locked = true;
        team.Territories.Add(territory);

        return new PlayResult(GameMutationStatus.Success, PlayEffect.BoroughStolen, holder.Id);
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

        return new PlayResult(GameMutationStatus.Success, PlayEffect.CounterSucceeded, holder.Id);
    }

    /// <summary>
    /// Replaces a resolved borough with any that is neither on the board nor already held.
    /// Remaining holds exactly those: boroughs move out of it onto the board, and from the board
    /// into a team's territory, so nothing in it is in play. It was shuffled when the game was
    /// dealt, so taking the next one is as good as picking at random.
    /// </summary>
    private static void TopUp(GameBoard board)
    {
        if (board.Remaining.Count == 0)
        {
            // Every borough in London is in play or held; the board just runs smaller.
            return;
        }

        var replacement = board.Remaining[0];
        board.Remaining.RemoveAt(0);
        board.Active.Add(new ActiveBorough { BoroughId = replacement });
    }

    private static void RotateHot(GameBoard board, int rotationMinutes, DateTimeOffset now)
    {
        board.HotBoroughId = board.Active.Count > 0
            ? board.Active[Random.Shared.Next(board.Active.Count)].BoroughId
            : null;

        board.HotRotatesAt = now.AddMinutes(rotationMinutes);
    }

    /// <summary>
    /// Shuffles a hand of the requested size, guaranteeing at least <paramref name="minClaimCards"/>
    /// claim challenges so a team is never left with nothing but steals to play.
    /// </summary>
    private static List<HandCard> DealHand(IReadOnlyList<Challenge> deck, int handSize, int minClaimCards)
    {
        var claims = deck
            .Where(c => c.Type is ChallengeType.Claim)
            .OrderBy(_ => Random.Shared.Next())
            .Take(minClaimCards)
            .ToList();

        var claimIds = claims.Select(c => c.Id).ToHashSet();

        // Shuffle and take the rest, rather than drawing each card independently: independent
        // draws repeat, and with a small deck a five card hand was coming up with two pairs. Two
        // teams can still share a challenge — with fewer cards than the table needs, they have to.
        var rest = deck
            .Where(c => !claimIds.Contains(c.Id))
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Max(0, handSize - claims.Count));

        return claims.Concat(rest).OrderBy(_ => Random.Shared.Next()).Select(ToHandCard).ToList();
    }

    /// <summary>
    /// Draws a card the team isn't already holding. If they hold the whole deck — possible when
    /// there are barely more challenges than a hand needs — a repeat is unavoidable. Forced to a
    /// claim challenge if the one just played was the hand's last claim above the minimum, so the
    /// hand never drops below it.
    /// </summary>
    private static HandCard DrawReplacement(
        IReadOnlyList<Challenge> deck, TeamHand hand, HandCard played, int minClaimCards)
    {
        var remainingClaims = hand.Cards.Count(c => c.Type is ChallengeType.Claim)
            - (played.Type is ChallengeType.Claim ? 1 : 0);
        var needsClaim = remainingClaims < minClaimCards;

        var held = hand.Cards.Select(c => c.ChallengeId).ToHashSet();
        var candidates = needsClaim ? deck.Where(c => c.Type is ChallengeType.Claim).ToList() : deck;
        var unheld = candidates.Where(challenge => !held.Contains(challenge.Id)).ToList();
        var pool = unheld.Count > 0 ? unheld : candidates;

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
        StealMinutes = challenge.Type is ChallengeType.Steal ? challenge.EffectiveStealMinutes : null,
    };

}
