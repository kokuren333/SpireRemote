using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rewards;

namespace SpireBridge;

// Read-only display helpers. Called only from Build on the captured game thread.
// Reflection is bounded to known members; no arbitrary getters or model graphs.
internal static class DisplayAccess
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal static object? Read(object? source, string name)
    {
        if (source == null) return null;
        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            try
            {
                var field = type.GetField(name, Members);
                if (field != null) return field.GetValue(source);
                var property = type.GetProperty(name, Members);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(source);
            }
            catch { return null; }
        }
        return null;
    }

    internal static string? Description(object model)
    {
        try
        {
            return model switch
            {
                CardModel card => CardDescription(card),
                PotionModel potion => GameAccess.FormatLoc(potion.DynamicDescription),
                RelicModel relic => GameAccess.FormatLoc(relic.DynamicDescription),
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string? CardDescription(CardModel card)
    {
        try { return card.GetDescriptionForPile(PileType.Hand, null); } catch { }
        try { return card.Description?.GetFormattedText(); } catch { return null; }
    }

    internal static string? Name(object model)
    {
        try
        {
            return model switch
            {
                CardModel card => card.Title,
                PotionModel potion => GameAccess.FormatLoc(potion.Title),
                RelicModel relic => GameAccess.FormatLoc(relic.Title),
                _ => null,
            };
        }
        catch { return null; }
    }

    internal static string? RewardName(Reward reward)
    {
        // Only return a real model title; otherwise retain the formatted reward text.
        var model = Read(reward, "Potion") ?? Read(reward, "Relic");
        return model == null ? null : Name(model);
    }

    internal static string? MapDescription(string pointType) => pointType switch
    {
        "Monster" => "通常戦闘", "Elite" => "エリート", "RestSite" => "休憩所",
        "Shop" or "Merchant" => "ショップ", "Treasure" => "宝箱", "Event" => "イベント",
        "Boss" => "ボス", "Unknown" => "？", _ => null,
    };

    internal static IReadOnlyList<CardModel> SelectionCards(object? screen)
    {
        try
        {
            if (screen == null) return Array.Empty<CardModel>();
            // Inspect only the active screen's declared card collections, including base classes.
            var candidates = new List<List<CardModel>>();
            for (var type = screen.GetType(); type != null && type != typeof(Control); type = type.BaseType)
            foreach (var field in type.GetFields(Members))
            {
                try
                {
                    List<CardModel>? cards = null;
                    if (typeof(IEnumerable<CardModel>).IsAssignableFrom(field.FieldType))
                        cards = (field.GetValue(screen) as IEnumerable<CardModel>)?.ToList();
                    else if (typeof(IEnumerable<CardCreationResult>).IsAssignableFrom(field.FieldType))
                        cards = (field.GetValue(screen) as IEnumerable<CardCreationResult>)?.Select(c => c.Card).ToList();
                    if (cards is { Count: > 0 }) candidates.Add(cards);
                }
                catch { }
            }
            // Conflicting collections are ambiguous; never combine pools or selected-card lists.
            if (candidates.Count == 0) return Array.Empty<CardModel>();
            var first = candidates[0];
            return candidates.All(c => c.SequenceEqual(first, ReferenceEqualityComparer.Instance))
                ? first : Array.Empty<CardModel>();
        }
        catch { return Array.Empty<CardModel>(); }
    }

    internal static void AddShopChoices(List<ChoiceInfo> choices, List<object> sources)
    {
        try
        {
            var ui = NRun.Instance?.MerchantRoom?.Inventory;
            // A closed inventory is not currently offered to the player.
            if (ui == null || !ui.IsOpen) return;
            var inventory = ui.Inventory;
            if (inventory == null) return;
            foreach (var entry in inventory.AllEntries)
            {
                try
                {
                    if (!entry.IsStocked) continue;
                    object? model = entry switch
                    {
                        MerchantCardEntry card => card.CreationResult?.Card,
                        MerchantPotionEntry potion => potion.Model,
                        MerchantRelicEntry relic => relic.Model,
                        _ => null,
                    };
                    if (model == null) continue; // card removal / unknown slots have no item model.
                    choices.Add(new ChoiceInfo { Index = choices.Count, Kind = "shop_item", Label = Name(model),
                        Description = $"{entry.Cost}G" + (Description(model) is string text ? $" ＋ {text}" : "") });
                    sources.Add(model);
                }
                catch { }
            }
        }
        catch { }
    }

    internal static void AddTreasureChoices(List<ChoiceInfo> choices, List<object> sources)
    {
        try
        {
            var room = NRun.Instance?.TreasureRoom;
            if (Read(room, "_isRelicCollectionOpen") is not true) return;
            var collection = Read(room, "_relicCollection");
            if (Read(collection, "HoldersInUse") is not System.Collections.IEnumerable holders) return;
            foreach (var holder in holders)
            {
                try
                {
                    if (Read(Read(holder, "Relic"), "Model") is not RelicModel relic) continue;
                    choices.Add(new ChoiceInfo { Index = choices.Count, Kind = "relic", Label = Name(relic),
                        Description = Description(relic) });
                    sources.Add(relic);
                }
                catch { }
            }
        }
        catch { }
    }

    internal static IEnumerable<object> FocusModels(object source, int depth = 0)
    {
        if (depth > 2) yield break;
        if (source is CardModel or PotionModel or EventOption or RestSiteOption or Reward or Creature or RelicModel)
        {
            yield return source;
            yield break;
        }
        // Ownership members confirmed by metadata. Excludes preview targets, cached focus,
        // global state, siblings and collections (which could identify an unrelated item).
        foreach (var name in new[] { "Model", "CardModel", "Option", "Reward", "Entity", "_model", "_option", "_reward", "_entity" })
        {
            var model = Read(source, name);
            if (model is CardModel or PotionModel or EventOption or RestSiteOption or Reward or Creature or RelicModel)
                yield return model;
        }
        foreach (var name in new[] { "CardNode", "Potion", "Relic" })
        {
            var wrapper = Read(source, name);
            if (wrapper != null && !ReferenceEquals(wrapper, source))
                foreach (var model in FocusModels(wrapper, depth + 1)) yield return model;
        }
    }

    internal static SelectedInfo? MatchFocus(IEnumerable<object> models, StateSnapshot snap,
        IReadOnlyList<CardModel> hand, IReadOnlyList<PotionModel> potions,
        IReadOnlyList<Creature> enemies, IReadOnlyList<object> choices)
    {
        // Two distinct ownership models at the same level are ambiguous, even if
        // only one happens to occur in this snapshot.
        var unique = models.Distinct(ReferenceEqualityComparer.Instance).ToList();
        if (unique.Count != 1) return null;
        var source = unique[0];
        static int Index<T>(IReadOnlyList<T> list, object value) =>
            Enumerable.Range(0, list.Count).FirstOrDefault(i => ReferenceEquals(list[i], value), -1);
        int index;
        if ((index = Index(hand, source)) >= 0 && index < snap.Hand.Count)
            return new SelectedInfo { Kind = "card", Index = index, Name = snap.Hand[index].Name, Description = snap.Hand[index].Description };
        if ((index = Index(potions, source)) >= 0 && index < snap.Potions.Count)
            return new SelectedInfo { Kind = "potion", Index = index, Name = snap.Potions[index].Name, Description = snap.Potions[index].Description };
        if ((index = Index(enemies, source)) >= 0 && index < snap.Enemies.Count)
            return new SelectedInfo { Kind = "enemy", Index = index, Name = snap.Enemies[index].Name, Description = null };
        if ((index = Index(choices, source)) >= 0 && index < snap.Choices.Count)
            return new SelectedInfo { Kind = "choice", Index = index, Name = snap.Choices[index].Label, Description = snap.Choices[index].Description };
        return null;
    }

    internal static SelectedInfo? Selected(object? screen, StateSnapshot snap, IReadOnlyList<CardModel> hand,
        IReadOnlyList<PotionModel> potions, IReadOnlyList<Creature> enemies, IReadOnlyList<object> choices)
    {
        try
        {
            var root = screen as Node ?? NRun.Instance;
            var focus = root?.GetViewport()?.GuiGetFocusOwner();
            // Stop at the first model owner. A mismatching owner does not justify
            // searching farther up into screen/container state.
            int remaining = 12;
            for (Node? node = focus; node != null && remaining-- > 0; node = node.GetParent())
            {
                var models = FocusModels(node).ToList();
                if (models.Count > 0) return MatchFocus(models, snap, hand, potions, enemies, choices);
                if (ReferenceEquals(node, root)) break;
            }
        }
        catch { }
        return null;
    }
}
