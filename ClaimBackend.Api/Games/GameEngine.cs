using ClaimBackend.Api.Boroughs;
using ClaimBackend.Api.Challenges;
using ClaimBackend.Api.Powers;
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
    StealStarted,
    CurseCast,
    CurseReversed,
    BoroughFortified,
    SwapStarted,
    Swapped,
}

/// <summary>
/// <paramref name="CounterpartTeamId"/> is the other team involved — the one robbed, or the one
/// that survived a steal and drew for it. Carried out of the engine because by the time the
/// caller sees the game the move has already been applied, and who it happened to is no longer
/// recoverable. <paramref name="BoroughId"/> is set only by <see cref="GameEngine.ResolveSteal"/>,
/// whose caller has no borough of its own to notify about — a steal's target was chosen when the
/// card was played, not on the request that resolves it.
/// </summary>
/// <param name="Detail">The curse's title, or what changed hands in a swap.</param>
public record PlayResult(
    GameMutationStatus Status,
    PlayEffect Effect = PlayEffect.Nothing,
    string? CounterpartTeamId = null,
    string? BoroughId = null,
    string? Detail = null);

/// <summary>
/// The rules. Every method mutates the game in place and is called inside the store's version
/// check, so a play that races another team's play is re-read and re-applied rather than
/// overwriting it.
///
/// The hand holds claim challenges only. Completing one claims the borough and draws power
/// cards, of which the team keeps one. Steal is one of those power cards: playing it at another
/// team's borough reveals a steal challenge and starts its clock.
///
/// Timers are deadlines rather than scheduled work: nothing runs between requests, and
/// <see cref="Advance"/> brings the world up to date whenever the game is looked at. That suits
/// a Lambda with no scheduler, and costs nothing while a game sits idle.
/// </summary>
public class GameEngine(IOptions<GamesOptions> options)
{
    private readonly GamesOptions _options = options.Value;

    /// <param name="cards">Every power card that exists; the configuration says how many of each.</param>
    public GameMutationStatus Start(
        Game game,
        IReadOnlyList<Challenge> deck,
        PowerConfiguration powers,
        IReadOnlyList<PowerDefinition> cards,
        DateTimeOffset now)
    {
        // There is nobody to steal from with one team.
        if (game.Teams.Count < 2)
        {
            return GameMutationStatus.NotEnoughTeams;
        }

        // Hands are dealt from claim challenges; steal challenges are what a Steal card reveals,
        // so with Steals in the power deck and none to reveal, one could be drawn and never played.
        var claims = ClaimsIn(deck);
        var inDeck = cards.Where(c => powers.CountOf(c.Id) > 0).ToList();
        var needsSteals = inDeck.Any(c => c.Kind is PowerKind.Steal);
        if (claims.Count < _options.HandSize || (needsSteals && !deck.Any(c => c.Type is ChallengeType.Steal)))
        {
            return GameMutationStatus.NotEnoughChallenges;
        }

        var powerPile = inDeck
            .SelectMany(p => Enumerable.Repeat(p.Id, powers.CountOf(p.Id)))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        // Possible only if every card the configuration names has since been deleted.
        if (powerPile.Count == 0)
        {
            return GameMutationStatus.EmptyPowerDeck;
        }

        var boardSize = BoardSizeFor(game);
        var pool = BoroughCatalogue.All.Select(b => b.Id).OrderBy(_ => Random.Shared.Next()).ToList();
        var active = pool.Take(boardSize).ToList();

        game.Board = new GameBoard
        {
            Active = active.Select(id => new ActiveBorough { BoroughId = id }).ToList(),
            Remaining = pool.Skip(boardSize).ToList(),
            HotBoroughId = active[Random.Shared.Next(active.Count)],
            HotRotatesAt = now.AddMinutes(game.HotRotationMinutes),
        };

        game.Hands.Clear();
        game.Powers.Clear();
        foreach (var team in game.Teams)
        {
            game.Hands.Add(new TeamHand { TeamId = team.Id, Cards = DealHand(claims, _options.HandSize) });
            game.Powers.Add(new TeamPowers { TeamId = team.Id, Cards = [], Offers = [] });
        }

        game.PowerDeck = new PowerDeck { DrawPile = powerPile, DiscardPile = [], Cards = inDeck };
        game.StartedAt = now;
        game.EndsAt = now.AddMinutes(game.DurationMinutes);
        game.Status = GameStatus.InProgress;

        return GameMutationStatus.Success;
    }

