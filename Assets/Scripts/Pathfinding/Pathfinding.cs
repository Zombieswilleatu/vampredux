using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Unity.Profiling;

namespace EnemyAI
{
    public class Pathfinding : MonoBehaviour
    {
        [Header("Performance")]
        [Tooltip("Hard stop on expanded nodes per path to avoid worst-case stalls.")]
        public int maxNodesPerPath = 600;

        [Header("Heuristic")]
        [Tooltip("Scale on the heuristic (1 = Euclidean). >1 more greedy, <1 more cautious.")]
        [Range(0.5f, 2.0f)] public float heuristicScale = 1.0f;

        private Grid grid;

        // --- Profiling markers (no counters) ---
        static readonly ProfilerMarker kPF_Find = new("Pathfinding.FindPath");
        static readonly ProfilerMarker kPF_Neighbors = new("Pathfinding.GetNeighbours");

        void Awake()
        {
            grid = GetComponent<Grid>();
            if (grid == null)
                grid = FindObjectOfType<Grid>();
            if (grid == null)
                Debug.LogError("[Pathfinding] No Grid found in scene.", this);
        }

        public LayerMask unwalkableMask => grid != null ? grid.unwalkableMask : 0;

        // -------------------------------
        // Public API
        // -------------------------------
        public void FindPath(Vector3 startPos, Vector3 targetPos, float clearanceRadius, Action<List<Node>> onPathFound)
        {
            using (kPF_Find.Auto())
            {
                if (grid == null) { onPathFound?.Invoke(null); return; }

                PFLog($"REQ clr={clearanceRadius:0.00} start=({startPos.x:0.##},{startPos.y:0.##}) end=({targetPos.x:0.##},{targetPos.y:0.##})", this);

                // Quick short-circuit
                if ((startPos - targetPos).sqrMagnitude < 1e-6f)
                {
                    PFLog("OK len=0 checked=1", this);
                    onPathFound?.Invoke(new List<Node>()); // match old behavior: empty path if already there
                    return;
                }

                Node start = grid.NodeFromWorldPoint(startPos, clearanceRadius);
                Node goal = grid.NodeFromWorldPoint(targetPos, clearanceRadius);

                if (start == null || goal == null || !start.walkable || !goal.walkable)
                {
                    onPathFound?.Invoke(null);
                    return;
                }

                // A* with local dictionaries (do NOT mutate Node fields).
                var open = new List<Node>(128);
                var openSet = new HashSet<Node>();
                var closed = new HashSet<Node>();

                var cameFrom = new Dictionary<Node, Node>(256);
                var gScore = new Dictionary<Node, float>(256);
                var fScore = new Dictionary<Node, float>(256);

                gScore[start] = 0f;
                fScore[start] = Heuristic(start, goal) * heuristicScale;

                open.Add(start);
                openSet.Add(start);

                int nodesExpanded = 0;

                while (open.Count > 0 && nodesExpanded < maxNodesPerPath)
                {
                    // Find node with min fScore in open (simple, robust; can swap for a heap later)
                    int bestIndex = 0;
                    float bestF = GetScore(fScore, open[0], float.PositiveInfinity);
                    for (int i = 1; i < open.Count; i++)
                    {
                        float f = GetScore(fScore, open[i], float.PositiveInfinity);
                        if (f < bestF) { bestF = f; bestIndex = i; }
                    }

                    Node current = open[bestIndex];
                    open.RemoveAt(bestIndex);
                    openSet.Remove(current);

                    if (current == goal)
                    {
                        var path = ReconstructPath(cameFrom, start, goal);
                        PFLog($"OK len={path.Count} checked={nodesExpanded + 1}", this);
                        onPathFound?.Invoke(path);
                        return;
                    }

                    closed.Add(current);
                    nodesExpanded++;

                    using (kPF_Neighbors.Auto())
                    {
                        var neighbors = grid.GetNeighbours(current, clearanceRadius);
                        for (int i = 0; i < neighbors.Count; i++)
                        {
                            Node nb = neighbors[i];
                            if (!nb.walkable || closed.Contains(nb)) continue;

                            float tentativeG = GetScore(gScore, current, float.PositiveInfinity)
                                             + Vector3.Distance(current.worldPosition, nb.worldPosition);

                            if (!openSet.Contains(nb))
                            {
                                open.Add(nb);
                                openSet.Add(nb);
                            }
                            else if (tentativeG >= GetScore(gScore, nb, float.PositiveInfinity))
                            {
                                // Not a better path
                                continue;
                            }

                            // This path to nb is best so far
                            cameFrom[nb] = current;
                            gScore[nb] = tentativeG;
                            fScore[nb] = tentativeG + Heuristic(nb, goal) * heuristicScale;
                        }
                    }
                }

                // No path or node budget hit
                PFLog($"FAIL checked={nodesExpanded}", this);
                onPathFound?.Invoke(null);
            }
        }

        public void NotifyEnvironmentChange(Vector3 position, float radius = 5f)
        {
            if (grid == null) return;

            // Best-effort: try to clear/expire any walkability cache on the Grid if it exposes a suitable method.
            // This avoids compile-time coupling to specific Grid APIs.
            try
            {
                var t = grid.GetType();

                // Try region-based invalidation first
                var mRegion = t.GetMethod("InvalidateCacheRegion",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Vector3), typeof(float) },
                    null);

                if (mRegion != null) { mRegion.Invoke(grid, new object[] { position, radius }); return; }

                // Try full cache clears as fallback
                var mClear1 = t.GetMethod("ClearWalkabilityCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mClear1 != null) { mClear1.Invoke(grid, null); return; }

                var mClear2 = t.GetMethod("ClearCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mClear2 != null) { mClear2.Invoke(grid, null); return; }

                // If none exist, it's harmless to do nothing.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Pathfinding] Cache invalidation reflection failed: {ex.Message}", this);
            }
        }

        // -------------------------------
        // Helpers
        // -------------------------------
        private static float GetScore(Dictionary<Node, float> map, Node n, float @default)
            => map.TryGetValue(n, out var v) ? v : @default;

        private static float Heuristic(Node a, Node b)
            => Vector3.Distance(a.worldPosition, b.worldPosition);

        private static List<Node> ReconstructPath(Dictionary<Node, Node> cameFrom, Node start, Node goal)
        {
            var path = new List<Node>(64);
            Node cur = goal;
            while (cur != null && cur != start)
            {
                path.Add(cur);
                if (!cameFrom.TryGetValue(cur, out cur)) break;
            }
            path.Reverse();
            return path;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void PFLog(string msg, UnityEngine.Object ctx = null)
        {
            Debug.Log($"<color=#58D>[PF]</color> {msg}", ctx == null ? this : ctx);
        }
    }
}
