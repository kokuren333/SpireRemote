using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;

namespace SpireBridge;

/// <summary>
/// Real game-state reader (API_NOTES.md §2–§7). All game API access happens
/// on the game thread via GameThread.Invoke; if marshaling is unavailable
/// the provider returns an honest fallback (screen="unknown", empty lists)
/// without touching game APIs on the wrong thread.
/// </summary>
public sealed class GameStateProvider : IStateProvider
{
    public StateSnapshot GetState()
    {
        var r = GameThread.Invoke(() => Build().Snapshot);
        return r.Success && r.Value != null ? r.Value : Fallback();
    }

    public IReadOnlyList<ActionInfo> GetActions()
    {
        var r = GameThread.Invoke(() => Build().Actions);
        return r.Success && r.Value != null ? r.Value : Array.Empty<ActionInfo>();
    }

    private static StateSnapshot Fallback() => new()
    {
        Screen = "unknown",
        Run = null,
        Player = null,
        Hand = new List<CardInfo>(),
        Enemies = new List<EnemyInfo>(),
        Potions = new List<PotionInfo>(),
        Choices = new List<ChoiceInfo>(),
    };

    private sealed class Built
    {
        public StateSnapshot Snapshot { get; init; } = Fallback();
        public List<ActionInfo> Actions { get; init; } = new();
    }

    // ---------------------------------------------------------------------------
    // Everything below runs on the game thread only.
    // ---------------------------------------------------------------------------

