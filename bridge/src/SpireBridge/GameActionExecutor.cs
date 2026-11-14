using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Action execution (API_NOTES.md §8/§9). Every execution re-resolves its
/// target objects from the CURRENT game state on the game thread (via
/// GameThread.Invoke) — indices always refer to a fresh read, never to a
/// cached snapshot. All failures come back as DoResult{ok:false,error:...};
/// nothing here throws to the HTTP layer.
///
/// Note on the error shape: DoResult (the contract DTO) has only ok/error,
/// so exception details are carried inside the error string as
/// "exception: &lt;message&gt;" instead of a separate message field.
/// </summary>
public sealed class GameActionExecutor : IActionExecutor
{
    public DoResult Execute(DoRequest request)
    {
        var r = GameThread.Invoke(() => ExecuteOnGameThread(request));
        if (!r.Success)
            return new DoResult { Ok = false, Error = r.Error ?? "game_thread_unavailable" };
        return r.Value ?? new DoResult { Ok = false, Error = "exception: empty result" };
    }

    // -- parsing -------------------------------------------------------------------

    private sealed class Parsed
    {
        public string Kind = "";
        public int? Index;
        public int? TargetIndex;
    }

    /// <summary>
    /// action_id is the primary key ("play_card:2:0", "end_turn", ...).
    /// Falls back to the kind/index/target fields when action_id is absent.
    /// Explicit index/target fields, when present, override the parsed ones.
    /// </summary>
    private static Parsed? Parse(DoRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ActionId))
        {
            var parts = request.ActionId.Trim().Split(':');
            var parsed = new Parsed { Kind = parts[0] };
            if (parts.Length > 1 && int.TryParse(parts[1], out var idx)) parsed.Index = idx;
            if (parts.Length > 2 && int.TryParse(parts[2], out var tgt)) parsed.TargetIndex = tgt;
            if (request.Index != null) parsed.Index = request.Index;
            if (request.Target != null && int.TryParse(request.Target, out var tt)) parsed.TargetIndex = tt;
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            var kind = request.Kind.Trim();
            if (kind.Contains(':'))
            {
                return Parse(new DoRequest
                {
                    ActionId = kind,
                    Index = request.Index,
                    Target = request.Target,
                });
            }
            var normalized = kind switch
            {
                "map" => "choose_map",
                "event" => "choose_event",
                "rest" => "choose_rest",
                "reward" => "claim_reward",
                _ => kind,
            };
            var parsed = new Parsed { Kind = normalized, Index = request.Index };
            if (request.Target != null && int.TryParse(request.Target, out var tt)) parsed.TargetIndex = tt;
            return parsed;
        }

        return null;
    }

    // -- result helpers ---------------------------------------------------------------

    private static DoResult Ok() => new() { Ok = true, Error = null };

    private static DoResult Err(string error) => new() { Ok = false, Error = error };

    private static DoResult ExceptionResult(Exception ex)
    {
        var message = ex.Message ?? ex.GetType().Name;
        if (message.Length > 200) message = message.Substring(0, 200);
        return new DoResult { Ok = false, Error = $"exception: {message}" };
    }

    // -- execution (game thread only) ---------------------------------------------------

    private DoResult ExecuteOnGameThread(DoRequest request)
    {
        try
        {
            var parsed = Parse(request);
            if (parsed == null) return Err("unsupported_kind");

            switch (parsed.Kind)
            {
                case "play_card": return PlayCard(parsed);
                case "end_turn": return EndTurn();
                case "use_potion": return UsePotion(parsed);
                case "choose_map": return ChooseMap(parsed);
                case "choose_event": return ChooseEvent(parsed);
                case "choose_rest": return ChooseRest(parsed);
                case "claim_reward": return ClaimReward(parsed);
                case "proceed": return Proceed();
                default: return Err("unsupported_kind");
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"[SpireBridge] /do failed: {ex}");
            return ExceptionResult(ex);
        }
    }

    /// <summary>Resolves the live RunManager / RunState / local player, or an error result.</summary>
    private static bool TryGetLive(
        out RunManager runManager,
        out RunState runState,
        out MegaCrit.Sts2.Core.Entities.Players.Player player,
        out DoResult error)
    {
        runManager = null!;
        runState = null!;
        player = null!;
        error = null!;

        var rm = RunManager.Instance;
        if (rm == null) { error = Err("not_in_run"); return false; }
        if (!GameAccess.TryGetRun(out var rs, out var pl) || rs == null || pl == null)
        {
            error = Err("not_in_run");
            return false;
        }
        runManager = rm;
        runState = rs;
        player = pl;
        return true;
    }

    private DoResult PlayCard(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");
        if (!TryGetLive(out _, out _, out var player, out var error)) return error;

        var combatState = GameAccess.GetCombatState();
        if (combatState == null) return Err("not_in_combat");

        IReadOnlyList<CardModel>? cards = null;
        try { cards = player.PlayerCombatState?.Hand?.Cards; } catch { }
        if (cards == null || parsed.Index.Value < 0 || parsed.Index.Value >= cards.Count)
            return Err("card_not_found");
        var card = cards[parsed.Index.Value];

        Creature? target = null;
        bool needsTarget = false;
        try { needsTarget = GameAccess.NeedsEnemyTarget(card.TargetType); } catch { }
        if (needsTarget)
        {
            if (parsed.TargetIndex == null) return Err("target_required");
            var enemies = combatState.Enemies;
            if (enemies == null || parsed.TargetIndex.Value < 0 || parsed.TargetIndex.Value >= enemies.Count)
                return Err("target_not_found");
            target = enemies[parsed.TargetIndex.Value];
            if (target == null) return Err("target_not_found");
        }

        bool played;
        try { played = card.TryManualPlay(target!); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return played ? Ok() : Err("play_failed");
    }

    private DoResult EndTurn()
    {
        if (!TryGetLive(out var rm, out _, out var player, out var error)) return error;

        int turnNumber = 0;
        try
        {
            var pcs = player.PlayerCombatState;
            if (pcs == null) return Err("not_in_combat");
            turnNumber = pcs.TurnNumber;
        }
        catch { return Err("not_in_combat"); }

        // Primary: enqueue EndPlayerTurnAction via the synchronizer (§9.2).
        try
        {
            var sync = rm.ActionQueueSynchronizer;
            if (sync == null) throw new InvalidOperationException("ActionQueueSynchronizer unavailable");
            sync.RequestEnqueue(new EndPlayerTurnAction(player, turnNumber));
            return Ok();
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"[SpireBridge] EndPlayerTurnAction enqueue failed, falling back: {ex.Message}");
        }

        // Fallback: CombatManager.SetReadyToEndTurn(player, true, null).
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null) return Err("not_in_combat");
            cm.SetReadyToEndTurn(player, true, null);
            return Ok();
        }
        catch (Exception ex) { return ExceptionResult(ex); }
    }

    private DoResult UsePotion(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");
        if (!TryGetLive(out var rm, out _, out var player, out var error)) return error;

        bool locked = false;
        try { locked = rm.IsPotionUseLocked; } catch { }
        if (locked) return Err("potion_use_locked");

        var potions = new List<PotionModel>();
        try
        {
            var ps = player.Potions;
            if (ps != null) potions.AddRange(ps);
        }
        catch { }
        if (parsed.Index.Value < 0 || parsed.Index.Value >= potions.Count)
            return Err("potion_not_found");
        var potion = potions[parsed.Index.Value];

        Creature? target = null;
        bool needsTarget = false;
        try { needsTarget = GameAccess.NeedsEnemyTarget(potion.TargetType); } catch { }
        if (needsTarget)
        {
            if (parsed.TargetIndex == null) return Err("target_required");
            var combatState = GameAccess.GetCombatState();
            if (combatState == null) return Err("not_in_combat");
            var enemies = combatState.Enemies;
            if (enemies == null || parsed.TargetIndex.Value < 0 || parsed.TargetIndex.Value >= enemies.Count)
                return Err("target_not_found");
            target = enemies[parsed.TargetIndex.Value];
            if (target == null) return Err("target_not_found");
        }

        try { potion.EnqueueManualUse(target!); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return Ok();
    }

    private DoResult ChooseMap(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");
        if (!TryGetLive(out var rm, out var rs, out var player, out var error)) return error;

        MegaCrit.Sts2.Core.Map.MapPoint? currentPoint = null;
        try { currentPoint = rs.CurrentMapPoint; } catch { }
        var children = GameAccess.SortedMapChildren(currentPoint);
        if (parsed.Index.Value < 0 || parsed.Index.Value >= children.Count)
            return Err("choice_not_found");
        var point = children[parsed.Index.Value];

        // Primary: MoveToMapCoordAction via the synchronizer (§9.4).
        try
        {
            var sync = rm.ActionQueueSynchronizer;
            if (sync == null) throw new InvalidOperationException("ActionQueueSynchronizer unavailable");
            sync.RequestEnqueue(new MoveToMapCoordAction(player, point.coord));
            return Ok();
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"[SpireBridge] MoveToMapCoordAction enqueue failed, falling back: {ex.Message}");
        }

        // Fallback: direct EnterMapCoord (Task is fired, not awaited).
        try
        {
            GameAccess.FireAndForget(rm.EnterMapCoord(point.coord), "EnterMapCoord");
            return Ok();
        }
        catch (Exception ex) { return ExceptionResult(ex); }
    }

    private DoResult ChooseEvent(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");
        if (!TryGetLive(out _, out var rs, out _, out var error)) return error;

        if (rs.CurrentRoom is not EventRoom eventRoom) return Err("not_in_event");
        MegaCrit.Sts2.Core.Models.EventModel? ev = null;
        try { ev = eventRoom.LocalMutableEvent; } catch { }
        var options = ev?.CurrentOptions;
        if (options == null || parsed.Index.Value < 0 || parsed.Index.Value >= options.Count)
            return Err("choice_not_found");
        var option = options[parsed.Index.Value];

        bool locked = false;
        try { locked = option.IsLocked; } catch { }
        if (locked) return Err("option_locked");

        try { GameAccess.FireAndForget(option.Chosen(), "EventOption.Chosen"); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return Ok();
    }

    private DoResult ChooseRest(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");
        if (!TryGetLive(out _, out var rs, out _, out var error)) return error;

        if (rs.CurrentRoom is not RestSiteRoom restRoom) return Err("not_in_rest");
        var options = restRoom.Options;
        if (options == null || parsed.Index.Value < 0 || parsed.Index.Value >= options.Count)
            return Err("choice_not_found");
        var option = options[parsed.Index.Value];

        bool enabled = true;
        try { enabled = option.IsEnabled; } catch { }
        if (!enabled) return Err("option_disabled");

        try { GameAccess.FireAndForget(option.OnSelect(), "RestSiteOption.OnSelect"); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return Ok();
    }

    private DoResult ClaimReward(Parsed parsed)
    {
        if (parsed.Index == null) return Err("missing_index");

        var screenObj = GameAccess.GetCurrentScreenObject();
        var rewardsSet = GameAccess.TryGetRewardsSet(screenObj);
        if (rewardsSet == null) return Err("rewards_unavailable");

        var rewards = rewardsSet.Rewards;
        if (rewards == null || parsed.Index.Value < 0 || parsed.Index.Value >= rewards.Count)
            return Err("choice_not_found");

        try { GameAccess.FireAndForget(rewards[parsed.Index.Value].SelectUnsynchronized(), "Reward.SelectUnsynchronized"); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return Ok();
    }

    private DoResult Proceed()
    {
        var rm = RunManager.Instance;
        if (rm == null) return Err("not_in_run");
        try { rm.Proceed(); }
        catch (Exception ex) { return ExceptionResult(ex); }
        return Ok();
    }
}