    /// <summary>The host's choice if they made one, otherwise a few more than there are teams.</summary>
    public int BoardSizeFor(Game game) =>
        Math.Clamp(game.BoardSize ?? DefaultBoardSize(game.Teams.Count), _options.MinBoardSize, _options.MaxBoardSize);

    public int DefaultBoardSize(int teamCount) =>
        Math.Clamp(teamCount + _options.BoardSizeOverTeams, _options.MinBoardSize, _options.MaxBoardSize);

    /// <summary>
    /// Brings deadlines up to date: steals whose clock ran out fail, the hot borough moves on,
    /// and the game ends when its time runs out. Safe to call repeatedly. Returns whether
    /// anything actually moved, because the caller has to persist it if so — rotating the hot
    /// borough and drawing power cards are random, and re-rolling them on every read would land
    /// somewhere new each time anyone looked.
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
            // A steal still running at the end simply never happened.
            game.Status = GameStatus.Finished;
            return true;
        }

        var changed = TimeOutSteals(game, now) | ExpireCurses(game, now);

        // The hot borough must always be one of the active — unclaimed by definition — so it also
        // moves the moment the borough it was sitting on is claimed out from under it. With no hot
        // borough at all, because nothing on the board was eligible, it waits for somewhere new
        // to go rather than re-rolling to nothing, and reporting a change, on every read.
        var hotStillActive = board.HotBoroughId is { } hot
            && board.Active.Any(a => a.BoroughId == hot);

        var rotate = hotStillActive
            ? now >= board.HotRotatesAt
            : board.HotBoroughId is not null || HotCandidates(board).Count > 0;

        if (rotate)
        {
            RotateHot(board, game.HotRotationMinutes, now);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Plays a claim challenge from the hand at an active borough. A card is spent whether or not
    /// the challenge came off, and the hand is topped back up; only a success claims the borough
    /// and draws power cards.
    /// </summary>
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

        var team = game.FindTeam(teamId);
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

        var active = board.Active.FirstOrDefault(a => a.BoroughId == boroughId);
        if (active is null)
        {
            return new PlayResult(GameMutationStatus.InvalidTarget);
        }

        var effect = succeeded ? Claim(game, board, team, card, active, now) : PlayEffect.Nothing;

        // Chosen before the played card leaves the hand, so the challenge just attempted isn't
        // handed straight back.
        var replacement = DrawReplacement(ClaimsIn(deck), hand);
        hand.Cards.Remove(card);
        if (replacement is not null)
        {
            hand.Cards.Add(replacement);
        }

        return new PlayResult(GameMutationStatus.Success, effect);
    }

    /// <summary>
    /// Takes some or none of an offer's cards into the team's power cards; the rest are
    /// discarded and the offer is gone. Keeping none is how a team at the limit turns one down.
    /// </summary>
    public GameMutationStatus KeepPowers(
        Game game, string teamId, string offerId, IReadOnlyCollection<string> cardIds)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return GameMutationStatus.GameNotRunning;
        }

        if (game.FindPowers(teamId) is not { } powers)
        {
            return GameMutationStatus.NotOnATeam;
        }

        var offer = powers.Offers.FirstOrDefault(o => o.Id == offerId);
        if (offer is null)
        {
            return GameMutationStatus.OfferNotFound;
        }

        var kept = cardIds.Distinct().Select(id => offer.Cards.FirstOrDefault(c => c.Id == id)).ToList();
        if (kept.Any(c => c is null))
        {
            return GameMutationStatus.NotInOffer;
        }

        if (kept.Count > offer.Keep)
        {
            return GameMutationStatus.TooManyKept;
        }

        if (powers.UnplayedCount + kept.Count > _options.MaxPowerCards)
        {
            return GameMutationStatus.PowerHandFull;
        }

        powers.Cards.AddRange(kept.OfType<PowerCard>());
        powers.Offers.Remove(offer);
        Discard(game, offer.Cards.Except(kept.OfType<PowerCard>()));

        return GameMutationStatus.Success;
    }

    /// <summary>Throws away an unplayed power card, to make room under the limit.</summary>
    public GameMutationStatus DiscardPower(Game game, string teamId, string cardId)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return GameMutationStatus.GameNotRunning;
        }

        var powers = game.FindPowers(teamId);
        var card = powers?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (powers is null || card is null)
        {
            return GameMutationStatus.CardNotInHand;
        }

        // A played card is the record of what happened, not something still held.
        if (card.IsPlayed)
        {
            return GameMutationStatus.AlreadyPlayed;
        }

        powers.Cards.Remove(card);
        Discard(game, [card]);
        return GameMutationStatus.Success;
    }

    /// <summary>
    /// Plays a power card. A curse needs a target team and lands on them at once; a boost is only
    /// marked as played. A steal needs a borough another team holds: the target is committed
    /// first, then a steal challenge is revealed and its countdown starts. Fortify needs one of the
    /// team's own boroughs; Swap needs another team, whose cards are then revealed. Curse Reverse
    /// can't be played on its own — it's used from the curse it answers. Nothing played can be
    /// taken back.
    /// </summary>
    public PlayResult PlayPower(
        Game game,
        string teamId,
        string cardId,
        string? boroughId,
        string? targetTeamId,
        IReadOnlyList<Challenge> deck,
        DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var card = game.FindPowers(teamId)?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null)
        {
            return new PlayResult(GameMutationStatus.CardNotInHand);
        }

        if (card.IsPlayed)
        {
            return new PlayResult(GameMutationStatus.AlreadyPlayed);
        }

        switch (card.Kind)
        {
            case PowerKind.Reverse:
                return new PlayResult(GameMutationStatus.RespondOnly);

            case PowerKind.Fortify:
            {
                var territory = boroughId is null ? null : game.FindTeam(teamId)?.FindTerritory(boroughId);
                if (territory is null)
                {
                    return new PlayResult(GameMutationStatus.InvalidTarget);
                }

                if (territory.Fortified)
                {
                    return new PlayResult(GameMutationStatus.AlreadyFortified);
                }

                // Otherwise Fortify would cancel a steal outright, which nothing else can.
                if (RunningSteals(game).Any(steal => steal.BoroughId == boroughId))
                {
                    return new PlayResult(GameMutationStatus.UnderAttack);
                }

                territory.Fortified = true;
                card.PlayedAt = now;
                card.PlayedOn = BoroughCatalogue.NameOf(boroughId!);
                return new PlayResult(GameMutationStatus.Success, PlayEffect.BoroughFortified, BoroughId: boroughId);
            }

            case PowerKind.Swap:
            {
                var target = targetTeamId is null ? null : game.FindTeam(targetTeamId);
                if (target is null || target.Id == teamId)
                {
                    return new PlayResult(GameMutationStatus.InvalidTarget);
                }

                if (game.FindPowers(target.Id) is not { UnplayedCount: > 0 })
                {
                    return new PlayResult(GameMutationStatus.NothingToSwap);
                }

                card.Swap = new SwapAttempt { TargetTeamId = target.Id };
                card.PlayedAt = now;
                card.PlayedOn = target.Name;
                return new PlayResult(GameMutationStatus.Success, PlayEffect.SwapStarted, target.Id);
            }

            case PowerKind.Curse:
            {
                var target = targetTeamId is null ? null : game.FindTeam(targetTeamId);
                if (target is null || target.Id == teamId)
                {
                    return new PlayResult(GameMutationStatus.InvalidTarget);
                }

                game.Curses.Add(new ActiveCurse
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Title = card.Title,
                    Description = card.Description,
                    FromTeamId = teamId,
                    TargetTeamId = target.Id,
                    StartedAt = now,
                    DurationMinutes = card.DurationMinutes,
                });

                card.PlayedAt = now;
                card.PlayedOn = target.Name;
                return new PlayResult(GameMutationStatus.Success, PlayEffect.CurseCast, target.Id, Detail: card.Title);
            }

            case PowerKind.Steal:
            {
                var holder = boroughId is null ? null : game.FindTeamHolding(boroughId);
                if (holder is null || holder.Id == teamId)
                {
                    return new PlayResult(GameMutationStatus.InvalidTarget);
                }

                if (holder.FindTerritory(boroughId!)!.Fortified)
                {
                    return new PlayResult(GameMutationStatus.BoroughLocked);
                }

                var steals = deck.Where(c => c.Type is ChallengeType.Steal).ToList();
                if (steals.Count == 0)
                {
                    return new PlayResult(GameMutationStatus.NotEnoughChallenges);
                }

                var challenge = steals[Random.Shared.Next(steals.Count)];
                card.Steal = new StealAttempt
                {
                    BoroughId = boroughId!,
                    ChallengeId = challenge.Id,
                    Title = challenge.Title,
                    Summary = challenge.Summary,
                    FurtherDetails = challenge.FurtherDetails,
                    ExpiresAt = now.AddMinutes(challenge.EffectiveStealMinutes),
                    PowerDraws = challenge.EffectivePowerDraws,
                };

                card.PlayedAt = now;
                card.PlayedOn = BoroughCatalogue.NameOf(boroughId!);
                return new PlayResult(GameMutationStatus.Success, PlayEffect.StealStarted, holder.Id, boroughId);
            }

            default:
                card.PlayedAt = now;
                return new PlayResult(GameMutationStatus.Success);
        }
    }

    /// <summary>
    /// Uses a Curse Reverse on an active curse aimed at the team: it ends, and is cast afresh — with
    /// its full time — on whoever sent it. A curse that is itself a reversal can't be sent back.
    /// </summary>
    public PlayResult RespondToCurse(Game game, string teamId, string curseId, string cardId, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var curse = game.Curses.FirstOrDefault(
            c => c.Id == curseId && c.TargetTeamId == teamId && c.Status is CurseStatus.Active);
        if (curse is null)
        {
            return new PlayResult(GameMutationStatus.CurseNotFound);
        }

        var card = game.FindPowers(teamId)?.Cards.FirstOrDefault(c => c.Id == cardId && !c.IsPlayed);
        if (card is not { Kind: PowerKind.Reverse })
        {
            return new PlayResult(GameMutationStatus.NotAResponse);
        }

        if (curse.IsReversal)
        {
            return new PlayResult(GameMutationStatus.CannotReverse);
        }

        curse.Status = CurseStatus.Reversed;
        curse.ResolvedAt = now;
        card.PlayedAt = now;
        card.PlayedOn = curse.Title;

        game.Curses.Add(new ActiveCurse
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = curse.Title,
            Description = curse.Description,
            FromTeamId = teamId,
            TargetTeamId = curse.FromTeamId,
            StartedAt = now,
            DurationMinutes = curse.DurationMinutes,
            IsReversal = true,
        });

        return new PlayResult(GameMutationStatus.Success, PlayEffect.CurseReversed, curse.FromTeamId, Detail: curse.Title);
    }

    /// <summary>
    /// The cursed team marking a curse done. Only for one with no time on it — a timed curse
    /// lifts on its own, and clearing it early would just be cheating.
    /// </summary>
    public GameMutationStatus ClearCurse(Game game, string teamId, string curseId, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return GameMutationStatus.GameNotRunning;
        }

        var curse = game.Curses.FirstOrDefault(
            c => c.Id == curseId && c.TargetTeamId == teamId && c.Status is CurseStatus.Active);
        if (curse is null)
        {
            return GameMutationStatus.CurseNotFound;
        }

        if (curse.DurationMinutes is not null)
        {
            return GameMutationStatus.CurseIsTimed;
        }

        curse.Status = CurseStatus.Cleared;
        curse.ResolvedAt = now;
        return GameMutationStatus.Success;
    }

    /// <summary>
    /// Finishes a played Swap: takes one of the other team's unplayed power cards and gives them
    /// one of the swapping team's. Either side may be left out only when that team has nothing
    /// left to swap — so a team that played its last card on the Swap can still take one.
    /// </summary>
    public PlayResult CompleteSwap(
        Game game, string teamId, string swapCardId, string? takeCardId, string? giveCardId, DateTimeOffset now)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var mine = game.FindPowers(teamId);
        var swap = mine?.Cards.FirstOrDefault(c => c.Id == swapCardId)?.Swap;
        if (mine is null || swap is not { CompletedAt: null })
        {
            return new PlayResult(GameMutationStatus.SwapNotFound);
        }

        if (game.FindPowers(swap.TargetTeamId) is not { } theirs)
        {
            return new PlayResult(GameMutationStatus.SwapNotFound);
        }

        var take = theirs.Cards.FirstOrDefault(c => c.Id == takeCardId && !c.IsPlayed);
        if (take is null && (takeCardId is not null || theirs.UnplayedCount > 0))
        {
            return new PlayResult(GameMutationStatus.NotInTheirHand);
        }

        var give = mine.Cards.FirstOrDefault(c => c.Id == giveCardId && !c.IsPlayed);
        if (give is null && (giveCardId is not null || mine.UnplayedCount > 0))
        {
            return new PlayResult(GameMutationStatus.CardNotInHand);
        }

        if (take is not null)
        {
            theirs.Cards.Remove(take);
            mine.Cards.Add(take);
        }

        if (give is not null)
        {
            mine.Cards.Remove(give);
            theirs.Cards.Add(give);
        }

        swap.CompletedAt = now;

        var targetName = game.FindTeam(swap.TargetTeamId)?.Name ?? "another team";
        var detail = $"took {take?.Title ?? "nothing"}, gave {give?.Title ?? "nothing"}";
        var swapCard = mine.Cards.First(c => c.Id == swapCardId);
        swapCard.PlayedOn = $"{targetName}: {detail}";

        return new PlayResult(GameMutationStatus.Success, PlayEffect.Swapped, swap.TargetTeamId, Detail: detail);
    }

    /// <summary>
    /// Reports how a running steal went. It has to arrive before the clock runs out — after that
    /// <see cref="Advance"/> has already failed it. A success takes the borough from whoever
    /// holds it now; a failure costs the card, and the team that was targeted draws power cards
    /// for surviving it.
    /// </summary>
    public PlayResult ResolveSteal(Game game, string teamId, string cardId, bool succeeded)
    {
        if (game.Status is not GameStatus.InProgress)
        {
            return new PlayResult(GameMutationStatus.GameNotRunning);
        }

        var team = game.FindTeam(teamId);
        var card = game.FindPowers(teamId)?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (team is null || card is null)
        {
            return new PlayResult(GameMutationStatus.CardNotInHand);
        }

        if (card.Kind is not PowerKind.Steal)
        {
            return new PlayResult(GameMutationStatus.InvalidTarget);
        }

        if (card.Steal is not { } steal)
        {
            return new PlayResult(GameMutationStatus.NotPlayed);
        }

        if (steal.Outcome is not null)
        {
            return new PlayResult(GameMutationStatus.StealSettled);
        }

        var holder = game.FindTeamHolding(steal.BoroughId);

        if (!succeeded)
        {
            steal.Outcome = StealOutcome.Failed;
            var defender = OfferToDefender(game, teamId, steal);

            return new PlayResult(
                GameMutationStatus.Success,
                defender is null ? PlayEffect.Nothing : PlayEffect.StealFailed,
                defender,
                steal.BoroughId);
        }

        steal.Outcome = StealOutcome.Succeeded;

        // Someone else may have stolen it while the clock ran — the steal is from whoever holds
        // it now. If that is already the stealing team, there is nothing left to take.
        if (holder is null || holder.Id == teamId)
        {
            return new PlayResult(GameMutationStatus.Success, BoroughId: steal.BoroughId);
        }

        var territory = holder.FindTerritory(steal.BoroughId)!;
        holder.Territories.Remove(territory);
        team.Territories.Add(territory);

        return new PlayResult(GameMutationStatus.Success, PlayEffect.BoroughStolen, holder.Id, steal.BoroughId);
    }

    private PlayEffect Claim(
        Game game, GameBoard board, GameTeam team, HandCard card, ActiveBorough active, DateTimeOffset now)
    {
        var wasHot = board.HotBoroughId == active.BoroughId;

        // The hot bonus is banked now and stays with the team even if the borough is later
        // stolen. Hot also doubles the power cards drawn and how many may be kept.
        if (wasHot)
        {
            team.BonusPoints++;
        }

        var multiplier = wasHot ? _options.HotClaimPowerMultiplier : 1;
        AddOffer(
            game,
            team.Id,
            $"Claimed {BoroughCatalogue.NameOf(active.BoroughId)}",
            card.PowerDraws * multiplier,
            multiplier);

        team.Territories.Add(new Territory { BoroughId = active.BoroughId });
        board.Active.Remove(active);
        TopUp(board);

        if (wasHot)
        {
            RotateHot(board, game.HotRotationMinutes, now);
        }

        return wasHot ? PlayEffect.HotBoroughClaimed : PlayEffect.BoroughClaimed;
    }

    private static IEnumerable<StealAttempt> RunningSteals(Game game) =>
        game.Powers.SelectMany(p => p.Cards).Select(c => c.Steal).OfType<StealAttempt>().Where(s => s.Outcome is null);

    /// <summary>Lifts every timed curse whose time is up.</summary>
    private static bool ExpireCurses(Game game, DateTimeOffset now)
    {
        var changed = false;

        foreach (var curse in game.Curses)
        {
            if (curse.Status is CurseStatus.Active && curse.EndsAt is { } endsAt && now >= endsAt)
            {
                curse.Status = CurseStatus.Expired;
                curse.ResolvedAt = endsAt;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Fails every running steal whose clock has run out.</summary>
    private static bool TimeOutSteals(Game game, DateTimeOffset now)
    {
        var changed = false;

        foreach (var powers in game.Powers)
        {
            foreach (var steal in powers.Cards.Select(c => c.Steal).OfType<StealAttempt>())
            {
                if (steal.Outcome is null && now >= steal.ExpiresAt)
                {
                    steal.Outcome = StealOutcome.TimedOut;
                    OfferToDefender(game, powers.TeamId, steal);
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Gives the team holding a failed steal's target its draw for surviving it. Returns that
    /// team, or null if nobody else holds the borough by now.
    /// </summary>
    private static string? OfferToDefender(Game game, string stealingTeamId, StealAttempt steal)
    {
        var defender = game.FindTeamHolding(steal.BoroughId);
        if (defender is null || defender.Id == stealingTeamId)
        {
            return null;
        }

        AddOffer(
            game,
            defender.Id,
            $"Survived a steal on {BoroughCatalogue.NameOf(steal.BoroughId)}",
            steal.PowerDraws,
            keep: 1);

        return defender.Id;
    }

    private static void AddOffer(Game game, string teamId, string reason, int draws, int keep)
    {
        if (game.FindPowers(teamId) is not { } powers || game.PowerDeck is not { } deck)
        {
            return;
        }

        var cards = Draw(deck, draws);

        // Every card is either held or waiting in an offer — there is nothing left to give.
        if (cards.Count == 0)
        {
            return;
        }

        powers.Offers.Add(new PowerOffer
        {
            Id = Guid.NewGuid().ToString("n"),
            Reason = reason,
            Cards = cards,
            Keep = Math.Min(keep, cards.Count),
        });
    }

    /// <summary>
    /// Takes cards off the top, shuffling the discard pile back in when the draw pile runs out.
    /// Comes up short only when both are empty.
    /// </summary>
    private static List<PowerCard> Draw(PowerDeck deck, int count)
    {
        var drawn = new List<PowerCard>();

        while (drawn.Count < count)
        {
            if (deck.DrawPile.Count == 0)
            {
                if (deck.DiscardPile.Count == 0)
                {
                    break;
                }

                deck.DrawPile.AddRange(deck.DiscardPile.OrderBy(_ => Random.Shared.Next()));
                deck.DiscardPile.Clear();
            }

            var id = deck.DrawPile[0];
            deck.DrawPile.RemoveAt(0);

            // Only a game dealt before cards were copied can meet an id it doesn't know; that card
            // just leaves the deck.
            if (deck.Find(id) is { } power)
            {
                drawn.Add(ToPowerCard(power));
            }
        }

        return drawn;
    }

    private static void Discard(Game game, IEnumerable<PowerCard> cards) =>
        game.PowerDeck?.DiscardPile.AddRange(cards.Select(c => c.PowerId));

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

    /// <summary>
    /// Moves the hot borough to an active one that has never been hot. If every borough on the
    /// board already has been, there is no hot borough until a fresh one is drawn.
    /// </summary>
    private static void RotateHot(GameBoard board, int rotationMinutes, DateTimeOffset now)
    {
        if (board.HotBoroughId is { } outgoing && !board.FormerHotBoroughIds.Contains(outgoing))
        {
            board.FormerHotBoroughIds.Add(outgoing);
        }

        var candidates = HotCandidates(board);
        board.HotBoroughId = candidates.Count > 0
            ? candidates[Random.Shared.Next(candidates.Count)]
            : null;

        board.HotRotatesAt = now.AddMinutes(rotationMinutes);
    }

    /// <summary>Active boroughs that have never been hot. The current one counts as having been.</summary>
    private static List<string> HotCandidates(GameBoard board) =>
        board.Active
            .Select(a => a.BoroughId)
            .Where(id => id != board.HotBoroughId && !board.FormerHotBoroughIds.Contains(id))
            .ToList();

    private static List<Challenge> ClaimsIn(IReadOnlyList<Challenge> deck) =>
        deck.Where(c => c.Type is ChallengeType.Claim).ToList();

    /// <summary>
    /// Shuffles and takes a hand, rather than drawing each card independently: independent draws
    /// repeat, and with a small deck a five card hand was coming up with two pairs. Two teams can
    /// still share a challenge — with fewer cards than the table needs, they have to.
    /// </summary>
    private static List<HandCard> DealHand(IReadOnlyList<Challenge> claims, int handSize) =>
        claims.OrderBy(_ => Random.Shared.Next()).Take(handSize).Select(ToHandCard).ToList();

    /// <summary>
    /// Draws a claim challenge the team isn't already holding. If they hold every one — possible
    /// when there are barely more claims than a hand needs — a repeat is unavoidable. Null only
    /// if every claim challenge has been deleted since the game was dealt.
    /// </summary>
    private static HandCard? DrawReplacement(IReadOnlyList<Challenge> claims, TeamHand hand)
    {
        var held = hand.Cards.Select(c => c.ChallengeId).ToHashSet();
        var unheld = claims.Where(challenge => !held.Contains(challenge.Id)).ToList();
        var pool = unheld.Count > 0 ? unheld : claims;

        return pool.Count > 0 ? ToHandCard(pool[Random.Shared.Next(pool.Count)]) : null;
    }

    private static HandCard ToHandCard(Challenge challenge) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        ChallengeId = challenge.Id,
        Title = challenge.Title,
        Summary = challenge.Summary,
        FurtherDetails = challenge.FurtherDetails,
        PowerDraws = challenge.EffectivePowerDraws,
    };

    private static PowerCard ToPowerCard(PowerDefinition power) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        PowerId = power.Id,
        Kind = power.Kind,
        Title = power.Title,
        Description = power.Description,
        DurationMinutes = power.Kind is PowerKind.Curse ? power.DurationMinutes : null,
    };
}