    private Built Build()
    {
        var snap = Fallback();
        var actions = new List<ActionInfo>();
        var built = new Built { Snapshot = snap, Actions = actions };

        try
        {
            if (!GameAccess.TryGetRun(out var runState, out var player) || runState == null)
            {
                snap.Screen = "main_menu";
                return built;
            }
            var rs = runState;

            snap.Run = new RunInfo
            {
                Act = GameAccess.SafeInt(() => rs.CurrentActIndex + 1),
                Floor = GameAccess.SafeInt(() => rs.TotalFloor),
                Gold = player == null ? null : GameAccess.SafeInt(() => player.Gold),
            };

            var combatState = GameAccess.GetCombatState();
            bool inCombat = combatState != null;

            // ---- player ------------------------------------------------------
            Creature? playerCreature = null;
            if (player != null)
            {
                try { playerCreature = player.Creature; } catch { /* stays null */ }
                var pi = new PlayerInfo
                {
                    Hp = playerCreature == null ? null : GameAccess.SafeInt(() => playerCreature!.CurrentHp),
                    MaxHp = playerCreature == null ? null : GameAccess.SafeInt(() => playerCreature!.MaxHp),
                    Block = playerCreature == null ? null : GameAccess.SafeInt(() => playerCreature!.Block),
                };
                if (inCombat)
                {
                    PlayerCombatState? pcsForEnergy = null;
                    try { pcsForEnergy = player.PlayerCombatState; } catch { }
                    if (pcsForEnergy != null)
                    {
                        pi.Energy = GameAccess.SafeInt(() => pcsForEnergy.Energy);
                        pi.MaxEnergy = GameAccess.SafeInt(() => pcsForEnergy.MaxEnergy);
                    }
                }
                snap.Player = pi;
            }

            // ---- screen determination (§7) ------------------------------------
            AbstractRoom? room = null;
            try { room = rs.CurrentRoom; } catch { }
            string? roomScreen = room switch
            {
                CombatRoom => "combat",
                EventRoom => "event",
                RestSiteRoom => "rest",
                MerchantRoom => "shop",
                TreasureRoom => "treasure",
                MapRoom => "map",
                _ => null,
            };

            string screen = roomScreen ?? "unknown";
            if (inCombat) screen = "combat";

            object? screenObj = GameAccess.GetCurrentScreenObject();
            string? overlayScreen = null;
            if (screenObj != null)
            {
                var typeName = screenObj.GetType().Name;
                if (typeName == "NRewardsScreen") overlayScreen = "rewards";
                else if (typeName == "NMapScreen") overlayScreen = "map";
                else if (typeName.Contains("Selection") || typeName.Contains("SelectScreen"))
                    overlayScreen = "card_selection";
            }
            // Overlay screens (rewards / map / card selection) win over the
            // room/combat baseline — e.g. rewards appear while CurrentRoom is
            // still the finished CombatRoom.
            if (overlayScreen != null) screen = overlayScreen;
            else if (screen == "unknown")
            {
                try { if (rs.CurrentMapPoint != null) screen = "map"; } catch { }
            }
            snap.Screen = screen;

            // ---- hand & enemies (combat only) ---------------------------------
            var handCards = new List<CardModel>();
            var enemies = new List<Creature>();
            if (inCombat && combatState != null)
            {
                if (player != null)
                {
                    try
                    {
                        var cards = player.PlayerCombatState?.Hand?.Cards;
                        if (cards != null) handCards.AddRange(cards);
                    }
                    catch { }
                }
                try
                {
                    var es = combatState.Enemies;
                    if (es != null) enemies.AddRange(es);
                }
                catch { }

                for (int i = 0; i < handCards.Count; i++)
                {
                    var card = handCards[i];
                    snap.Hand.Add(new CardInfo
                    {
                        Index = i,
                        Id = GameAccess.SafeString(() => card.Id.Entry)
                             ?? GameAccess.SafeString(() => card.Id.ToString()),
                        Name = GameAccess.SafeString(() => card.Title),
                        Cost = ResolveCost(card),
                        Description = ResolveDescription(card),
                        Playable = GameAccess.SafeBool(() => card.CanPlay()),
                    });
                }
                for (int i = 0; i < enemies.Count; i++)
                {
                    var enemy = enemies[i];
                    snap.Enemies.Add(new EnemyInfo
                    {
                        Index = i,
                        Name = GameAccess.SafeString(() => enemy.Name),
                        Hp = GameAccess.SafeInt(() => enemy.CurrentHp),
                        MaxHp = GameAccess.SafeInt(() => enemy.MaxHp),
                        Block = GameAccess.SafeInt(() => enemy.Block),
                        Intent = ResolveIntent(enemy),
                    });
                }
            }

            // ---- potions --------------------------------------------------------
            var potions = new List<PotionModel>();
            if (player != null)
            {
                try
                {
                    var ps = player.Potions;
                    if (ps != null) potions.AddRange(ps);
                }
                catch { }
            }
            for (int i = 0; i < potions.Count; i++)
            {
                var potion = potions[i];
                snap.Potions.Add(new PotionInfo
                {
                    Index = i,
                    Id = GameAccess.SafeString(() => potion.Id.Entry)
                         ?? GameAccess.SafeString(() => potion.Id.ToString()),
                    Name = SafePotionName(potion),
                });
            }

            // ---- choices & actions per screen -----------------------------------
            if (inCombat)
            {
                for (int i = 0; i < handCards.Count; i++)
                {
                    var card = handCards[i];
                    bool needsTarget = false;
                    try { needsTarget = GameAccess.NeedsEnemyTarget(card.TargetType); } catch { }
                    string? cardName = i < snap.Hand.Count ? snap.Hand[i].Name : null;
                    if (needsTarget && enemies.Count > 0)
                    {
                        for (int e = 0; e < enemies.Count; e++)
                        {
                            string? enemyName = e < snap.Enemies.Count ? snap.Enemies[e].Name : null;
                            actions.Add(new ActionInfo
                            {
                                ActionId = $"play_card:{i}:{e}",
                                Kind = "play_card",
                                Label = enemyName == null ? cardName : $"{cardName} -> {enemyName}",
                                Target = enemyName,
                            });
                        }
                    }
                    else
                    {
                        actions.Add(new ActionInfo
                        {
                            ActionId = $"play_card:{i}",
                            Kind = "play_card",
                            Label = cardName,
                            Target = null,
                        });
                    }
                }
                actions.Add(new ActionInfo
                {
                    ActionId = "end_turn",
                    Kind = "end_turn",
                    Label = "End Turn",
                    Target = null,
                });
            }

            // Potion actions are offered whenever potions are held during a run.
            for (int i = 0; i < potions.Count; i++)
            {
                var potion = potions[i];
                bool needsTarget = false;
                try { needsTarget = GameAccess.NeedsEnemyTarget(potion.TargetType); } catch { }
                string? potionName = snap.Potions[i].Name;
                if (needsTarget && inCombat && enemies.Count > 0)
                {
                    for (int e = 0; e < enemies.Count; e++)
                    {
                        string? enemyName = e < snap.Enemies.Count ? snap.Enemies[e].Name : null;
                        actions.Add(new ActionInfo
                        {
                            ActionId = $"use_potion:{i}:{e}",
                            Kind = "use_potion",
                            Label = enemyName == null ? potionName : $"{potionName} -> {enemyName}",
                            Target = enemyName,
                        });
                    }
                }
                else
                {
                    actions.Add(new ActionInfo
                    {
                        ActionId = $"use_potion:{i}",
                        Kind = "use_potion",
                        Label = potionName,
                        Target = null,
                    });
                }
            }

            switch (screen)
            {
                case "map":
                {
                    MegaCrit.Sts2.Core.Map.MapPoint? currentPoint = null;
                    try { currentPoint = rs.CurrentMapPoint; } catch { }
                    var children = GameAccess.SortedMapChildren(currentPoint);
                    for (int i = 0; i < children.Count; i++)
                    {
                        var point = children[i];
                        string label = GameAccess.SafeString(
                            () => $"{point.PointType} ({point.coord.col},{point.coord.row})")
                            ?? GameAccess.SafeString(() => point.PointType.ToString())
                            ?? "map";
                        snap.Choices.Add(new ChoiceInfo { Index = i, Label = label, Kind = "map" });
                        actions.Add(new ActionInfo
                        {
                            ActionId = $"choose_map:{i}",
                            Kind = "choose_map",
                            Label = label,
                            Target = null,
                        });
                    }
                    break;
                }

                case "event":
                {
                    if (room is EventRoom eventRoom)
                    {
                        MegaCrit.Sts2.Core.Models.EventModel? ev = null;
                        try { ev = eventRoom.LocalMutableEvent; } catch { }
                        IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption>? options = null;
                        try { options = ev?.CurrentOptions; } catch { }
                        if (options != null)
                        {
                            for (int i = 0; i < options.Count; i++)
                            {
                                var opt = options[i];
                                string? label = null;
                                try { label = opt.Title?.GetFormattedText(); } catch { }
                                bool locked = false;
                                try { locked = opt.IsLocked; } catch { }
                                if (locked) label = (label ?? "option") + " [locked]";
                                snap.Choices.Add(new ChoiceInfo { Index = i, Label = label, Kind = "event" });
                                actions.Add(new ActionInfo
                                {
                                    ActionId = $"choose_event:{i}",
                                    Kind = "choose_event",
                                    Label = label,
                                    Target = null,
                                });
                            }
                        }
                    }
                    break;
                }

                case "rest":
                {
                    if (room is RestSiteRoom restRoom)
                    {
                        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption>? options = null;
                        try { options = restRoom.Options; } catch { }
                        if (options != null)
                        {
                            for (int i = 0; i < options.Count; i++)
                            {
                                var opt = options[i];
                                string? label = null;
                                try { label = opt.Title?.GetFormattedText(); } catch { }
                                bool enabled = true;
                                try { enabled = opt.IsEnabled; } catch { }
                                if (!enabled) label = (label ?? "option") + " [disabled]";
                                snap.Choices.Add(new ChoiceInfo { Index = i, Label = label, Kind = "rest" });
                                actions.Add(new ActionInfo
                                {
                                    ActionId = $"choose_rest:{i}",
                                    Kind = "choose_rest",
                                    Label = label,
                                    Target = null,
                                });
                            }
                        }
                    }
                    break;
                }

                case "rewards":
                {
                    var rewardsSet = GameAccess.TryGetRewardsSet(screenObj);
                    if (rewardsSet != null)
                    {
                        var rewards = new List<Reward>();
                        try { if (rewardsSet.Rewards != null) rewards.AddRange(rewardsSet.Rewards); }
                        catch { }
                        for (int i = 0; i < rewards.Count; i++)
                        {
                            var reward = rewards[i];
                            string? label = null;
                            try { label = reward.Description?.GetFormattedText(); } catch { }
                            snap.Choices.Add(new ChoiceInfo { Index = i, Label = label, Kind = "reward" });
                            actions.Add(new ActionInfo
                            {
                                ActionId = $"claim_reward:{i}",
                                Kind = "claim_reward",
                                Label = label,
                                Target = null,
                            });
                        }
                    }
                    // Proceed is meaningful on the rewards screen regardless of
                    // whether the RewardsSet itself could be read.
                    actions.Add(new ActionInfo
                    {
                        ActionId = "proceed",
                        Kind = "proceed",
                        Label = "Proceed",
                        Target = null,
                    });
                    break;
                }

                // "shop" / "treasure" / "card_selection": v1 intentionally
                // exposes no choices (see report). Snapshot fields above
                // (run/player/potions/screen) are still real.
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let a state-read failure escape to the HTTP layer as a
            // crash; return whatever was honestly collected so far.
            BridgeLog.Warn($"[SpireBridge] state build failed (partial snapshot returned): {ex.Message}");
        }

        return built;
    }

    private static string? SafePotionName(PotionModel potion)
    {
        try { return potion.Title?.GetFormattedText(); }
        catch { return null; }
    }

    /// <summary>Cost fallback chain (§4.4): GetResolved → GetAmountToSpend → Canonical → null.</summary>
    private static int? ResolveCost(CardModel card)
    {
        try { return card.EnergyCost.GetResolved(); } catch { }
        try { return card.EnergyCost.GetAmountToSpend(); } catch { }
        try { return card.EnergyCost.Canonical; } catch { }
        return null;
    }

    /// <summary>Description fallback chain (§4.3): GetDescriptionForPile → Description formatted → null.</summary>
    private static string? ResolveDescription(CardModel card)
    {
        try { return card.GetDescriptionForPile(PileType.Hand, null); } catch { }
        try { return card.Description?.GetFormattedText(); } catch { }
        return null;
    }

    /// <summary>
    /// Intent string (§5.2): label text, plus "攻撃 N" for attack intents;
    /// type name as last resort.
    /// NOTE: ApiDump verification (2026-11-15) shows MonsterModel.GetIntents()
    /// is PRIVATE in v0.113.1, contrary to the API_NOTES sketch. The public
    /// route is MonsterModel.NextMove (MoveState).Intents, used first here;
    /// the private GetIntents() is only a reflection fallback.
    /// </summary>
    private static string? ResolveIntent(Creature enemy)
    {
        try
        {
            var monster = enemy.Monster;
            if (monster == null) return null;
            var intents = GetIntentsPublic(monster) ?? GetIntentsViaReflection(monster);
            if (intents == null || intents.Count == 0) return null;

            var parts = new List<string>();
            foreach (var intent in intents)
            {
                if (intent == null) continue;
                string? part = null;
                try { part = intent.GetIntentLabel(Array.Empty<Creature>(), enemy)?.GetFormattedText(); }
                catch { }
                if (intent is AttackIntent attack)
                {
                    int? damage = null;
                    try { damage = attack.GetTotalDamage(Array.Empty<Creature>(), enemy); }
                    catch { }
                    if (damage != null)
                        part = string.IsNullOrEmpty(part) ? $"攻撃 {damage}" : $"{part} 攻撃 {damage}";
                }
                if (string.IsNullOrEmpty(part))
                {
                    try { part = intent.GetType().Name; } catch { }
                }
                if (!string.IsNullOrEmpty(part)) parts.Add(part!);
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch { return null; }
    }

    /// <summary>Public intent route: NextMove (MoveState) exposes Intents publicly.</summary>
    private static IReadOnlyList<AbstractIntent>? GetIntentsPublic(MonsterModel monster)
    {
        try
        {
            var intents = monster.NextMove?.Intents;
            if (intents != null && intents.Count > 0) return intents;
        }
        catch { }
        return null;
    }

    /// <summary>Fallback: invoke the private MonsterModel.GetIntents() via reflection.</summary>
    private static IReadOnlyList<AbstractIntent>? GetIntentsViaReflection(MonsterModel monster)
    {
        try
        {
            var method = monster.GetType().GetMethod(
                "GetIntents",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null) return null;
            return method.Invoke(monster, null) as IReadOnlyList<AbstractIntent>;
        }
        catch { return null; }
    }
}
