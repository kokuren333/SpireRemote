using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SpireBridge;

internal static class EnemyPositionAccess
{
    // Called only during Build on the game thread. A failed traversal invalidates all positions.
    internal static EnemyPosition?[] Read(IReadOnlyList<Creature> enemies, IReadOnlyList<EnemyInfo> info)
    {
        var result = new EnemyPosition?[enemies.Count];
        try
        {
            var room = NRun.Instance?.CombatRoom;
            if (room == null) return result;
            var nodes = new List<NCreature>();
            var pending = new Stack<(Node Node, int Depth)>();
            pending.Push((room, 0));
            int visited = 0;
            while (pending.Count > 0)
            {
                var (node, depth) = pending.Pop();
                if (++visited > 2000 || depth > 8) return result;
                if (node is NCreature creature) nodes.Add(creature);
                int children = node.GetChildCount(false);
                if (depth == 8 && children > 0) return result;
                if (children > 2000 - visited - pending.Count) return result;
                for (int i = 0; i < children; i++) pending.Push((node.GetChild(i, false), depth + 1));
            }
            for (int i = 0; i < enemies.Count; i++)
            {
                try
                {
                    if (info[i].Alive == false) continue;
                    var matches = nodes.Where(n => ReferenceEquals(n.Entity, enemies[i])).Take(2).ToArray();
                    if (matches.Length != 1) continue;
                    var node = matches[0];
                    Control rectNode = node.Hitbox ?? node;
                    if (!node.IsInsideTree() || !node.IsVisibleInTree()
                        || !rectNode.IsInsideTree() || !rectNode.IsVisibleInTree()) continue;
                    var root = node.GetTree().Root;
                    if (root == null || node.GetViewport() != root || rectNode.GetViewport() != root) continue;
                    var windowSize = root.Size;
                    var localSize = rectNode.Size;
                    if (localSize.X <= 0 || localSize.Y <= 0) continue;
                    var transform = root.GetFinalTransform() * rectNode.GetGlobalTransformWithCanvas();
                    var rect = transform * new Rect2(Vector2.Zero, localSize);
                    result[i] = EnemyDisplayLogic.Normalize(rect.Position.X, rect.Position.Y,
                        rect.Size.X, rect.Size.Y, windowSize.X, windowSize.Y);
                }
                catch { result[i] = null; }
            }
        }
        catch { Array.Clear(result); }
        return result;
    }
}
