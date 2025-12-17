using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public sealed class SearchMemory
    {
        private readonly EnemyAICore _core;
        private readonly Grid _grid;
        private readonly AreaChunker2D _chunker;
        private readonly BfsLocal _bfs;

        // COVERAGE TRACKING
        private bool[] _searched;
        private readonly Dictionary<int, int> _areaSearchedCount = new Dictionary<int, int>(64);
        private int _totalMarkCount = 0;
        private int _totalCellsMarked = 0;

        // Frontier ring
        private readonly List<Vector3> _frontier = new List<Vector3>(256);
        private int _frontierCursor = 0;
        private Vector3 _anchor;

        // de-dup
        private readonly HashSet<int> _seedSeen = new HashSet<int>();

        // scratch
        private readonly List<Vector3> _tempPoints = new List<Vector3>(256);

        public SearchMemory(EnemyAICore core, Grid grid, AreaChunker2D chunker)
        {
            _core = core;
            _grid = grid;
            _chunker = chunker;
            _bfs = new BfsLocal(grid, chunker);

            if (_chunker == null)
            {
                Debug.LogError("[SearchMemory] AreaChunker2D is null! Coverage tracking will fail.");
                return;
            }

            _chunker.EnsureBuilt();
            int n = _chunker.GridWidth * _chunker.GridHeight;
            _searched = new bool[n];

            Debug.Log($"<color=cyan>[SearchMemory]</color> Init: {n} cells ({_chunker.GridWidth}x{_chunker.GridHeight})");
        }

        public void ResetPlan(Vector3 anchor)
        {
            _anchor = anchor;
            _frontier.Clear();
            _frontierCursor = 0;
            SeedFrontierAround(anchor, _core.searchBatchSize);
        }

        public void RefreshFrontierForPosition(Vector3 pos)
        {
            _anchor = pos;
            _frontier.Clear();
            _frontierCursor = 0;
            SeedFrontierAround(pos, _core.searchBatchSize);
        }

        public bool TryGetUnsearchedPointInAreaLocal(int areaId, Vector3 from, out Vector3 pick, int maxExpansions = 2048)
        {
            if (_chunker == null || _searched == null)
            {
                pick = default;
                return false;
            }

            return _chunker.TryGetUnsearchedPointInArea(areaId, from, _searched, out pick, maxExpansions);
        }

        public bool TryGetNextSearchPoint(Vector3 from, out Vector3 pick)
        {
            // 1) consume from frontier
            while (_frontierCursor < _frontier.Count)
            {
                var cand = _frontier[_frontierCursor++];
                if (IsGoodCandidate(from, cand))
                {
                    pick = cand;
                    return true;
                }
            }

            // 2) reseed near `from` and retry
            _frontier.Clear();
            _frontierCursor = 0;
            SeedFrontierAround(from, _core.searchBatchSize);

            while (_frontierCursor < _frontier.Count)
            {
                var cand = _frontier[_frontierCursor++];
                if (IsGoodCandidate(from, cand))
                {
                    pick = cand;
                    return true;
                }
            }

            // 3) last resort: push forward away from anchor
            Vector2 dir = ((Vector2)from - (Vector2)_anchor);
            if (dir.sqrMagnitude < 0.0001f) dir = Random.insideUnitCircle.normalized;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;

            float fallbackDist = Mathf.Max(2f, _core.searchMinHopDist * 2f);
            pick = from + (Vector3)(dir.normalized * fallbackDist);
            return true;
        }

        public float GetAreaCoverage(int areaId)
        {
            if (_chunker == null || _searched == null) return 0f;

            int total = _chunker.GetAreaWalkableCount(areaId);
            if (total <= 0) return 1f;

            _areaSearchedCount.TryGetValue(areaId, out int searched);
            float cov = (float)searched / total;
            return Mathf.Clamp01(cov);
        }

        public float GetTotalCoverage()
        {
            if (_chunker == null) return 0f;

            int areaCount = _chunker.AreaCount;
            if (areaCount == 0) return 0f;

            long totalWalkable = 0;
            long totalSearched = 0;

            for (int i = 0; i < areaCount; i++)
            {
                int walkable = _chunker.GetAreaWalkableCount(i);
                _areaSearchedCount.TryGetValue(i, out int searched);
                totalWalkable += walkable;
                totalSearched += searched;
            }

            if (totalWalkable == 0) return 0f;
            return Mathf.Clamp01((float)totalSearched / totalWalkable);
        }

        public int GetAreaWalkableNodesStrictCached(int areaId)
            => (_chunker != null) ? _chunker.GetAreaWalkableCount(areaId) : 0;

        // === Minimal, quiet marking (no per-sweep spam) ===
        public void MarkSearchedConeBudgeted(Vector2 origin, Vector2 heading, float radius, float halfAngleDeg)
        {
            if (_chunker == null || _searched == null) return;

            float r = radius * Mathf.Max(1f, _core.searchRadiusBoost);
            float half = Mathf.Clamp(halfAngleDeg * Mathf.Max(1f, _core.searchAngleBoost), 1f, 180f);

            float r2 = r * r;
            float cosThresh = Mathf.Cos(half * Mathf.Deg2Rad);

            Vector2 min = origin - new Vector2(r, r);
            Vector2 max = origin + new Vector2(r, r);

            int gridW = _chunker.GridWidth;
            int gridH = _chunker.GridHeight;

            Vector2Int cmin = WorldToCell(min);
            Vector2Int cmax = WorldToCell(max);

            cmin.x = Mathf.Clamp(cmin.x, 0, gridW - 1);
            cmin.y = Mathf.Clamp(cmin.y, 0, gridH - 1);
            cmax.x = Mathf.Clamp(cmax.x, 0, gridW - 1);
            cmax.y = Mathf.Clamp(cmax.y, 0, gridH - 1);

            LayerMask losMask = (_core.searchOccluderMask.value != 0)
                ? _core.searchOccluderMask
                : new LayerMask { value = Physics2D.DefaultRaycastLayers };

            int cellsMarked = 0;

            for (int cy = cmin.y; cy <= cmax.y; cy++)
                for (int cx = cmin.x; cx <= cmax.x; cx++)
                {
                    int idx = cy * gridW + cx;
                    if (idx < 0 || idx >= _searched.Length) continue;

                    Vector2 w = CellCenterWorld(cx, cy);
                    int areaId = _chunker.GetAreaIdStrict(w);
                    if (areaId < 0) continue; // wall or portal

                    Vector2 v = w - origin;
                    if (v.sqrMagnitude > r2) continue;

                    if (half < 179.9f)
                    {
                        float cos = Vector2.Dot(heading.normalized, v.normalized);
                        if (cos < cosThresh) continue;
                    }

                    if (_core.searchUseLOS && Physics2D.Linecast(origin, w, losMask).collider != null)
                        continue;

                    if (!_searched[idx])
                    {
                        _searched[idx] = true;
                        cellsMarked++;
                        _totalCellsMarked++;

                        if (_areaSearchedCount.TryGetValue(areaId, out int count))
                            _areaSearchedCount[areaId] = count + 1;
                        else
                            _areaSearchedCount[areaId] = 1;
                    }
                }

            _totalMarkCount++;
            // no per-sweep debug print
        }

        public void LogCoverageDetail()
        {
            if (_chunker == null) return;

            int areaCount = _chunker.AreaCount;
            float total = GetTotalCoverage() * 100f;
            Debug.Log($"<color=#7ABFFF>[SearchMem]</color> total={total:0.1}% areas={areaCount} markSweeps={_totalMarkCount} totalCellsMarked={_totalCellsMarked}");
            for (int a = 0; a < areaCount; a++)
            {
                float cov = GetAreaCoverage(a) * 100f;
                int walkable = _chunker.GetAreaWalkableCount(a);
                _areaSearchedCount.TryGetValue(a, out int searched);
                Debug.Log($"  area {a}: {cov:0.1}% ({searched}/{walkable})");
            }
        }

        public bool TryGetAreaHits(int areaId, out int hits)
        {
            _areaSearchedCount.TryGetValue(areaId, out hits);
            return hits > 0;
        }

        public int TotalMarkedCells
        {
            get
            {
                if (_searched == null) return 0;
                int count = 0;
                for (int i = 0; i < _searched.Length; i++)
                    if (_searched[i]) count++;
                return count;
            }
        }

        // ----- COMMS SUPPORT (unchanged behavior) -----
        public void CopySearchedKeysSample(List<int> dst, int sampleBudget)
        {
            if (dst == null || _chunker == null || _grid == null || _searched == null) return;
            dst.Clear();

            int areaCount = _chunker.AreaCount;
            if (areaCount == 0) return;

            _tempPoints.Clear();
            int perArea = Mathf.Max(1, sampleBudget / areaCount);

            for (int a = 0; a < areaCount; a++)
            {
                _tempPoints.Clear();
                _chunker.CopyAreaWorldPointsSampled(a, _tempPoints, perArea, Random.Range(0, 1000));

                foreach (var pt in _tempPoints)
                {
                    var node = _grid.NodeFromWorldPoint(pt);
                    if (node != null)
                    {
                        int key = node.gridY * _grid.gridSizeXPublic + node.gridX;
                        dst.Add(key);
                    }
                }
            }
        }

        public void CopySearchedKeysSampleRecency(List<int> dst, int sampleBudget)
            => CopySearchedKeysSample(dst, sampleBudget);

        public void IngestSearchedKeys(List<int> flatKeys, List<Vector3> worlds, float recencyBoost)
        {
            if (_chunker == null || _searched == null) return;

            if (worlds != null && worlds.Count > 0)
            {
                foreach (var world in worlds)
                    MarkWorldSearched(world);
                return;
            }

            if (flatKeys == null || flatKeys.Count == 0 || _grid == null) return;

            int w = _grid.gridSizeXPublic;
            int h = _grid.gridSizeYPublic;
            int n = w * h;

            foreach (int key in flatKeys)
            {
                if (key < 0 || key >= n) continue;

                int gx = key % w;
                int gy = key / w;

                var node = _grid.GetNodeByGridCoords(gx, gy);
                if (node != null)
                    MarkWorldSearched(node.worldPosition);
            }
        }

        // === NEW: seed cross-area frontier points (used when area complete) ===
        public int SeedCrossAreaFrontier(int fromAreaId, Vector3 around, int count)
        {
            if (_chunker == null || _grid == null || count <= 0) return 0;

            int added = 0;
            int samples = Mathf.Max(12, count * 2);
            float rMin = Mathf.Max(2.0f, _core.searchMinHopDist * 1.5f);
            float rMax = Mathf.Max(rMin + 5f, _core.searchStepMaxDist * 1.5f);

            _seedSeen.Clear();

            float baseAng = Time.time * 57.29578f;
            for (int i = 0; i < samples && added < count; i++)
            {
                float ang = (baseAng + i * (360f / samples)) * Mathf.Deg2Rad;

                // try two radii per angle to “reach past” portals
                for (int pass = 0; pass < 2 && added < count; pass++)
                {
                    float t = (i + pass * 0.5f) / samples;
                    float dist = Mathf.Lerp(rMin, rMax, t);

                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector3 cand = around + (Vector3)(dir * dist);

                    var n = _grid.NodeFromWorldPoint(cand);
                    if (n == null || !n.walkable) continue;

                    int areaAtCand = _chunker.GetAreaIdStrict(cand);
                    if (areaAtCand == fromAreaId) continue; // want different area

                    if (!_core.searchAllowPortalTargets && _chunker.IsPortal(cand)) continue;

                    int key = n.gridY * _grid.gridSizeXPublic + n.gridX;
                    if (_seedSeen.Add(key))
                    {
                        _frontier.Add(n.worldPosition);
                        added++;
                    }
                }
            }

            return added;
        }

        // --- helpers ---
        public void MarkWorldSearched(Vector3 world)
        {
            if (_chunker == null || _searched == null) return;

            Vector2Int cell = WorldToCell(world);
            int gridW = _chunker.GridWidth;
            int gridH = _chunker.GridHeight;

            if (cell.x < 0 || cell.y < 0 || cell.x >= gridW || cell.y >= gridH) return;

            int idx = cell.y * gridW + cell.x;
            if (idx < 0 || idx >= _searched.Length) return;

            int areaId = _chunker.GetAreaIdStrict(world);
            if (areaId < 0) return;

            if (!_searched[idx])
            {
                _searched[idx] = true;

                if (_areaSearchedCount.TryGetValue(areaId, out int count))
                    _areaSearchedCount[areaId] = count + 1;
                else
                    _areaSearchedCount[areaId] = 1;
            }
        }

        private bool IsGoodCandidate(Vector3 from, Vector3 world)
        {
            float minDist = Mathf.Max(1.5f, _core.searchMinHopDist);
            if (Vector2.Distance(from, world) < minDist)
                return false;

            var node = _grid.NodeFromWorldPoint(world);
            if (node == null || !node.walkable) return false;
            if (!_grid.IsWalkableCached(node, 0f)) return false;

            if (!_core.searchAllowPortalTargets && _chunker != null && _chunker.IsPortal(world))
                return false;

            return true;
        }

        private void SeedFrontierAround(Vector3 center, int count)
        {
            if (count <= 0) return;

            float configMin = _core.searchMinHopDist;
            float rMin = Mathf.Max(2.0f, configMin * 1.5f);
            float rMax = Mathf.Max(rMin + 3f, _core.searchStepMaxDist * 1.2f);
            int samples = Mathf.Max(8, count);

            _seedSeen.Clear();

            float baseAng = Time.time * 57.29578f;
            for (int i = 0; i < samples; i++)
            {
                float t = (i + 0.382f) / samples;
                float ang = (baseAng + i * (360f / samples)) * Mathf.Deg2Rad;

                float distT = t * t * t;
                float dist = Mathf.Lerp(rMin, rMax, distT);

                Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                Vector3 cand = center + (Vector3)(dir * dist);

                var n = _grid.NodeFromWorldPoint(cand);
                if (n == null) continue;

                cand = n.worldPosition;

                if (_core.searchUseLOS)
                {
                    LayerMask mask = _core.searchOccluderMask.value != 0
                        ? _core.searchOccluderMask
                        : new LayerMask { value = Physics2D.DefaultRaycastLayers };

                    if (Physics2D.Linecast(center, cand, mask).collider != null)
                        continue;
                }

                int key = n.gridY * _grid.gridSizeXPublic + n.gridX;
                if (_seedSeen.Add(key))
                {
                    _frontier.Add(cand);
                    if (_frontier.Count >= count) break;
                }
            }
        }

        private Vector2Int WorldToCell(Vector2 world)
        {
            if (_chunker == null) return Vector2Int.zero;

            Vector2 origin = _chunker.GridOrigin;
            float cellSize = _chunker.CellSizePublic;

            int cx = Mathf.FloorToInt((world.x - origin.x) / cellSize);
            int cy = Mathf.FloorToInt((world.y - origin.y) / cellSize);

            return new Vector2Int(cx, cy);
        }

        private Vector2 CellCenterWorld(int cx, int cy)
        {
            if (_chunker == null) return Vector2.zero;

            Vector2 origin = _chunker.GridOrigin;
            float cellSize = _chunker.CellSizePublic;

            return new Vector2(origin.x + (cx + 0.5f) * cellSize,
                               origin.y + (cy + 0.5f) * cellSize);
        }
    }
}
