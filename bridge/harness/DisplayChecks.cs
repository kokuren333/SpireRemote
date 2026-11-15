using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using SpireBridge;

// Pure managed checks: no game object construction, native engine, game thread,
// singleton access or live endpoint. Source objects below are synthetic tokens.
static class DisplayChecks
{
    public static void Run()
    {
        static void Check(bool condition, string detail)
        {
            if (!condition) throw new Exception("Display check failed: " + detail);
        }
        var source = new EqualToken();
        var other = new EqualToken(); // Equal by value, but not the same model reference.
        var snapshot = new StateSnapshot {
            Choices = new() { new ChoiceInfo { Index = 0, Kind = "card_select", Label = "Shown card", Description = "Shown description" } }
        };
        var cards = Array.Empty<CardModel>();
        var potions = Array.Empty<PotionModel>();
        var enemies = Array.Empty<Creature>();
        var choices = new object[] { source };
        var result = DisplayAccess.MatchFocus(new object[] { source, source }, snapshot, cards, potions, enemies, choices);
        Check(result?.Kind == "choice" && result.Index == 0 && result.Name == "Shown card" && result.Description == "Shown description", "reference mapping and duplicate ownership");
        Check(DisplayAccess.MatchFocus(new object[] { other }, snapshot, cards, potions, enemies, choices) == null, "value equality must not match");
        Check(DisplayAccess.MatchFocus(new object[] { source, other }, snapshot, cards, potions, enemies, choices) == null, "ambiguous ownership must not match");
        Check(DisplayAccess.MatchFocus(Array.Empty<object>(), snapshot, cards, potions, enemies, choices) == null, "missing focus");
        Check(DisplayAccess.Read(new Derived(), "_owned") is string text && text == "inherited", "private inherited field");
        Check(DisplayAccess.Read(new Derived(), "Model") == null, "throwing getter");
        Check(!DisplayAccess.FocusModels(new Derived()).Any(), "unrelated and throwing ownership ignored");
        Check(DisplayAccess.SelectionCards(new Derived()).Count == 0, "no offered collection returns empty");
        Check(DisplayAccess.MapDescription("Shop") == "ショップ" && DisplayAccess.MapDescription("Unassigned") == null, "known and unknown map descriptions");
        snapshot.Potions.Add(new PotionInfo());
        snapshot.Selected = result;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Check(doc.RootElement.GetProperty("potions")[0].GetProperty("description").ValueKind == JsonValueKind.Null, "nullable potion description emitted");
        Check(doc.RootElement.GetProperty("choices")[0].GetProperty("description").GetString() == "Shown description", "choice description emitted");
        Check(doc.RootElement.GetProperty("selected").GetProperty("index").GetInt32() == 0, "selected DTO serialized");
        Console.WriteLine("DISPLAY_CHECKS_PASS");
    }

    sealed class EqualToken
    {
        public override bool Equals(object? obj) => obj is EqualToken;
        public override int GetHashCode() => 0;
    }
    class Base
    {
        private readonly string _owned = "inherited";
        public override string ToString() => _owned;
    }
    sealed class Derived : Base
    {
        public object Model => throw new InvalidOperationException("unavailable");
        public object ArbitraryGetter => throw new Exception("must never be evaluated");
    }
}
