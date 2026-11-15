namespace SpireBridge;

// Managed-only logic: no engine or game model dependencies.
internal static class EnemyDisplayLogic
{
    internal static EnemyPosition? Normalize(double left, double top, double width, double height,
        double windowWidth, double windowHeight)
    {
        if (!new[] { left, top, width, height, windowWidth, windowHeight }.All(double.IsFinite)
            || width <= 0 || height <= 0 || windowWidth <= 0 || windowHeight <= 0) return null;
        double x = (left + width / 2) / windowWidth, y = (top + height / 2) / windowHeight;
        double w = width / windowWidth, h = height / windowHeight;
        if (!new[] { x, y, w, h }.All(double.IsFinite) || x < 0 || x > 1 || y < 0 || y > 1
            || w <= 0 || h <= 0) return null;
        var result = new EnemyPosition { X = Math.Round(x, 4), Y = Math.Round(y, 4),
            W = Math.Round(w, 4), H = Math.Round(h, 4) };
        return result.W > 0 && result.H > 0 ? result : null;
    }

    internal static string? TargetedLabel(string? name, string? enemyName, string? enemyLabel)
        => enemyName == null ? name : enemyLabel == null ? $"{name} -> {enemyName}" : $"{name} -> {enemyLabel} {enemyName}";
}

internal sealed class EnemyLabelState
{
    private object? _combat;
    private readonly Dictionary<object, string> _labels = new(ReferenceEqualityComparer.Instance);
    private int _next;

    internal IReadOnlyDictionary<object, string> Assign(object? combat,
        IEnumerable<(object Reference, int Index, double? X)> enemies)
    {
        if (combat == null || !ReferenceEquals(combat, _combat))
        {
            _combat = combat;
            _labels.Clear();
            _next = 0;
        }
        if (combat == null) return _labels;
        foreach (var enemy in enemies.Where(e => !_labels.ContainsKey(e.Reference))
                     .OrderBy(e => e.X ?? double.PositiveInfinity).ThenBy(e => e.Index))
        {
            if (_labels.ContainsKey(enemy.Reference)) continue;
            int number = _next++;
            _labels.Add(enemy.Reference, ((char)('A' + number % 26)).ToString()
                + (number < 26 ? "" : (number / 26 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return _labels;
    }
}
