using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore
    {
        [SerializeField] private AreaChunker2D _cachedChunker;
        private AreaChunker2D Chunker
            => _cachedChunker
               ?? (_cachedChunker = GetComponent<AreaChunker2D>()
                                   ?? GetComponentInParent<AreaChunker2D>()
                                   ?? Object.FindObjectOfType<AreaChunker2D>(true));

        public void InitSearchMemory()
        {
            InitSearchMemoryIfNeeded();
        }

        public void InitSearchMemoryIfNeeded()
        {
            if (_searchMemory == null)
            {
                var ch = Chunker;
                if (grid == null || ch == null)
                {
                    Debug.LogWarning($"<color=yellow>[{name}]</color> Search init delayed (grid={(grid != null ? "OK" : "NULL")} chunker={(ch != null ? "OK" : "NULL")})");
                    return;
                }

                try
                {
                    _searchMemory = new SearchMemory(this, grid, ch);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"<color=red>[{name}]</color> SearchMemory init FAILED: {ex.Message}");
                }
            }
        }

        public void ResetSearchPlan(Vector3 anchor)
        {
            InitSearchMemoryIfNeeded();
            _searchMemory?.ResetPlan(anchor);
        }

        public void RefreshFrontierForPosition(Vector3 pos)
        {
            InitSearchMemoryIfNeeded();
            _searchMemory?.RefreshFrontierForPosition(pos);
        }

        public bool TryGetNextSearchPoint(Vector3 from, out Vector3 point)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory != null && _searchMemory.TryGetNextSearchPoint(from, out point))
                return true;

            var rnd = Random.insideUnitCircle.normalized;
            float r = Mathf.Lerp(searchLocalSampleRMin, searchLocalSampleRMax, Random.value);
            point = from + new Vector3(rnd.x, rnd.y, 0f) * r;
            return true;
        }

        public bool TryGetUnsearchedPointInAreaLocal(
            int areaId,
            Vector3 startWorld,
            out Vector3 pick,
            int maxExpansions)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory != null)
                return _searchMemory.TryGetUnsearchedPointInAreaLocal(areaId, startWorld, out pick, maxExpansions);

            pick = default;
            return false;
        }

        public float GetAreaCoverageLocal(int areaId)
        {
            InitSearchMemoryIfNeeded();
            return Mathf.Clamp01(_searchMemory?.GetAreaCoverage(areaId) ?? 0f);
        }

        public float GetTotalSearchCoverage()
        {
            InitSearchMemoryIfNeeded();
            return _searchMemory?.GetTotalCoverage() ?? 0f;
        }

        public int GetAreaWalkableNodesStrictCached(int areaId)
        {
            InitSearchMemoryIfNeeded();
            return _searchMemory?.GetAreaWalkableNodesStrictCached(areaId)
                   ?? (Chunker != null ? Chunker.GetAreaWalkableCount(areaId) : 0);
        }

        // === Quiet marking (removed budget spam) ===
        public void MarkSearchedConeBudgeted(Vector2 origin, Vector2 dir, float radius, float halfAngle)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory == null) return;

            if (!TryConsumeBudget(searchRaysPerSweep, out int granted) || granted <= 0)
                return;

            _searchMemory.MarkSearchedConeBudgeted(origin, dir, radius, halfAngle);
        }

        public void LogDetailedCoverageInfo()
        {
            InitSearchMemoryIfNeeded();
            _searchMemory?.LogCoverageDetail();
        }

        public void CopySearchedKeysSampleRecency(List<int> outKeys, int sampleBudget)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory == null) { outKeys?.Clear(); return; }
            _searchMemory.CopySearchedKeysSampleRecency(outKeys, sampleBudget);
        }

        public void CopySearchedKeysSample(List<int> outKeys, int sampleBudget)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory == null) { outKeys?.Clear(); return; }
            _searchMemory.CopySearchedKeysSample(outKeys, sampleBudget);
        }

        public bool TryWorldForKey(int flatKey, out Vector3 world)
        {
            world = default;
            if (grid == null) return false;

            int w = grid.gridSizeXPublic;
            if (w <= 0) return false;
            int gx = flatKey % w;
            int gy = flatKey / w;

            var n = grid.GetNodeByGridCoords(gx, gy);
            if (n == null) return false;
            world = n.worldPosition;
            return true;
        }

        public void IngestSearchedKeys(List<int> flatKeys, List<Vector3> worlds, float recencyBoost)
        {
            InitSearchMemoryIfNeeded();
            _searchMemory?.IngestSearchedKeys(flatKeys, worlds, recencyBoost);
        }

        public bool TryGetAreaHitCount(int areaId, out int hits)
        {
            InitSearchMemoryIfNeeded();
            if (_searchMemory != null)
                return _searchMemory.TryGetAreaHits(areaId, out hits);

            hits = 0;
            return false;
        }

        public int GetTotalMarkedCells()
        {
            InitSearchMemoryIfNeeded();
            return _searchMemory?.TotalMarkedCells ?? 0;
        }

        // === NEW: thin shim so SearchState can request cross-area frontier ===
        public int BuildCrossAreaFrontier(int fromAreaId, Vector3 around, int count)
        {
            InitSearchMemoryIfNeeded();
            return _searchMemory != null ? _searchMemory.SeedCrossAreaFrontier(fromAreaId, around, count) : 0;
        }
    }
}
