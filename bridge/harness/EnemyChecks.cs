using SpireBridge;

static class EnemyChecks
{
    public static void Run()
    {
        static void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("Enemy check failed: " + name);
            Console.WriteLine("PASS " + name);
        }
        var state = new EnemyLabelState();
        object combat = new(), a = new EqualToken(), b = new EqualToken(), c = new EqualToken(), d = new();
        var labels = state.Assign(combat, new[] { (a, 0, (double?)0.8), (b, 1, (double?)0.2), (c, 2, (double?)0.5) });
        Check(labels[b] == "A" && labels[c] == "B" && labels[a] == "C", "AC-B2 x order and equal-valued distinct references");
        labels = state.Assign(combat, new[] { (a, 0, (double?)0.1), (c, 1, (double?)0.9) });
        Check(labels[a] == "C" && labels[c] == "B", "AC-B2 identity stable after movement and disappearance");
        labels = state.Assign(combat, new[] { (d, 0, (double?)0.0) });
        Check(labels[d] == "D", "AC-B2 later arrival uses next unused label");
        labels = state.Assign(new object(), new[] { (a, 0, (double?)null), (b, 2, (double?)0.3), (c, 1, (double?)0.3) });
        Check(labels[c] == "A" && labels[b] == "B" && labels[a] == "C", "AC-B2 combat reset, null positions last and index tie break");
        var tokens = Enumerable.Range(0, 53).Select(_ => new object()).ToArray();
        labels = state.Assign(new object(), tokens.Select((t, i) => (t, i, (double?)null)));
        Check(labels[tokens[26]] == "A2" && labels[tokens[51]] == "Z2" && labels[tokens[52]] == "A3", "AC-B2 label rollover");
        Check(state.Assign(null, Array.Empty<(object, int, double?)>()).Count == 0, "AC-B2 leaving combat clears references");
        labels = state.Assign(combat, new[] { (a, 0, (double?)null) });
        Check(labels[a] == "A", "AC-B2 reentry starts at A");

        var pos = EnemyDisplayLogic.Normalize(10, 20, 33, 44, 123, 234);
        Check(pos?.X == 0.2154 && pos.Y == 0.1795 && pos.W == 0.2683 && pos.H == 0.1880, "AC-B3 center and size rounded to four decimals");
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            for (int field = 0; field < 6; field++)
            {
                var values = new double[] { 10, 20, 30, 40, 100, 100 };
                values[field] = bad;
                if (EnemyDisplayLogic.Normalize(values[0], values[1], values[2], values[3], values[4], values[5]) != null)
                    throw new Exception("Nonfinite field accepted: " + field);
            }
        Check(true, "AC-B3 all nonfinite inputs rejected");
        Check(EnemyDisplayLogic.Normalize(0, 0, 0, 1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, 0, 1, -1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, 0, -1, 1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, 0, 1, 0, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, 0, 1, 1, 0, 100) == null
            && EnemyDisplayLogic.Normalize(0, 0, 1, 1, 100, 0) == null, "AC-B3 nonpositive rectangle and window sizes rejected");
        Check(EnemyDisplayLogic.Normalize(99.501, 0, 1, 1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(-0.51, 0, 1, 1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, 99.501, 1, 1, 100, 100) == null
            && EnemyDisplayLogic.Normalize(0, -0.51, 1, 1, 100, 100) == null,
            "AC-B3 centers outside bounds rejected before rounding");
        Check(EnemyDisplayLogic.TargetedLabel("ストライク", "スライム", "B") == "ストライク -> B スライム"
            && EnemyDisplayLogic.TargetedLabel("ストライク", "スライム", null) == "ストライク -> スライム",
            "AC-B4 targeted label with and without enemy label");
        var action = new ActionInfo { Label = EnemyDisplayLogic.TargetedLabel("ストライク", "スライム", "B"), Target = "スライム" };
        Check(action.Target == "スライム", "AC-B4 target remains enemy name");
    }
    sealed class EqualToken
    {
        public override bool Equals(object? obj) => obj is EqualToken;
        public override int GetHashCode() => 0;
    }
}
