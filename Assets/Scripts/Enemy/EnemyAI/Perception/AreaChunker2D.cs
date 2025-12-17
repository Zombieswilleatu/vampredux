// ═════════════════════════════════════════════════════════════════════════════
// FIXED: AreaChunker2D.cs - Added public accessors for origin and cell size
// ═════════════════════════════════════════════════════════════════════════════
// -----------------------------
// File: EnemyAI/Perception/AreaChunker2D.cs
// Purpose: Partition walkable space into areas, detect portals (narrow runs),
//          build an area adjacency graph + portal anchors, and expose helpers
//          for fast "next-hop" toward a target area.
// MAJOR FIX: Coverage tracking completely removed from AreaChunker2D
//            - AreaChunker is now TOPOLOGY ONLY (areas, portals, adjacency)
//            - Coverage is handled entirely by SearchMemory
//            - No more dual tracking, no more coverage bugs
// NEW: Exposed GridOrigin and CellSizePublic for SearchMemory coordinate sync
// -----------------------------
using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class AreaChunker2D : MonoBehaviour
    {
        [Header("Bounds")]
        [Tooltip("If empty, bounds are built automatically from scene objects.")]
        public BoxCollider2D boundsSource;
        [Tooltip("Auto-build a BoxCollider2D if boundsSource is not set.")]
        public bool autoCreateBounds = true;
        [Tooltip("Layers scanned when auto-creating bounds (tilemaps, floor, walls, props, etc.).")]
        public LayerMask boundsFromLayers = ~0;

        [Header("Grid & Obstacles")]
        [Min(0.1f)] public float cellSize = 0.5f;
        [Tooltip("Layers considered solid (block movement / LOS).")]
        public LayerMask obstacleMask;

        [Header("Portal (narrow passage) detection")]
        [Tooltip("Cells with local min free width <= this (world units) are treated as 'portals' and cut areas.")]
        public float portalMaxWidth = 1.2f;
        [Tooltip("Min corridor length (cells) required before a narrow run counts as a portal.")]
        [Min(1)] public int portalMinRunCells = 2;

        [Header("Debug & Logging")]
        public bool verbose = true;
        [Tooltip("Draw gizmos even when the GameObject is not selected.")]
        public bool drawDebugAlways = false;
        public bool drawDebug = false;
        [Range(0, 1)] public float debugAlpha = 0.25f;
        public Color areaA = new Color(0.2f, 0.8f, 1f, 0.15f);
        public Color areaB = new Color(1f, 0.8f, 0.2f, 0.15f);
        public Color portalColor = new Color(1f, 0f, 0f, 0.50f);

        // --- Grid ---
        private Vector2 _origin;
        private Vector2Int _size;
        private float _cs;

        private bool[] _walk;
        private bool[] _portal;
        private int[] _areaId;

        private struct AreaInfo { public int total; public List<int> cells; }
        private readonly List<AreaInfo> _areas = new List<AreaInfo>(64);

        // Adjacency graph
        private List<int>[] _areaAdj;

        // Portal anchors
        private readonly Dictionary<long, List<Vector2>> _portalAnchorsByPair = new Dictionary<long, List<Vector2>>(128);

        private bool _built;

        // Scratch for builds
        private readonly Queue<int> _floodQ = new Queue<int>(1024);

        // Scratch for runtime BFS
        private readonly Queue<int> _bfsQ = new Queue<int>(1024);
        private int[] _visitStamp;
        private int _visitTick;

        // --- Public readouts ---
        public bool IsBuilt => _built;
        public int GridWidth => _size.x;
        public int GridHeight => _size.y;
        public int AreaCount => _areas.Count;
        public int WalkableCount { get; private set; }
        public int PortalCount { get; private set; }

        // NEW: Public accessors for coordinate system sync
        public Vector2 GridOrigin => _origin;
        public float CellSizePublic => _cs;

        // ---------- Unity lifecycle ----------

        private void OnEnable()
        {
            if (!_built && (Application.isPlaying || drawDebug || drawDebugAlways))
                SafeEnsureBuilt();
        }

        private void OnValidate()
        {
            // CRITICAL: FIRST LINE - block ALL validation during play mode
            if (Application.isPlaying)
            {
                Debug.LogWarning($"<color=yellow>[AreaChunker2D]</color> OnValidate blocked during play mode", this);
                return; // EXIT IMMEDIATELY
            }

            // Editor-only validation
            cellSize = Mathf.Max(0.1f, cellSize);
            portalMinRunCells = Mathf.Max(1, portalMinRunCells);

            if (drawDebug || drawDebugAlways)
                SafeEnsureBuilt();
        }

        // ---------- Public API ----------

        public void EnsureBuilt()
        {
            if (_built) return;
            Build();
        }

        public void SafeEnsureBuilt()
        {
            try { EnsureBuilt(); }
            catch (System.Exception ex)
            {
                Debug.LogError($"[{name}] AreaChunker2D build error: {ex.Message}\n{ex.StackTrace}", this);
            }
        }

        /// <summary>Area id for position. If on a portal, returns the first neighboring area (or -1).</summary>
        public int GetAreaId(Vector2 worldPos)
        {
            EnsureBuilt();
            if (!WorldToIndex(worldPos, out int idx)) return -1;
            int id = _areaId[idx];
            if (id >= 0) return id;

            if (_walk[idx] && _portal[idx])
            {
                var c = IndexToCell(idx);
                int idN = NeighborArea(c.x + 1, c.y); if (idN >= 0) return idN;
                idN = NeighborArea(c.x - 1, c.y); if (idN >= 0) return idN;
                idN = NeighborArea(c.x, c.y + 1); if (idN >= 0) return idN;
                idN = NeighborArea(c.x, c.y - 1); if (idN >= 0) return idN;
            }
            return -1;
        }

        public int GetAreaId(Vector3 worldPos) => GetAreaId((Vector2)worldPos);

        /// <summary>Strict area id for position. Returns -1 for walls, out of bounds, and portals.</summary>
        public int GetAreaIdStrict(Vector2 worldPos)
        {
            EnsureBuilt();
            if (!WorldToIndex(worldPos, out int idx)) return -1;
            if (!_walk[idx] || (_portal != null && _portal[idx])) return -1;
            return _areaId[idx];
        }

        public int GetAreaIdStrict(Vector3 worldPos) => GetAreaIdStrict((Vector2)worldPos);

        /// <summary>True if the cell at worldPos is tagged as a portal.</summary>
        public bool IsPortal(Vector2 worldPos)
        {
            EnsureBuilt();
            if (!WorldToIndex(worldPos, out int idx)) return false;
            return _portal != null && _portal[idx];
        }
        public bool IsPortal(Vector3 worldPos) => IsPortal((Vector2)worldPos);

        /// <summary>Total walkable cells in an area (0 if invalid).</summary>
        public int GetAreaWalkableCount(int areaId)
        {
            EnsureBuilt();
            if (areaId < 0 || areaId >= _areas.Count) return 0;
            return _areas[areaId].total;
        }

        /// <summary>Copy sampled world points from an area (runtime-friendly).</summary>
        public void CopyAreaWorldPointsSampled(int areaId, List<Vector2> target, int sampleBudget, int seed = -1)
        {
            EnsureBuilt();
            if (target == null) return;
            if (areaId < 0 || areaId >= _areas.Count) return;

            var info = _areas[areaId];
            int count = info.cells.Count;
            if (count == 0) return;

            int budget = Mathf.Max(1, sampleBudget);
            int stride = Mathf.Max(1, count / budget);
            int start = (seed >= 0) ? (seed % stride) : Random.Range(0, stride);

            for (int i = start; i < count; i += stride)
            {
                int idx = info.cells[i];
                Vector2 w = CellCenterWorld(idx);
                target.Add(w);
            }
        }

        public void CopyAreaWorldPointsSampled(int areaId, List<Vector3> target, int sampleBudget, int seed = -1)
        {
            EnsureBuilt();
            if (target == null) return;
            if (areaId < 0 || areaId >= _areas.Count) return;

            var info = _areas[areaId];
            int count = info.cells.Count;
            if (count == 0) return;

            int budget = Mathf.Max(1, sampleBudget);
            int stride = Mathf.Max(1, count / budget);
            int start = (seed >= 0) ? (seed % stride) : Random.Range(0, stride);

            for (int i = start; i < count; i += stride)
            {
                int idx = info.cells[i];
                Vector2 w = CellCenterWorld(idx);
                target.Add(new Vector3(w.x, w.y, 0f));
            }
        }

        /// <summary>Find nearest unsearched point in area using external searched array.</summary>
        public bool TryGetUnsearchedPointInArea(int areaId, Vector2 from, bool[] searchedArray, out Vector3 point, int maxExpansions = 2048)
        {
            EnsureBuilt();
            point = default;

            if (areaId < 0 || areaId >= _areas.Count) return false;
            if (searchedArray == null) return false;

            int n = _size.x * _size.y;
            if (searchedArray.Length != n) return false;

            _visitStamp ??= new int[n];
            if (_visitStamp.Length != n) _visitStamp = new int[n];
            unchecked { _visitTick++; if (_visitTick == 0) { System.Array.Clear(_visitStamp, 0, _visitStamp.Length); _visitTick = 1; } }

            if (!WorldToIndex(from, out int startIdx))
            {
                WorldToCellClamped(from, out var c);
                startIdx = CellToIndex(c);
            }

            _bfsQ.Clear();
            EnqueueVisit(startIdx);

            while (_bfsQ.Count > 0 && maxExpansions-- > 0)
            {
                int i = _bfsQ.Dequeue();
                if (!_walk[i]) continue;

                if (!_portal[i] && _areaId[i] == areaId)
                {
                    if (!searchedArray[i])
                    {
                        Vector2 w = CellCenterWorld(i);
                        point = new Vector3(w.x, w.y, 0f);
                        return true;
                    }
                }

                var c = IndexToCell(i);
                TryEnqueue(c.x + 1, c.y);
                TryEnqueue(c.x - 1, c.y);
                TryEnqueue(c.x, c.y + 1);
                TryEnqueue(c.x, c.y - 1);
            }

            return false;

            void TryEnqueue(int cx, int cy)
            {
                if (cx < 0 || cy < 0 || cx >= _size.x || cy >= _size.y) return;
                int ni = CellToIndex(new Vector2Int(cx, cy));
                if (_visitStamp[ni] == _visitTick) return;
                if (!_walk[ni]) return;
                EnqueueVisit(ni);
            }

            void EnqueueVisit(int idx)
            {
                _visitStamp[idx] = _visitTick;
                _bfsQ.Enqueue(idx);
            }
        }

        // ---------- Public: portal helpers ----------

        public bool TryGetPortalStep(int fromAreaId, int toAreaId, Vector2 origin, out Vector3 step)
        {
            EnsureBuilt();
            step = default;
            if (fromAreaId < 0 || toAreaId < 0 || fromAreaId == toAreaId) return false;
            if (fromAreaId >= _areas.Count || toAreaId >= _areas.Count) return false;

            if (_areaAdj == null || _areaAdj.Length != _areas.Count) return false;

            var nbrs = _areaAdj[fromAreaId];
            bool adjacent = false;
            if (nbrs != null)
            {
                for (int i = 0; i < nbrs.Count; i++)
                    if (nbrs[i] == toAreaId) { adjacent = true; break; }
            }
            if (!adjacent) return false;

            long key = PairKey(fromAreaId, toAreaId);
            if (!_portalAnchorsByPair.TryGetValue(key, out var anchors) || anchors == null || anchors.Count == 0)
                return false;

            float bestD2 = float.MaxValue;
            Vector2 best = default;
            for (int i = 0; i < anchors.Count; i++)
            {
                float d2 = (anchors[i] - origin).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = anchors[i]; }
            }
            step = new Vector3(best.x, best.y, 0f);
            return true;
        }

        public bool TryGetNextPortalToward(int fromAreaId, int targetAreaId, Vector2 origin, out Vector3 step)
        {
            EnsureBuilt();
            step = default;
            if (_areas == null || _areaAdj == null) return false;
            if (fromAreaId < 0 || targetAreaId < 0 || fromAreaId == targetAreaId) return false;
            int areaCount = _areas.Count;
            if (fromAreaId >= areaCount || targetAreaId >= areaCount) return false;

            var prev = new int[areaCount];
            var visited = new bool[areaCount];
            for (int i = 0; i < areaCount; i++) prev[i] = -1;
            var q = new Queue<int>();
            visited[fromAreaId] = true;
            q.Enqueue(fromAreaId);

            while (q.Count > 0)
            {
                int a = q.Dequeue();
                var nbrs = _areaAdj[a];
                if (nbrs == null) continue;
                for (int i = 0; i < nbrs.Count; i++)
                {
                    int nb = nbrs[i];
                    if (visited[nb]) continue;
                    visited[nb] = true;
                    prev[nb] = a;
                    if (nb == targetAreaId) { q.Clear(); break; }
                    q.Enqueue(nb);
                }
            }

            if (!visited[targetAreaId]) return false;

            int cur = targetAreaId;
            int firstHop = targetAreaId;
            while (prev[cur] != -1 && prev[cur] != fromAreaId)
            {
                cur = prev[cur];
                firstHop = cur;
            }
            if (prev[targetAreaId] == fromAreaId) firstHop = targetAreaId;

            long key = PairKey(fromAreaId, firstHop);
            if (!_portalAnchorsByPair.TryGetValue(key, out var anchors) || anchors == null || anchors.Count == 0)
                return false;

            float bestD2 = float.MaxValue;
            Vector2 best = default;
            for (int i = 0; i < anchors.Count; i++)
            {
                float d2 = (anchors[i] - origin).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = anchors[i]; }
            }
            step = new Vector3(best.x, best.y, 0f);
            return true;
        }

        // ---------- Build ----------

        [ContextMenu("Force Rebuild")]
        public void ForceRebuild()
        {
            _built = false;
            EnsureBuilt();
            DumpSummary();
        }

        [ContextMenu("Dump Summary")]
        public void DumpSummary()
        {
            if (!_built)
            {
                Debug.LogWarning($"[{name}] AreaChunker2D not built.", this);
                return;
            }
            var b = boundsSource != null ? boundsSource.bounds : new Bounds();
            Debug.Log(
                $"[{name}] AreaChunker2D SUMMARY\n" +
                $"- Bounds center={b.center:F2} size={b.size:F2}\n" +
                $"- Grid: {_size.x} x {_size.y} (cellSize={_cs})\n" +
                $"- Origin={_origin:F2}\n" +
                $"- Walkable={WalkableCount}  Portals={PortalCount}  Areas={_areas.Count}",
                this
            );
        }

        private void Build()
        {
            if (Application.isPlaying && _built)
            {
                Debug.LogWarning($"<color=yellow>[AreaChunker2D]</color> Build blocked during play mode (already built)", this);
                return;
            }

            if (boundsSource == null && autoCreateBounds)
                boundsSource = CreateBoundsFromScene();

            if (boundsSource == null)
            {
                if (verbose)
                    Debug.LogWarning($"[{name}] AreaChunker2D: No boundsSource; auto-create failed. Aborting build.", this);
                return;
            }

            Bounds b = boundsSource.bounds;
            _origin = b.min;
            _cs = Mathf.Max(0.1f, cellSize);

            _size = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(b.size.x / _cs)),
                Mathf.Max(1, Mathf.CeilToInt(b.size.y / _cs))
            );

            int n = _size.x * _size.y;
            _walk = new bool[n];
            _portal = new bool[n];
            _areaId = new int[n];
            _visitStamp = new int[n];

            WalkableCount = 0;
            PortalCount = 0;

            for (int i = 0; i < n; i++)
            {
                Vector2 w = CellCenterWorld(i);
                bool free = !Physics2D.OverlapPoint(w, obstacleMask);
                _walk[i] = free;
                _areaId[i] = -1;
                if (free) WalkableCount++;
            }

            float maxW = Mathf.Max(_cs, portalMaxWidth);
            int widthCells = Mathf.CeilToInt(maxW / _cs);

            var hRun = new int[n];
            var vRun = new int[n];

            for (int y = 0; y < _size.y; y++)
            {
                int run = 0;
                for (int x = 0; x < _size.x; x++)
                {
                    int i = y * _size.x + x;
                    run = _walk[i] ? run + 1 : 0;
                    hRun[i] = run;
                }
                run = 0;
                for (int x = _size.x - 1; x >= 0; x--)
                {
                    int i = y * _size.x + x;
                    run = _walk[i] ? run + 1 : 0;
                    if (_walk[i]) hRun[i] = Mathf.Min(hRun[i] + run - 1, 9999);
                }
            }

            for (int x = 0; x < _size.x; x++)
            {
                int run = 0;
                for (int y = 0; y < _size.y; y++)
                {
                    int i = y * _size.x + x;
                    run = _walk[i] ? run + 1 : 0;
                    vRun[i] = run;
                }
                run = 0;
                for (int y = _size.y - 1; y >= 0; y--)
                {
                    int i = y * _size.x + x;
                    run = _walk[i] ? run + 1 : 0;
                    if (_walk[i]) vRun[i] = Mathf.Min(vRun[i] + run - 1, 9999);
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (!_walk[i]) { _portal[i] = false; continue; }
                int minSpanCells = Mathf.Min(hRun[i], vRun[i]);
                bool narrow = minSpanCells <= widthCells;

                if (narrow)
                {
                    var c = IndexToCell(i);
                    int runH = CountRun(c.x, c.y, 1, 0) + CountRun(c.x, c.y, -1, 0) - 1;
                    int runV = CountRun(c.x, c.y, 0, 1) + CountRun(c.x, c.y, 0, -1) - 1;
                    if (Mathf.Max(runH, runV) >= portalMinRunCells)
                    {
                        _portal[i] = true;
                        PortalCount++;
                    }
                }
            }

            _areas.Clear();
            int nextArea = 0;
            for (int i = 0; i < n; i++)
            {
                if (!_walk[i] || _portal[i] || _areaId[i] != -1) continue;
                FloodArea(i, nextArea++);
            }

            BuildAdjacencyAndAnchors();

            _built = true;

            if (verbose)
            {
                Debug.Log(
                    $"[{name}] AreaChunker2D built.\n" +
                    $"- Bounds: center={b.center:F2}, size={b.size:F2}\n" +
                    $"- Origin: {_origin:F2}\n" +
                    $"- Grid: {_size.x}x{_size.y}  cell={_cs}\n" +
                    $"- Walkable={WalkableCount}  Portals={PortalCount}  Areas={_areas.Count}",
                    this
                );
            }
        }

        private void FloodArea(int seed, int id)
        {
            var info = new AreaInfo { total = 0, cells = new List<int>(256) };
            _floodQ.Clear();
            _floodQ.Enqueue(seed);
            _areaId[seed] = id;

            while (_floodQ.Count > 0)
            {
                int i = _floodQ.Dequeue();
                info.total++;
                info.cells.Add(i);

                var c = IndexToCell(i);
                TryAdd(c.x + 1, c.y);
                TryAdd(c.x - 1, c.y);
                TryAdd(c.x, c.y + 1);
                TryAdd(c.x, c.y - 1);
            }

            _areas.Add(info);

            void TryAdd(int cx, int cy)
            {
                if (cx < 0 || cy < 0 || cx >= _size.x || cy >= _size.y) return;
                int ni = CellToIndex(new Vector2Int(cx, cy));
                if (!_walk[ni] || _portal[ni] || _areaId[ni] != -1) return;
                _areaId[ni] = id;
                _floodQ.Enqueue(ni);
            }
        }

        private void BuildAdjacencyAndAnchors()
        {
            int areaCount = _areas.Count;
            _areaAdj = new List<int>[areaCount];
            for (int i = 0; i < areaCount; i++) _areaAdj[i] = new List<int>(4);
            _portalAnchorsByPair.Clear();

            for (int y = 0; y < _size.y; y++)
            {
                for (int x = 0; x < _size.x; x++)
                {
                    int i = CellToIndex(new Vector2Int(x, y));
                    if (!_walk[i] || !_portal[i]) continue;

                    int neighAreasCount = 0;
                    int aUp = NeighborArea(x, y + 1);
                    int aDn = NeighborArea(x, y - 1);
                    int aRt = NeighborArea(x + 1, y);
                    int aLt = NeighborArea(x - 1, y);

                    int[] areas = new int[4];
                    Vector2[] anchors = new Vector2[4];

                    void AddArea(int aid, int cx, int cy)
                    {
                        if (aid < 0) return;
                        for (int k = 0; k < neighAreasCount; k++)
                            if (areas[k] == aid) return;
                        areas[neighAreasCount] = aid;
                        anchors[neighAreasCount] = CellCenterWorld(CellToIndex(new Vector2Int(cx, cy)));
                        neighAreasCount++;
                    }

                    AddArea(aUp, x, y + 1);
                    AddArea(aDn, x, y - 1);
                    AddArea(aRt, x + 1, y);
                    AddArea(aLt, x - 1, y);

                    if (neighAreasCount < 2) continue;

                    for (int p = 0; p < neighAreasCount; p++)
                        for (int q = p + 1; q < neighAreasCount; q++)
                        {
                            int a = areas[p];
                            int b = areas[q];
                            if (a == b || a < 0 || b < 0) continue;

                            if (!Contains(_areaAdj[a], b)) _areaAdj[a].Add(b);
                            if (!Contains(_areaAdj[b], a)) _areaAdj[b].Add(a);

                            long key = PairKey(a, b);
                            if (!_portalAnchorsByPair.TryGetValue(key, out var list))
                            {
                                list = new List<Vector2>(8);
                                _portalAnchorsByPair[key] = list;
                            }

                            list.Add(anchors[p]);
                            list.Add(anchors[q]);
                        }
                }
            }

            static bool Contains(List<int> list, int val)
            {
                for (int i = 0; i < list.Count; i++) if (list[i] == val) return true;
                return false;
            }
        }

        private BoxCollider2D CreateBoundsFromScene()
        {
            bool found = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);

            var cols = FindObjectsOfType<Collider2D>();
            foreach (var c in cols)
            {
                if (((1 << c.gameObject.layer) & boundsFromLayers.value) == 0) continue;
                if (!found) { b = c.bounds; found = true; }
                else b.Encapsulate(c.bounds);
            }

            var rends = FindObjectsOfType<Renderer>();
            foreach (var r in rends)
            {
                if (((1 << r.gameObject.layer) & boundsFromLayers.value) == 0) continue;
                if (!found) { b = r.bounds; found = true; }
                else b.Encapsulate(r.bounds);
            }

            if (!found)
            {
                if (verbose) Debug.LogWarning($"[{name}] AreaChunker2D: Could not infer bounds from scene.", this);
                return null;
            }

            var go = new GameObject("AreaChunker_AutoBounds");
            go.transform.SetParent(transform);
            go.transform.position = b.center;

            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.offset = Vector2.zero;
            box.size = b.size;

            if (verbose)
                Debug.Log($"[{name}] AreaChunker2D: Auto-created bounds at {b.center:F2} size {b.size:F2}.", this);

            return box;
        }

        // ---------- Helpers ----------

        private bool WorldToIndex(Vector2 w, out int idx)
        {
            var c = new Vector2Int(
                Mathf.FloorToInt((w.x - _origin.x) / _cs),
                Mathf.FloorToInt((w.y - _origin.y) / _cs)
            );
            if (c.x < 0 || c.y < 0 || c.x >= _size.x || c.y >= _size.y) { idx = -1; return false; }
            idx = c.y * _size.x + c.x; return true;
        }

        private void WorldToCellClamped(Vector2 w, out Vector2Int c)
        {
            c = new Vector2Int(
                Mathf.FloorToInt((w.x - _origin.x) / _cs),
                Mathf.FloorToInt((w.y - _origin.y) / _cs)
            );
            c.x = Mathf.Clamp(c.x, 0, _size.x - 1);
            c.y = Mathf.Clamp(c.y, 0, _size.y - 1);
        }

        private int CellToIndex(Vector2Int c) => c.y * _size.x + c.x;

        private Vector2Int IndexToCell(int i)
        {
            int y = i / _size.x; int x = i - y * _size.x;
            return new Vector2Int(x, y);
        }

        private Vector2 CellCenterWorld(int i)
        {
            var c = IndexToCell(i);
            return new Vector2(_origin.x + (c.x + 0.5f) * _cs,
                               _origin.y + (c.y + 0.5f) * _cs);
        }

        private int NeighborArea(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= _size.x || cy >= _size.y) return -1;
            int i = CellToIndex(new Vector2Int(cx, cy));
            if (!_walk[i] || _portal[i]) return -1;
            return _areaId[i];
        }

        private int CountRun(int cx, int cy, int dx, int dy)
        {
            int run = 0;
            while (cx >= 0 && cy >= 0 && cx < _size.x && cy < _size.y)
            {
                int i = CellToIndex(new Vector2Int(cx, cy));
                if (!_walk[i]) break;
                run++; cx += dx; cy += dy;
            }
            return run;
        }

        private static long PairKey(int a, int b)
        {
            if (a > b) { int t = a; a = b; b = t; }
            return ((long)(uint)a << 32) | (uint)b;
        }

        // ---------- Gizmos ----------

        private void OnDrawGizmos()
        {
            if (!drawDebugAlways) return;
            DrawGizmosInternal();
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawDebug) return;
            DrawGizmosInternal();
        }

        private void DrawGizmosInternal()
        {
            if (!_built || _areaId == null) return;

            for (int i = 0; i < _areaId.Length; i++)
            {
                if (_walk == null || !_walk[i]) continue;

                Vector2 w = CellCenterWorld(i);
                Vector3 size = new Vector3(_cs, _cs, 0f);

                if (_portal != null && _portal[i])
                {
                    Gizmos.color = new Color(portalColor.r, portalColor.g, portalColor.b, debugAlpha);
                    Gizmos.DrawCube(new Vector3(w.x, w.y, 0f), size);
                    continue;
                }

                int area = _areaId[i];
                Color baseCol = ((area & 1) == 0) ? areaA : areaB;
                baseCol.a = debugAlpha;

                Gizmos.color = baseCol;
                Gizmos.DrawCube(new Vector3(w.x, w.y, 0f), size);
            }
        }
    }
}