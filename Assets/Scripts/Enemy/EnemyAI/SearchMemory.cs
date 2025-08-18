// -----------------------------
// File: EnemyAICore.SearchMemory.cs (partial)
// PURPOSE: Local per-agent search memory + optional sharing (keys + world).
// Perf: uses global ray budget (defined in another partial) for movement trail;
//       dense cone kept for scans. Packed long keys replace (int,int) tuples.
// -----------------------------
using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Search Dependencies")]
        [SerializeField] internal AreaChunker2D areaChunker; // assign in prefab; we also lazy-find

        // ---- Packed key helpers (gridX,gridY) -> long
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static long K(int x, int y) => ((long)(uint)x << 32) | (uint)y;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static (int x, int y) U(long k) => ((int)(k >> 32), (int)k);

        // Core data (local memory)
        private readonly HashSet<long> _searched = new();             // local searched keys (packed)
        private readonly List<long> _searchedOrder = new();           // recency list (newest at end) (packed)
        private readonly Dictionary<int, int> _localAreaHits = new(); // areaId -> locally hit cells

        private readonly Queue<Node> _frontier = new();
        private Vector3 _searchAnchor;
        private float _searchRadius;

        // gizmo caches (world-space)
        private readonly List<Vector3> _searchedPts = new();
        private readonly List<Vector3> _sharedPts = new();   // cyan dots for shared
        private readonly List<Vector3> _frontierPts = new();

        // world lookup for keys we’ve seen locally (used when we share)
        private readonly Dictionary<long, Vector3> _keyToWorld = new(); // packed -> world

        // scratch (legacy; kept for compatibility if needed elsewhere)
        private readonly List<Vector2> _areaScratch = new(256);

        // --- Non-alloc neighbour buffer (max 8 for 8-connectivity)
        private readonly Node[] _neighBuf = new Node[8];

        // ---- Public/local coverage API ----
        internal float GetAreaCoverageLocal(int areaId)
        {
            EnsureAreaChunker();
            if (areaChunker == null || areaId < 0) return 0f;
            int denom = areaChunker.GetAreaWalkableCount(areaId);
            if (denom <= 0) return 0f;
            _localAreaHits.TryGetValue(areaId, out int num);
            return Mathf.Clamp01((float)num / denom);
        }

        /// <summary>
        /// Return a good target point in 'areaId':
        /// - If we're already inside that area: non-alloc BFS to nearest unsearched.
        /// - If we're in a different area: stride-sample that area's cached world points
        ///   to pick any valid unsearched (far enough) so we can path there immediately.
        ///   If all are already marked locally, relax the "unsearched" check as a last resort.
        /// </summary>
        internal bool TryGetUnsearchedPointInAreaLocal(int areaId, Vector2 from, out Vector3 point)
        {
            point = default;
            EnsureAreaChunker();
            if (areaChunker == null || areaId < 0 || grid == null) return false;

            var start = grid.NodeFromWorldPoint(new Vector3(from.x, from.y, 0f));
            if (start == null || !start.walkable) return false;

            _bfsQ.Clear();
            _bfsVisited.Clear();

            _bfsQ.Enqueue(start);
            _bfsVisited.Add(K(start.gridX, start.gridY));

            // Adaptive cap: when local coverage is high, keep the search tiny so we don't thrash.
            int maxExpansions = 2048;
            int denom = areaChunker.GetAreaWalkableCount(areaId);
            if (denom > 0)
            {
                _localAreaHits.TryGetValue(areaId, out int hits);
                float cov = Mathf.Clamp01((float)hits / denom);
                if (cov >= 0.95f) maxExpansions = 256;
                else if (cov >= 0.85f) maxExpansions = 512;
                else if (cov >= 0.70f) maxExpansions = 1024;
            }

            while (_bfsQ.Count > 0 && maxExpansions-- > 0)
            {
                var n = _bfsQ.Dequeue();
                if (n == null || !n.walkable) continue;

                if (areaChunker.GetAreaIdStrict(n.worldPosition) == areaId)
                {
                    long key = K(n.gridX, n.gridY);
                    if (!_searched.Contains(key))
                    {
                        point = n.worldPosition;
                        return true;
                    }
                }

                int count = grid.GetNeighboursNonAlloc(n, _neighBuf);
                for (int i = 0; i < count; i++)
                {
                    var neigh = _neighBuf[i];
                    if (neigh == null || !neigh.walkable) continue;
                    long k = K(neigh.gridX, neigh.gridY);
                    if (_bfsVisited.Add(k)) _bfsQ.Enqueue(neigh);
                }
            }

            return false;
        }


        // ---- Core lifecycle ----
        internal void ResetSearchPlan(Vector3 anchor)
        {
            _searched.Clear();
            _searchedOrder.Clear();
            _frontier.Clear();
            _searchedPts.Clear();
            _sharedPts.Clear();
            _frontierPts.Clear();
            _keyToWorld.Clear();
            _localAreaHits.Clear();

            _searchAnchor = anchor;
            _searchRadius = Mathf.Max(grid != null ? grid.nodeRadius * 4f : 1f, searchExpandRadius * 0.5f);

            EnsureAreaChunker();

            LogAI($"SearchPlan reset (anchor {anchor:F2}, startRadius {_searchRadius:0.00})");
            SeedFrontier(_searchAnchor, _searchRadius, searchBatchSize);
            PruneFrontierTooClose(_searchAnchor); // keep queue clean up front
            RefreshFrontierDebug();
            LogAI($"  after seed: frontier={_frontier.Count}, searched={_searched.Count}");
        }

        private void EnsureAreaChunker()
        {
            if (areaChunker != null) return;
            areaChunker = GetComponent<AreaChunker2D>()
                       ?? GetComponentInParent<AreaChunker2D>()
                       ?? Object.FindObjectOfType<AreaChunker2D>(true);
            areaChunker?.EnsureBuilt();
        }

        internal void EnsureFrontierHasWork(Vector3 fromPosition)
        {
            if (_frontier.Count == 0)
            {
                float prev = _searchRadius;
                _searchRadius += searchExpandRadius;
                LogAI($"Frontier empty, expanding radius {prev:0.00}->{_searchRadius:0.00}");
                SeedFrontier(fromPosition, _searchRadius, searchBatchSize);
                PruneFrontierTooClose(fromPosition);
                RefreshFrontierDebug();
                LogAI($"  after expand: frontier={_frontier.Count}");
            }
        }

        internal bool TryGetNextSearchPoint(Vector3 fromPosition, out Vector3 worldPos)
        {
            if (grid == null)
            {
                LogAI("WARN: grid is null in TryGetNextSearchPoint");
                worldPos = fromPosition;
                return false;
            }

            EnsureFrontierHasWork(fromPosition);
            if (_frontier.Count == 0)
            {
                LogAI("WARN: frontier still empty after EnsureFrontierHasWork");
                worldPos = fromPosition;
                return false;
            }

            Node best = null;
            float bestScore = float.MaxValue;

            int qCount = _frontier.Count;
            for (int i = 0; i < qCount; i++)
            {
                var n = _frontier.Dequeue();
                if (n == null) continue;

                long key = K(n.gridX, n.gridY);
                if (_searched.Contains(key)) { _frontier.Enqueue(n); continue; }

                float d = Vector2.Distance(fromPosition, n.worldPosition);
                if (d < searchMinHopDist) { _frontier.Enqueue(n); continue; }

                float bias = (d <= searchStepMaxDist) ? d : d * 2f;
                float score = bias + Random.value * 0.05f;

                if (score < bestScore) { bestScore = score; best = n; }
                _frontier.Enqueue(n);
            }

            if (best == null)
            {
                // Expand & retry
                float prev = _searchRadius;
                _searchRadius += searchExpandRadius;
                LogAI($"No acceptable node; expanding radius {prev:0.00}->{_searchRadius:0.00} and retry");
                SeedFrontier(fromPosition, _searchRadius, searchBatchSize);
                PruneFrontierTooClose(fromPosition);

                // Fallback: ensure we still move
                int guard = _frontier.Count;
                float minHop = Mathf.Max(0.25f, grid.nodeRadius * 2f, searchMinHopDist * 0.8f);

                while (guard-- > 0 && best == null)
                {
                    var n = _frontier.Dequeue();
                    if (n != null)
                    {
                        long k = K(n.gridX, n.gridY);
                        float d = Vector2.Distance(fromPosition, n.worldPosition);
                        if (n.walkable && !_searched.Contains(k) && d >= minHop) best = n;
                        _frontier.Enqueue(n);
                    }
                }

                if (best == null)
                {
                    var cur = grid.NodeFromWorldPoint(fromPosition);
                    if (cur != null)
                    {
                        int nc = grid.GetNeighboursNonAlloc(cur, _neighBuf);
                        for (int i = 0; i < nc && best == null; i++)
                        {
                            var nb = _neighBuf[i];
                            if (nb == null || !nb.walkable) continue;

                            long k = K(nb.gridX, nb.gridY);
                            float d = Vector2.Distance(fromPosition, nb.worldPosition);
                            if (!_searched.Contains(k) && d >= minHop) best = nb;
                        }
                    }
                }
            }

            if (best == null)
            {
                LogAI("FAIL: TryGetNextSearchPoint found nothing after expand");
                worldPos = fromPosition;
                return false;
            }

            long bestKey = K(best.gridX, best.gridY);
            if (_searched.Add(bestKey)) _searchedOrder.Add(bestKey);
            _searchedPts.Add(best.worldPosition);
            _keyToWorld[bestKey] = best.worldPosition;

            // bump local area coverage
            TryBumpLocalArea(best.worldPosition);

            int enqueuedNeighbors = 0;
            int ncount = grid.GetNeighboursNonAlloc(best, _neighBuf);
            for (int i = 0; i < ncount; i++)
            {
                var neigh = _neighBuf[i];
                if (neigh == null) continue;
                long k = K(neigh.gridX, neigh.gridY);
                if (neigh.walkable && !_searched.Contains(k)) { _frontier.Enqueue(neigh); enqueuedNeighbors++; }
            }

            worldPos = best.worldPosition;
            RefreshFrontierDebug();
            LogAI($"NextSearch -> {worldPos:F2} (score {bestScore:0.000}, searched={_searched.Count}, frontier+{enqueuedNeighbors} -> {_frontier.Count})");
            return true;
        }

        // --- Dense cone (used after dwell scans) ---
        internal void MarkSearchedCone(Vector3 origin, Vector2 dir, float radius, float halfAngleDeg)
            => MarkSearchedCone_Internal(origin, dir, radius, halfAngleDeg, -1);

        internal void MarkSearchedCone(Vector3 origin, Vector2 dir, float radius, float halfAngleDeg, int maxMarksOverride)
            => MarkSearchedCone_Internal(origin, dir, radius, halfAngleDeg, maxMarksOverride);

        private void MarkSearchedCone_Internal(Vector3 origin, Vector2 dir, float radius, float halfAngleDeg, int maxMarksOverride)
        {
            if (!enableSearchMarking) return;   // master switch
            if (grid == null) { LogAI("WARN: grid is null in MarkSearchedCone"); return; }
            if (radius <= 0.01f) return;

            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;

            int maxMarksCfg = (maxMarksOverride > 0) ? maxMarksOverride : searchMaxMarksPerSweep;
            int maxMarks = Mathf.Max(8, maxMarksCfg);

            int marked = 0;
            float step = Mathf.Max(grid.nodeRadius * 0.9f, 0.05f);
            float half = Mathf.Clamp(halfAngleDeg, 0f, 180f);

            for (float dx = -radius; dx <= radius; dx += step)
            {
                for (float dy = -radius; dy <= radius; dy += step)
                {
                    Vector2 off = new(dx, dy);
                    if (off.sqrMagnitude > radius * radius) continue;
                    if (half < 179.9f)
                    {
                        float ang = Vector2.Angle(dir, off.normalized);
                        if (ang > half) continue;
                    }

                    Vector3 wp = origin + new Vector3(off.x, off.y, 0f);

                    if (searchUseLOS)
                    {
                        Vector2 to = (Vector2)(wp - origin);
                        float dist = to.magnitude;
                        if (dist > 0.001f)
                        {
                            Vector2 from = (Vector2)origin + (to / dist) * searchLOSStartInset;
                            var hit = Physics2D.Linecast(from, (Vector2)wp, searchOccluderMask);
                            if (hit.collider != null) continue;
                        }
                    }

                    var n = grid.NodeFromWorldPoint(wp);
                    if (n == null || !n.walkable) continue;

                    long key = K(n.gridX, n.gridY);
                    if (_searched.Add(key))
                    {
                        _searchedOrder.Add(key);
                        _searchedPts.Add(n.worldPosition);
                        _keyToWorld[key] = n.worldPosition;
                        TryBumpLocalArea(n.worldPosition);

                        marked++;
                        if (marked >= maxMarks) goto FINISH;
                    }
                }
            }
        FINISH:
            if (marked > 0) PruneFrontierOfSearched();
            RefreshFrontierDebug();
            LogAI($"Sweep cone marked={marked}, searched={_searched.Count}, frontier={_frontier.Count}");
        }

        // --- Budgeted ray sweep (used for movement trail) ---
        internal void MarkSearchedConeBudgeted(Vector2 origin, Vector2 heading, float radius, float halfAngleDeg)
        {
            if (!enableSearchMarking) return;
            if (grid == null) return;

            int rays = Mathf.Max(1, searchRaysPerSweep);
            if (!TryConsumeBudget(rays)) return; // from Core partial

            Vector2 fwd = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector2.right;
            float half = Mathf.Clamp(halfAngleDeg, 1f, 180f) * Mathf.Deg2Rad;

            int marked = 0;
            int maxMarks = Mathf.Max(1, searchMaxMarksPerSweep);

            float step = Mathf.Max(grid.nodeRadius * 2f, 0.1f);
            const int staleLimit = 8;

            for (int i = 0; i < rays; i++)
            {
                float t = (rays == 1) ? 0f : (i / (float)(rays - 1)) * 2f - 1f; // [-1..1]
                float a = t * half;
                float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                Vector2 dir = new(ca * fwd.x - sa * fwd.y, sa * fwd.x + ca * fwd.y);

                int hits = Physics2D.RaycastNonAlloc(origin, dir, _rayBuf, radius, visionBlockMask);
                float dist = hits > 0 ? _rayBuf[0].distance : radius;

                long lastKey = long.MinValue;
                int stale = 0;

                for (float s = step; s <= dist; s += step)
                {
                    Vector3 wp = (Vector2)origin + dir * s;
                    var n = grid.NodeFromWorldPoint(wp);
                    if (n == null || !n.walkable) continue;

                    long key = K(n.gridX, n.gridY);
                    if (key == lastKey) continue;
                    lastKey = key;

                    if (_searched.Add(key))
                    {
                        _searchedOrder.Add(key);
                        _searchedPts.Add(n.worldPosition);
                        _keyToWorld[key] = n.worldPosition;
                        TryBumpLocalArea(n.worldPosition);

                        if (++marked >= maxMarks) return;
                        stale = 0;
                    }
                    else
                    {
                        if (++stale >= staleLimit) break;
                    }
                }
            }
        }

        // --- Used by COMMS (receiver) ---
        internal void IngestSearchedKeys(IList<(int x, int y)> keys, int maxAdd, IList<Vector3> world = null)
        {
            if (keys == null || keys.Count == 0 || maxAdd <= 0) return;

            int added = 0;
            for (int i = 0; i < keys.Count && added < maxAdd; i++)
            {
                var k = keys[i];
                long kp = K(k.x, k.y);
                if (_searched.Add(kp))
                {
                    _searchedOrder.Add(kp);
                    added++;

                    if (world != null && i < world.Count)
                    {
                        var wp = world[i];
                        _sharedPts.Add(wp);
                        _keyToWorld[kp] = wp;
                        if (wp != Vector3.zero) TryBumpLocalArea(wp);
                    }
                }
            }

            if (added > 0)
            {
                PruneFrontierOfSearched();
                RefreshFrontierDebug();
                if (debugComms) CommsDbg($"ingest: +{added} shared/searched (now {_searched.Count}), frontier={_frontier.Count}");
            }
            else if (debugComms)
            {
                CommsDbg("ingest: no new cells");
            }
        }

        // Allow COMMS to look up world positions to send
        internal bool TryWorldForKey((int x, int y) key, out Vector3 worldPos)
            => _keyToWorld.TryGetValue(K(key.x, key.y), out worldPos);

        // ---- Local coverage bump ----
        private void TryBumpLocalArea(Vector3 worldPos)
        {
            EnsureAreaChunker();
            if (areaChunker == null) return;
            int area = areaChunker.GetAreaIdStrict(worldPos);
            if (area < 0) return;
            _localAreaHits.TryGetValue(area, out int c);
            _localAreaHits[area] = c + 1;
        }

        // Remove searched nodes from the frontier queue
        private void PruneFrontierOfSearched()
        {
            int count = _frontier.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
            {
                var n = _frontier.Dequeue();
                if (n == null) continue;
                long key = K(n.gridX, n.gridY);
                if (!_searched.Contains(key)) _frontier.Enqueue(n);
            }
        }

        // Optional: remove entries that are too close to bother with
        private void PruneFrontierTooClose(Vector3 from)
        {
            int count = _frontier.Count;
            if (count == 0) return;
            float minHop = Mathf.Max(0.25f, grid.nodeRadius * 2f, searchMinHopDist * 0.8f);

            for (int i = 0; i < count; i++)
            {
                var n = _frontier.Dequeue();
                if (n == null) continue;
                if (Vector2.Distance(from, n.worldPosition) >= minHop) _frontier.Enqueue(n);
            }
        }

        private void SeedFrontier(Vector3 around, float radius, int budget)
        {
            if (grid == null) { LogAI("WARN: grid is null in SeedFrontier"); return; }

            int added = 0, duplicates = 0;
            float r = Mathf.Max(radius, grid.nodeRadius * 2f);

            float offset = Random.value * Mathf.PI * 2f;
            int samples = Mathf.Max(12, Mathf.CeilToInt(r * 6f));

            for (int i = 0; i < samples && added < budget; i++)
            {
                float t = offset + (i / (float)samples) * Mathf.PI * 2f;
                Vector3 p = around + new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f) * r;
                var n = grid.NodeFromWorldPoint(p);
                if (n != null && n.walkable)
                {
                    long key = K(n.gridX, n.gridY);
                    if (!_searched.Contains(key)) { _frontier.Enqueue(n); added++; }
                    else duplicates++;
                }
            }

            // random interior samples
            int interiorAdded = 0;
            int interior = Mathf.Min(8, budget - added);
            for (int i = 0; i < interior; i++)
            {
                Vector2 rnd = Random.insideUnitCircle * r;
                Vector3 p = around + new Vector3(rnd.x, rnd.y, 0f);
                var n = grid.NodeFromWorldPoint(p);
                if (n != null && n.walkable)
                {
                    long key = K(n.gridX, n.gridY);
                    if (!_searched.Contains(key)) { _frontier.Enqueue(n); added++; interiorAdded++; }
                    else duplicates++;
                }
            }

            RefreshFrontierDebug();
            LogAI($"SeedFrontier center {around:F2} r={r:0.00} -> added={added} (ring {added - interiorAdded}, interior {interiorAdded}), dupes={duplicates}, frontier={_frontier.Count}");
        }

        // --- Gizmos (kept as-is to match your current script) ---
        [Header("Search Debug Gizmos")]
        [SerializeField] internal bool showSearchGizmos = true;
        [SerializeField] internal float gizmoPointSizeSearched = 0.12f;
        [SerializeField] internal float gizmoPointSizeFrontier = 0.08f;
        [SerializeField] internal Color gizmoColorSearched = new(1f, 0.2f, 0.2f, 0.35f);
        [SerializeField] internal Color gizmoColorShared = new(0.2f, 1f, 1f, 0.65f);
        [SerializeField] internal Color gizmoColorFrontier = new(1f, 0.95f, 0.2f, 0.30f);
        [SerializeField] internal Color gizmoColorAnchor = new(0.2f, 1f, 1f, 0.60f);

        private void RefreshFrontierDebug()
        {
            _frontierPts.Clear();
            foreach (var n in _frontier) { if (n != null) _frontierPts.Add(n.worldPosition); }
        }

        private void OnDrawGizmosSelected()
        {
            if (!showSearchGizmos) return;

            Gizmos.color = gizmoColorAnchor;
            Gizmos.DrawWireSphere(_searchAnchor, Mathf.Max(_searchRadius, 0.01f));

            Gizmos.color = gizmoColorSearched;
            float rS = Mathf.Max(0.001f, gizmoPointSizeSearched);
            for (int i = 0; i < _searchedPts.Count; i++) Gizmos.DrawSphere(_searchedPts[i], rS);

            Gizmos.color = gizmoColorShared;
            for (int i = 0; i < _sharedPts.Count; i++) Gizmos.DrawSphere(_sharedPts[i], rS);

            Gizmos.color = gizmoColorFrontier;
            float rF = Mathf.Max(0.001f, gizmoPointSizeFrontier);
            for (int i = 0; i < _frontierPts.Count; i++) Gizmos.DrawSphere(_frontierPts[i], rF);
        }

        // --- Sharing samplers ---
        internal void CopySearchedKeysSample(List<(int x, int y)> outList, int max)
        {
            outList.Clear();
            int total = _searched.Count;
            if (total == 0 || max <= 0) return;

            int stride = Mathf.Max(1, total / Mathf.Max(1, max));
            int i = 0;
            foreach (var k in _searched)
                if ((i++ % stride) == 0)
                {
                    outList.Add(U(k));
                    if (outList.Count >= max) break;
                }
        }

        internal void CopySearchedKeysSampleRecency(List<(int x, int y)> outList, int max)
        {
            outList.Clear();
            int total = _searchedOrder.Count;
            if (total == 0 || max <= 0) return;

            int take = Mathf.Min(max, total);
            for (int i = 0; i < take; i++)
                outList.Add(U(_searchedOrder[total - 1 - i]));
        }

        // --- BFS scratch (local to this partial) ---
        private readonly Queue<Node> _bfsQ = new();
        private readonly HashSet<long> _bfsVisited = new();
    }
}
