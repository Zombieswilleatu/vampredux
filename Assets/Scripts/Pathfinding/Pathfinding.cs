using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace EnemyAI
{
    public class Pathfinding : MonoBehaviour
    {
        [Header("Performance")]
        public int maxNodesPerPath = 1200;                 // hard ceiling for direct A*
        public int maxNodesCoarse = 200;
        public float hierarchicalThreshold = 8f;           // lowered to prefer hierarchical sooner
        [Range(2, 8)] public int coarseGridScale = 4;

        [Header("Heuristic")]
        [Range(0.5f, 2.0f)] public float heuristicScale = 1.25f; // slightly greedy → fewer expansions

        [Header("Robustness")]
        public bool returnPartialIfBudgetHit = true;
        public float goalProximityEpsilon = 0.75f;         // relaxed stop condition
        public bool retryWithReducedClearance = true;
        [Range(0.5f, 0.99f)] public float clearanceRetryFactor = 0.85f;

        [Header("Spike control (fine A*)")]
        [Tooltip("If a direct search exceeds this many node expansions, escalate to hierarchical.")]
        public int directExpandBudget = 350;

        [Tooltip("If a direct search runs longer than this (ms), escalate to hierarchical.")]
        public float directTimeBudgetMs = 0.6f;

        [Tooltip("When a direct search hits a budget, try hierarchical automatically.")]
        public bool escalateToHierOnBudget = true;

        [Header("Spike control (coarse A*)")]
        [Tooltip("Number of coarse cells to expand before yielding.")]
        public int coarseExpandBudget = 64;

        [Tooltip("Time slice (ms) for the coarse pass before yielding.")]
        public float coarseTimeBudgetMs = 0.75f;

        private Grid grid;

        // ---- Coarse cache keyed by (cell, clearance bucket) ----
        struct CoarseKey : IEquatable<CoarseKey>
        {
            public Vector2Int p;
            public int clrBucket;
            public bool Equals(CoarseKey other) => p == other.p && clrBucket == other.clrBucket;
            public override bool Equals(object obj) => obj is CoarseKey other && Equals(other);
            public override int GetHashCode() => unchecked((p.GetHashCode() * 397) ^ clrBucket);
        }
        private readonly Dictionary<CoarseKey, bool> coarseWalkableCache = new();
        private float lastCoarseCacheTime = -1f;
        private const float COARSE_CACHE_LIFETIME = 5f;

        // ---- A* scratch (reused per path; avoids GC spikes) ----
        private readonly Dictionary<Node, Node> _cameFrom = new(2048);
        private readonly Dictionary<Node, float> _gScore = new(2048);
        private readonly HashSet<Node> _closed = new();
        private readonly MinHeap _open = new(); // (Node,f)
        private readonly Node[] _nbuf = new Node[8];

        // ---- Coarse pooled structures (no per-call allocs) ----
        private readonly List<Vector2Int> _cOpen = new(256);
        private readonly HashSet<Vector2Int> _cOpenSet = new();
        private readonly HashSet<Vector2Int> _cClosed = new();
        private readonly Dictionary<Vector2Int, Vector2Int> _cCameFrom = new(512);
        private readonly Dictionary<Vector2Int, float> _cG = new(512);
        private readonly Dictionary<Vector2Int, float> _cF = new(512);

        // ---- Profiling (fine-grained scopes) ----
        static readonly ProfilerMarker kPF_Find = new("Pathfinding.FindPath");
        static readonly ProfilerMarker kPF_Neigh = new("Pathfinding.GetNeighbours");
        static readonly ProfilerMarker kPF_Hier = new("Pathfinding.Hierarchical"); // used around dispatch points

        private float NodeDiameter => grid != null ? grid.nodeRadius * 2f : 1f;

        // signal from FindPathDirect → FindPath to escalate
        private bool _hintEscalateHier = false;

        void Awake()
        {
            grid = GetComponent<Grid>();
            if (grid == null) grid = FindObjectOfType<Grid>();
            if (grid == null) Debug.LogError("[Pathfinding] No Grid found in scene.", this);
        }

        public LayerMask unwalkableMask => grid != null ? grid.unwalkableMask : 0;

        // -------------------------------
        // Public API
        // -------------------------------
        public void FindPath(Vector3 startPos, Vector3 targetPos, float clearanceRadius, Action<List<Node>> onPathFound)
        {
            if (grid == null) { onPathFound?.Invoke(null); return; }

            float distance = Vector3.Distance(startPos, targetPos);
            var startC = WorldToCoarseGrid(startPos);
            var goalC = WorldToCoarseGrid(targetPos);

            // prefer hierarchical on long trips
            if (distance > hierarchicalThreshold && startC != goalC)
            {
                using (kPF_Hier.Auto())
                {
                    PFLog($"Using hierarchical pathfinding for {distance:F1} unit path", this);
                    FindPathHierarchical(startPos, targetPos, clearanceRadius, onPathFound);
                }
                return;
            }

            _hintEscalateHier = false;
            var result = FindPathDirect(startPos, targetPos, clearanceRadius,
                                        Mathf.Min(maxNodesPerPath, directExpandBudget * 2));

            // escalate if the direct search hit a budget
            if (result == null && _hintEscalateHier && escalateToHierOnBudget)
            {
                using (kPF_Hier.Auto())
                {
                    PFLog("Escalating to hierarchical (direct search hit budget).", this);
                    FindPathHierarchical(startPos, targetPos, clearanceRadius, onPathFound);
                }
                return;
            }

            if (result == null && retryWithReducedClearance)
            {
                float retryClear = clearanceRadius * clearanceRetryFactor;
                PFLog($"RETRY with reduced clearance: {clearanceRadius:0.00} -> {retryClear:0.00}", this);
                result = FindPathDirect(startPos, targetPos, retryClear, maxNodesPerPath);
            }

            onPathFound?.Invoke(result);
        }

        public void FindPath(Vector3 startPos, Vector3 targetPos, float clearanceRadius,
                             Action<List<Node>> onPathFound, bool forceExtendedLimit)
        {
            if (forceExtendedLimit) FindPathHierarchical(startPos, targetPos, clearanceRadius, onPathFound);
            else FindPath(startPos, targetPos, clearanceRadius, onPathFound);
        }

        // -------------------------------
        // Prewarm
        // -------------------------------
        public void PrewarmCoarse(Vector3 center, float clearanceRadius, int radiusCells = 2)
        {
            if (grid == null) return;

            var fine = grid.NodeFromWorldPoint(center);
            if (fine == null) return;

            int cscale = Mathf.Max(1, coarseGridScale);
            var c = new Vector2Int(fine.gridX / cscale, fine.gridY / cscale);

            int clrBucket = Mathf.Max(0, Mathf.CeilToInt(clearanceRadius / (grid.nodeRadius + 1e-6f)));
            float bucketClearance = clrBucket * grid.nodeRadius;

            int rx = Mathf.Clamp(radiusCells, 0, 16), ry = rx;

            for (int dx = -rx; dx <= rx; dx++)
            {
                for (int dy = -ry; dy <= ry; dy++)
                {
                    var p = new Vector2Int(c.x + dx, c.y + dy);
                    if (!InCoarseBounds(p)) continue;

                    _ = IsCoarseNodeWalkable(p, clrBucket, bucketClearance);

                    var r = new Vector2Int(p.x + 1, p.y);
                    if (dx < rx && InCoarseBounds(r))
                        _ = HasBoundaryGate(p, r, bucketClearance, clrBucket);

                    var u = new Vector2Int(p.x, p.y + 1);
                    if (dy < ry && InCoarseBounds(u))
                        _ = HasBoundaryGate(p, u, bucketClearance, clrBucket);

                    var ru = new Vector2Int(p.x + 1, p.y + 1);
                    if (dx < rx && dy < ry && InCoarseBounds(ru))
                        _ = HasBoundaryGate(p, ru, bucketClearance, clrBucket);
                }
            }
        }

        // -------------------------------
        // Hierarchical (coarse → fine segments) — async
        // -------------------------------
        private void FindPathHierarchical(Vector3 startPos, Vector3 targetPos, float clearanceRadius, Action<List<Node>> onPathFound)
        {
            StartCoroutine(FindPathHierarchicalRoutine(startPos, targetPos, clearanceRadius, onPathFound));
        }

        private IEnumerator FindPathHierarchicalRoutine(Vector3 startPos, Vector3 targetPos, float clearanceRadius, Action<List<Node>> onPathFound)
        {
            if (grid == null) { onPathFound?.Invoke(null); yield break; }

            // warm small rings so first frame doesn’t pay cold-cache costs
            PrewarmCoarse(startPos, clearanceRadius, 2);
            PrewarmCoarse(targetPos, clearanceRadius, 2);

            // ❶ Budgeted coarse A*
            List<Vector3> coarsePath = null;
            bool coarseDone = false;
            yield return StartCoroutine(FindCoarsePathBudgeted(startPos, targetPos, clearanceRadius,
                p => { coarsePath = p; coarseDone = true; }));

            if (!coarseDone || coarsePath == null)
            {
                PFLog("Hierarchical: No coarse path", this);
                onPathFound?.Invoke(null);
                yield break;
            }

            if (coarsePath.Count == 0)
            {
                var direct = FindPathDirect(startPos, targetPos, clearanceRadius, maxNodesPerPath)
                          ?? (retryWithReducedClearance
                              ? FindPathDirect(startPos, targetPos, clearanceRadius * clearanceRetryFactor, maxNodesPerPath)
                              : null);
                onPathFound?.Invoke(direct);
                yield break;
            }

            // ❷ Stitch fine segments; yield between segments
            var fullPath = new List<Node>(256);
            Vector3 cur = startPos;

            for (int i = 0; i <= coarsePath.Count; i++)
            {
                Vector3 next = (i == coarsePath.Count) ? targetPos : coarsePath[i];

                // LOS shortcut to avoid many fine A* calls
                List<Node> seg = null;
                if (HasClearanceLine(cur, next, clearanceRadius))
                {
                    var endNode = grid.NodeFromWorldPoint(next, clearanceRadius);
                    if (endNode != null) seg = new List<Node> { endNode };
                }

                if (seg == null)
                {
                    seg = FindPathDirect(cur, next, clearanceRadius, maxNodesPerPath)
                       ?? (retryWithReducedClearance
                            ? FindPathDirect(cur, next, clearanceRadius * clearanceRetryFactor, maxNodesPerPath)
                            : null);
                }

                if (seg == null)
                {
                    PFLog($"Hierarchical: segment {i} failed", this);
                    onPathFound?.Invoke(fullPath.Count > 0 ? fullPath : null);
                    yield break;
                }

                if (fullPath.Count > 0 && seg.Count > 0)
                {
                    var last = fullPath[fullPath.Count - 1];
                    var first = seg[0];
                    if (ReferenceEquals(last, first) || (last.gridX == first.gridX && last.gridY == first.gridY))
                        seg.RemoveAt(0);
                }

                fullPath.AddRange(seg);
                cur = next;

                // spread work (major spike killer)
                yield return null;
            }

            PFLog($"Hierarchical: Built full path with {fullPath.Count} nodes", this);
            onPathFound?.Invoke(fullPath);
        }

        // -------------------------------
        // Budgeted Coarse pathfinding (clustered A*)
        // -------------------------------
        private IEnumerator FindCoarsePathBudgeted(Vector3 startPos, Vector3 targetPos, float clearanceRadius, Action<List<Vector3>> done)
        {
            // clear pooled containers (no new allocations)
            _cOpen.Clear(); _cOpenSet.Clear(); _cClosed.Clear();
            _cCameFrom.Clear(); _cG.Clear(); _cF.Clear();

            // refresh coarse walkability cache periodically
            if (Time.time - lastCoarseCacheTime > COARSE_CACHE_LIFETIME)
            {
                coarseWalkableCache.Clear();
                lastCoarseCacheTime = Time.time;
            }

            var startC = WorldToCoarseGrid(startPos);
            var goalC = WorldToCoarseGrid(targetPos);
            if (!InCoarseBounds(startC) || !InCoarseBounds(goalC)) { done(null); yield break; }

            int clrBucket = Mathf.Max(0, Mathf.CeilToInt(clearanceRadius / (grid.nodeRadius + 1e-6f)));
            float bucketClearance = clrBucket * grid.nodeRadius;

            if (!IsCoarseNodeWalkable(startC, clrBucket, bucketClearance) ||
                !IsCoarseNodeWalkable(goalC, clrBucket, bucketClearance)) { done(null); yield break; }

            if (startC == goalC) { done(new List<Vector3>(0)); yield break; }

            _cOpen.Add(startC);
            _cOpenSet.Add(startC);
            _cG[startC] = 0f;
            _cF[startC] = CoarseHeuristic(startC, goalC);

            int expandedSinceYield = 0;
            float t0 = Time.realtimeSinceStartup;

            while (_cOpen.Count > 0)
            {
                // pick best f (simple scan; open is small)
                int bestIdx = 0;
                float bestF = GetCoarseScore(_cF, _cOpen[0], float.MaxValue);
                for (int i = 1; i < _cOpen.Count; i++)
                {
                    float f = GetCoarseScore(_cF, _cOpen[i], float.MaxValue);
                    if (f < bestF) { bestF = f; bestIdx = i; }
                }

                var current = _cOpen[bestIdx];
                _cOpen.RemoveAt(bestIdx);
                _cOpenSet.Remove(current);

                if (current == goalC)
                {
                    var path = new List<Vector3>(32);
                    var node = goalC;
                    while (_cCameFrom.ContainsKey(node))
                    {
                        path.Add(CoarseToWorldCenter(node));
                        node = _cCameFrom[node];
                    }
                    path.Reverse();
                    done(path);
                    yield break;
                }

                _cClosed.Add(current);

                foreach (var nb in GetCoarseNeighbors(current))
                {
                    if (!InCoarseBounds(nb) || _cClosed.Contains(nb)) continue;
                    if (!IsCoarseNodeWalkable(nb, clrBucket, bucketClearance)) continue;
                    if (!HasBoundaryGate(current, nb, bucketClearance, clrBucket)) continue;

                    float tentativeG = GetCoarseScore(_cG, current, float.MaxValue) + CoarseHeuristic(current, nb);

                    if (!_cOpenSet.Contains(nb))
                    {
                        _cOpen.Add(nb);
                        _cOpenSet.Add(nb);
                    }
                    else if (tentativeG >= GetCoarseScore(_cG, nb, float.MaxValue)) continue;

                    _cCameFrom[nb] = current;
                    _cG[nb] = tentativeG;
                    _cF[nb] = tentativeG + CoarseHeuristic(nb, goalC) * heuristicScale;
                }

                // yield on budget (prevents multi-frame spikes)
                if (++expandedSinceYield >= coarseExpandBudget ||
                    ((Time.realtimeSinceStartup - t0) * 1000f) > coarseTimeBudgetMs)
                {
                    expandedSinceYield = 0;
                    t0 = Time.realtimeSinceStartup;
                    yield return null;
                }
            }

            done(null);
        }

        private Vector2Int WorldToCoarseGrid(Vector3 worldPos)
        {
            var fineNode = grid.NodeFromWorldPoint(worldPos);
            if (fineNode == null) return new Vector2Int(-1, -1);
            return new Vector2Int(fineNode.gridX / coarseGridScale, fineNode.gridY / coarseGridScale);
        }

        private Vector3 CoarseToWorldCenter(Vector2Int coarsePos)
        {
            int cx = Mathf.Clamp(coarsePos.x * coarseGridScale + (coarseGridScale / 2), 0, grid.gridSizeXPublic - 1);
            int cy = Mathf.Clamp(coarsePos.y * coarseGridScale + (coarseGridScale / 2), 0, grid.gridSizeYPublic - 1);
            var n = grid.GetNodeByGridCoords(cx, cy, 0f);
            if (n != null) return n.worldPosition;

            float worldX = (cx - grid.gridSizeXPublic * 0.5f + 0.5f) * NodeDiameter;
            float worldY = (cy - grid.gridSizeYPublic * 0.5f + 0.5f) * NodeDiameter;
            return new Vector3(worldX, worldY, 0f);
        }

        private int CoarseMaxX => (grid.gridSizeXPublic + coarseGridScale - 1) / coarseGridScale;
        private int CoarseMaxY => (grid.gridSizeYPublic + coarseGridScale - 1) / coarseGridScale;
        private bool InCoarseBounds(Vector2Int p) => (uint)p.x < CoarseMaxX && (uint)p.y < CoarseMaxY;

        private List<Vector2Int> GetCoarseNeighbors(Vector2Int pos)
        {
            var neighbors = new List<Vector2Int>(8);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    neighbors.Add(new Vector2Int(pos.x + dx, pos.y + dy));
                }
            return neighbors;
        }

        private float CoarseHeuristic(Vector2Int a, Vector2Int b)
        {
            int dx = Mathf.Abs(a.x - b.x);
            int dy = Mathf.Abs(b.y - a.y);
            return Mathf.Sqrt(dx * dx + dy * dy) * coarseGridScale;
        }

        private float GetCoarseScore(Dictionary<Vector2Int, float> scores, Vector2Int node, float defaultValue)
            => scores.TryGetValue(node, out var v) ? v : defaultValue;

        private bool IsCoarseNodeWalkable(Vector2Int coarsePos, int clrBucket, float bucketClearance)
        {
            var key = new CoarseKey { p = coarsePos, clrBucket = clrBucket };
            if (coarseWalkableCache.TryGetValue(key, out bool ok)) return ok;

            int walkableCount = 0, total = 0;
            int x0 = coarsePos.x * coarseGridScale;
            int y0 = coarsePos.y * coarseGridScale;

            for (int lx = 0; lx < coarseGridScale; lx++)
                for (int ly = 0; ly < coarseGridScale; ly++)
                {
                    int fx = x0 + lx, fy = y0 + ly;
                    if ((uint)fx >= grid.gridSizeXPublic || (uint)fy >= grid.gridSizeYPublic) continue;

                    var node = grid.GetNodeByGridCoords(fx, fy, bucketClearance);
                    if (node != null)
                    {
                        total++;
                        if (node.walkable) walkableCount++;
                    }
                }

            bool walkable = (total > 0) && (walkableCount > total / 2);
            coarseWalkableCache[key] = walkable;
            return walkable;
        }

        // Require a walkable "gate" along the shared edge; diagonals require both orthogonals (no corner cut)
        private bool HasBoundaryGate(Vector2Int a, Vector2Int b, float bucketClearance, int clrBucket)
        {
            int dx = b.x - a.x;
            int dy = b.y - a.y;

            if (dx == 0 && Math.Abs(dy) == 1)
            {
                int yEdge = (dy > 0) ? (b.y * coarseGridScale) : (a.y * coarseGridScale);
                int x0 = a.x * coarseGridScale;
                for (int lx = 0; lx < coarseGridScale; lx++)
                {
                    int fxA = x0 + lx;
                    int fyA = yEdge - (dy > 0 ? 1 : 0);
                    int fxB = fxA;
                    int fyB = yEdge + (dy > 0 ? 0 : -1) + 1;

                    if ((uint)fxA >= grid.gridSizeXPublic || (uint)fyA >= grid.gridSizeYPublic) continue;
                    if ((uint)fxB >= grid.gridSizeXPublic || (uint)fyB >= grid.gridSizeYPublic) continue;

                    var nA = grid.GetNodeByGridCoords(fxA, fyA, bucketClearance);
                    var nB = grid.GetNodeByGridCoords(fxB, fyB, bucketClearance);
                    if (nA != null && nB != null && nA.walkable && nB.walkable) return true;
                }
                return false;
            }
            if (dy == 0 && Math.Abs(dx) == 1)
            {
                int xEdge = (dx > 0) ? (b.x * coarseGridScale) : (a.x * coarseGridScale);
                int y0 = a.y * coarseGridScale;
                for (int ly = 0; ly < coarseGridScale; ly++)
                {
                    int fxA = xEdge - (dx > 0 ? 1 : 0);
                    int fyA = y0 + ly;
                    int fxB = xEdge + (dx > 0 ? 0 : -1) + 1;
                    int fyB = fyA;

                    if ((uint)fxA >= grid.gridSizeXPublic || (uint)fyA >= grid.gridSizeYPublic) continue;
                    if ((uint)fxB >= grid.gridSizeXPublic || (uint)fyB >= grid.gridSizeYPublic) continue;

                    var nA = grid.GetNodeByGridCoords(fxA, fyA, bucketClearance);
                    var nB = grid.GetNodeByGridCoords(fxB, fyB, bucketClearance);
                    if (nA != null && nB != null && nA.walkable && nB.walkable) return true;
                }
                return false;
            }

            if (Math.Abs(dx) == 1 && Math.Abs(dy) == 1)
            {
                var aX = new Vector2Int(a.x + dx, a.y);
                var aY = new Vector2Int(a.x, a.y + dy);
                if (!InCoarseBounds(aX) || !InCoarseBounds(aY)) return false;
                if (!IsCoarseNodeWalkable(aX, clrBucket, bucketClearance)) return false;
                if (!IsCoarseNodeWalkable(aY, clrBucket, bucketClearance)) return false;

                return HasBoundaryGate(a, aX, bucketClearance, clrBucket) &&
                       HasBoundaryGate(a, aY, bucketClearance, clrBucket);
            }

            return false;
        }

        // -------------------------------
        // Fine A* (direct)  — with budgets & escalation hint
        // -------------------------------
        private List<Node> FindPathDirect(Vector3 startPos, Vector3 targetPos, float clearanceRadius, int nodeLimit)
        {
            using (kPF_Find.Auto())
            {
                if (grid == null) return null;

                PFLog($"Direct path: start=({startPos.x:0.##},{startPos.y:0.##}) end=({targetPos.x:0.##},{targetPos.y:0.##}) limit={nodeLimit}", this);

                Node start = grid.NodeFromWorldPoint(startPos, clearanceRadius);
                Node goal = grid.NodeFromWorldPoint(targetPos, clearanceRadius);

                if (start == null || goal == null || !start.walkable || !goal.walkable)
                    return null;

                if (start.gridX == goal.gridX && start.gridY == goal.gridY)
                    return new List<Node>(); // already there

                _cameFrom.Clear(); _gScore.Clear(); _closed.Clear(); _open.Clear();

                float startF = Heuristic(start, goal) * heuristicScale;
                _gScore[start] = 0f;
                _open.InsertOrDecrease(start, startF);

                int nodesExpanded = 0;
                Node bestSoFar = start;
                float bestDistance = Vector3.Distance(start.worldPosition, goal.worldPosition);

                float t0 = Time.realtimeSinceStartup; // seconds

                while (_open.Count > 0 && nodesExpanded < nodeLimit)
                {
                    // budgets to prevent long lock-ups in a single frame
                    float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                    if (nodesExpanded >= directExpandBudget || ms > directTimeBudgetMs)
                    {
                        _hintEscalateHier = true;
                        break; // return null → caller escalates to hierarchical
                    }

                    Node current = _open.PopMin();
                    if (current == null) break;

                    float dist = Vector3.Distance(current.worldPosition, goal.worldPosition);
                    if (current == goal || (current.gridX == goal.gridX && current.gridY == goal.gridY) || dist <= goalProximityEpsilon)
                    {
                        var path = ReconstructPath(_cameFrom, start, current);
                        PFLog($"Path found: {path.Count} nodes, {nodesExpanded} expanded", this);
                        return path;
                    }

                    if (dist < bestDistance) { bestSoFar = current; bestDistance = dist; }

                    _closed.Add(current);
                    nodesExpanded++;

                    using (kPF_Neigh.Auto())
                    {
                        int ncount = grid.GetNeighboursNonAlloc(current, _nbuf, clearanceRadius);
                        for (int i = 0; i < ncount; i++)
                        {
                            Node nb = _nbuf[i];
                            if (!nb.walkable || _closed.Contains(nb)) continue;

                            float tentativeG = (_gScore.TryGetValue(current, out var gCur) ? gCur : float.PositiveInfinity)
                                             + Vector3.Distance(current.worldPosition, nb.worldPosition);

                            if (_gScore.TryGetValue(nb, out var gOld) && tentativeG >= gOld) continue;

                            _cameFrom[nb] = current;
                            _gScore[nb] = tentativeG;
                            float f = tentativeG + Heuristic(nb, goal) * heuristicScale;
                            _open.InsertOrDecrease(nb, f);
                        }
                    }
                }

                if (_hintEscalateHier)
                {
                    PFLog($"Direct search hit budget (expanded={nodesExpanded}, {(Time.realtimeSinceStartup - t0) * 1000f:0.00}ms); escalating.", this);
                    return null; // caller will escalate
                }

                if (returnPartialIfBudgetHit && bestSoFar != start && _cameFrom.ContainsKey(bestSoFar))
                {
                    var partial = ReconstructPath(_cameFrom, start, bestSoFar);
                    PFLog($"Partial path: {partial.Count} nodes (budget hit)", this);
                    return partial;
                }

                PFLog($"Path failed: {nodesExpanded} nodes expanded", this);
                return null;
            }
        }

        // -------------------------------
        // Helpers
        // -------------------------------
        private static float Heuristic(Node a, Node b) => Vector3.Distance(a.worldPosition, b.worldPosition);

        private static List<Node> ReconstructPath(Dictionary<Node, Node> cameFrom, Node start, Node end)
        {
            var path = new List<Node>(64);
            Node cur = end;
            while (cur != null && cur != start)
            {
                path.Add(cur);
                if (!cameFrom.TryGetValue(cur, out cur)) break;
            }
            path.Reverse();
            return path;
        }

        // Conservative LOS check with clearance; steps along the line sampling grid nodes.
        private bool HasClearanceLine(Vector3 a, Vector3 b, float clearanceRadius)
        {
            if (grid == null) return false;
            float dist = Vector3.Distance(a, b);
            if (dist <= Mathf.Epsilon) return true;

            float step = Mathf.Max(NodeDiameter * 0.5f, 0.1f);
            int samples = Mathf.Max(2, Mathf.CeilToInt(dist / step));
            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                var p = Vector3.Lerp(a, b, t);
                var n = grid.NodeFromWorldPoint(p, clearanceRadius);
                if (n == null || !n.walkable) return false;
            }
            return true;
        }

        public void NotifyEnvironmentChange(Vector3 position, float radius = 5f)
        {
            coarseWalkableCache.Clear();
            if (grid == null) return;

            try
            {
                var t = grid.GetType();

                var mRegion = t.GetMethod("InvalidateCacheRegion",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Vector3), typeof(float) },
                    null);
                if (mRegion != null) { mRegion.Invoke(grid, new object[] { position, radius }); return; }

                var mClear1 = t.GetMethod("ClearWalkabilityCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mClear1 != null) { mClear1.Invoke(grid, null); return; }

                var mClear2 = t.GetMethod("ClearCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mClear2 != null) { mClear2.Invoke(grid, null); return; }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Pathfinding] Cache invalidation reflection failed: {ex.Message}", this);
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void PFLog(string msg, UnityEngine.Object ctx = null)
        {
            Debug.Log($"<color=#58D>[PF]</color> {msg}", ctx == null ? this : ctx);
        }

        // ===============================
        // Local Min-Heap for A* open set
        // ===============================
        private sealed class MinHeap
        {
            private struct Entry { public Node node; public float f; }

            private readonly List<Entry> _heap = new(256);
            private readonly Dictionary<Node, int> _indices = new(2048);

            public int Count => _heap.Count;

            public void Clear() { _heap.Clear(); _indices.Clear(); }

            public void InsertOrDecrease(Node n, float f)
            {
                if (_indices.TryGetValue(n, out int i))
                {
                    if (f >= _heap[i].f) return;
                    _heap[i] = new Entry { node = n, f = f };
                    SiftUp(i);
                    return;
                }

                _heap.Add(new Entry { node = n, f = f });
                int idx = _heap.Count - 1;
                _indices[n] = idx;
                SiftUp(idx);
            }

            public Node PopMin()
            {
                if (_heap.Count == 0) return null;

                var min = _heap[0].node;
                var last = _heap[_heap.Count - 1];
                _heap.RemoveAt(_heap.Count - 1);
                _indices.Remove(min);

                if (_heap.Count > 0)
                {
                    _heap[0] = last;
                    _indices[last.node] = 0;
                    SiftDown(0);
                }
                return min;
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_heap[p].f <= _heap[i].f) break;
                    Swap(i, p);
                    i = p;
                }
            }

            private void SiftDown(int i)
            {
                while (true)
                {
                    int l = i * 2 + 1;
                    if (l >= _heap.Count) break;
                    int r = l + 1;
                    int s = (r < _heap.Count && _heap[r].f < _heap[l].f) ? r : l;
                    if (_heap[i].f <= _heap[s].f) break;
                    Swap(i, s);
                    i = s;
                }
            }

            private void Swap(int a, int b)
            {
                var t = _heap[a];
                _heap[a] = _heap[b];
                _heap[b] = t;
                _indices[_heap[a].node] = a;
                _indices[_heap[b].node] = b;
            }
        }
    }
}
