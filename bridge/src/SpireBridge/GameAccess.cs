using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Shared, fully-defensive accessors into the game model layer. Every method
/// here must only be called on the game thread (via GameThread.Invoke) and
/// never throws: anything unobtainable comes back as null / false / empty.
///
/// API usage strictly follows API_NOTES.md (sts2.dll v0.113.1 metadata):
///  - RunManager.Instance / DebugOnlyGetState() (State itself is private)
///  - CombatManager.Instance / DebugOnlyGetState()
///  - ActiveScreenContext.Instance.GetCurrentScreen() for overlay screens
///  - MapCoord's public fields are `col` / `row` (confirmed via ApiDump).
/// </summary>
internal static class GameAccess
{
    // -- tiny safe-eval helpers -------------------------------------------------

    public static string? SafeString(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    public static int? SafeInt(Func<int> f)
    {
        try { return f(); } catch { return null; }
    }

    public static bool? SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return null; }
    }

    public static string? FormatLoc(LocString? loc)
    {
        if (loc == null) return null;
        try { return loc.GetFormattedText(); }
        catch
        {
            try { return loc.ToString(); } catch { return null; }
        }
    }

    // -- run / combat entry points ----------------------------------------------

    /// <summary>True when a run is in progress; outs are then non-null run state (player may still be null).</summary>
    public static bool TryGetRun(out RunState? runState, out MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        runState = null;
        player = null;
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress) return false;
            runState = rm.DebugOnlyGetState();
            if (runState == null) return false;
            var players = runState.Players;
            if (players != null && players.Count > 0) player = players[0];
            return true;
        }
        catch
        {
            runState = null;
            player = null;
            return false;
        }
    }

    /// <summary>Current CombatState, or null when not in combat.</summary>
    public static CombatState? GetCombatState()
    {
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null || !cm.IsInProgress) return null;
            return cm.DebugOnlyGetState();
        }
        catch { return null; }
    }

    /// <summary>Current overlay/root screen object (IScreenContext), or null.</summary>
    public static object? GetCurrentScreenObject()
    {
        try { return ActiveScreenContext.Instance?.GetCurrentScreen(); }
        catch { return null; }
    }

    // -- rewards ------------------------------------------------------------------

    /// <summary>
    /// Best-effort RewardsSet extraction from an NRewardsScreen instance via
    /// reflection: the screen keeps it in a private field (ApiDump confirms a
    /// private field `_rewardsSet` of type RewardsSet on NRewardsScreen).
    /// We look up any instance field assignable to RewardsSet rather than
    /// hard-coding the field name. Returns null when unavailable — callers
    /// must treat rewards as unavailable, never fabricate them.
    /// </summary>
    public static RewardsSet? TryGetRewardsSet(object? screen)
    {
        try
        {
            if (screen == null) return null;
            if (screen.GetType().Name != "NRewardsScreen") return null;
            var fields = screen.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var f in fields)
            {
                if (typeof(RewardsSet).IsAssignableFrom(f.FieldType))
                    return f.GetValue(screen) as RewardsSet;
            }
            return null;
        }
        catch { return null; }
    }

    // -- targeting ------------------------------------------------------------------

    /// <summary>Only AnyEnemy cards/potions need a per-enemy target in v1.</summary>
    public static bool NeedsEnemyTarget(TargetType targetType) =>
        targetType == TargetType.AnyEnemy;

    // -- map ------------------------------------------------------------------------

    /// <summary>
    /// Children of a map point in a deterministic order (row, then col) so
    /// that choice indices are stable between /state, /actions and /do.
    /// </summary>
    public static List<MapPoint> SortedMapChildren(MapPoint? point)
    {
        var list = new List<MapPoint>();
        try
        {
            if (point?.Children == null) return list;
            list.AddRange(point.Children);
            list.Sort((a, b) =>
            {
                int byRow = a.coord.row.CompareTo(b.coord.row);
                return byRow != 0 ? byRow : a.coord.col.CompareTo(b.coord.col);
            });
        }
        catch { /* return whatever was collected */ }
        return list;
    }

    // -- async fire-and-forget -------------------------------------------------------

    /// <summary>Observes a fire-and-forget game Task so faults are logged, not lost.</summary>
    public static void FireAndForget(Task? task, string what)
    {
        if (task == null) return;
        _ = task.ContinueWith(
            t => BridgeLog.Warn($"[SpireBridge] {what} faulted: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}

/// <summary>
/// Reads the game version from release_info.json located next to (one level
/// above) the sts2 assembly. Kept in its own class so that the HTTP layer can
/// call it inside try/catch: if the sts2 assembly cannot even be loaded
/// (out-of-game harness), the failure surfaces as a catchable exception at
/// the call site and /health simply reports game_version=null.
/// </summary>
internal static class GameVersion
{
    private static string? _cached;
    private static bool _computed;

    public static string? Get()
    {
        if (_computed) return _cached;
        _computed = true;
        try
        {
            var location = typeof(RunManager).Assembly.Location;
            if (string.IsNullOrEmpty(location)) return null;
            var managedDir = Path.GetDirectoryName(location);
            if (managedDir == null) return null;

            var candidates = new List<string>
            {
                Path.Combine(managedDir, "release_info.json"),
            };
            var parent = Directory.GetParent(managedDir);
            if (parent != null) candidates.Add(Path.Combine(parent.FullName, "release_info.json"));

            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var v))
                    {
                        _cached = v.GetString();
                        return _cached;
                    }
                }
                catch { /* try next candidate */ }
            }
        }
        catch { /* game_version stays null */ }
        return null;
    }
}
