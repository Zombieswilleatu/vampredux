using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// High-performance, clearance-aware grid with layered caching and asynchronous prewarm.
/// Implements IPathfindingCache so the pathfinder can invalidate caches directly (no reflection).
/// FIXED: Grid coordinate system now EXACTLY matches AreaChunker2D for perfect alignment.
/// </summary>
[ExecuteAlways]
public class Grid : MonoBehaviour, IPathfindingCache
{
    // ===========================
    // Inspector
    // ===========================
    [Header("Collision / Layout")]
    public LayerMask unwalkableMask;
    public Vector2 gridWorldSize = new Vector2(32, 32);
    [Min(0.05f)] public float nodeRadius = 0.5f;

    [Header("Bounds Source (must match AreaChunker2D)")]
    [Tooltip("MUST be the same BoxCollider2D used by AreaChunker2D for coordinate alignment")]
    public BoxCollider2D boundsSource;
    [Tooltip("Auto-find bounds if not set")]
    public bool autoFindBounds = true;

    [Header("Gizmos")]
    public bool showGridGizmos = true;
    public bool showGridBoundaryAlways = true;

    [Header("Caching (fine grid)")]
    [Tooltip("Cache walkability checks to reduce Physics2D calls.")]
    public bool useWalkabilityCache = true;

    [Tooltip("Seconds a cached result remains valid (runtime).")]
    public float cacheLifetime = 2.0f;

    [Tooltip("Maximum cached entries before an emergency clear.")]
    public int maxCacheEntries = 1000;

    [Header("Prewarm (runtime only)")]
    [Tooltip("Kick off a background pass at Start() to fill neighbour masks (and walkability) for these clearances.")]
    public bool prewarmOnStart = false;

    [Tooltip("Clearance radii to prewarm. Include 0 for baked-sized walkers.")]
    public float[] prewarmClearances = new float[] { 0f, 0.5f, 1.0f };

    [Tooltip("How many cells to process per frame while prewarming.")]
    public int prewarmCellsPerFrame = 6000;

    [Tooltip("Log progress while prewarming.")]
    public bool prewarmLog = false;

    // ===========================
    // Internal grid - COORDINATE SYSTEM MATCHES AreaChunker2D
    // ===========================
    private Node[,] grid;
    private float nodeDiameter;
    private int gridSizeX, gridSizeY;

    // CRITICAL: Use same coordinate system as AreaChunker2D
    private Vector2 _origin; // bounds.min (same as AreaChunker2D._origin)
    private float _cellSize; // same as AreaChunker2D._cs

    // Public readouts (used by pathfinding)
    public int gridSizeXPublic => gridSizeX;
    public int gridSizeYPublic => gridSizeY;

    // ===========================
    // Walkability cache (world-pos + radius)
    // ===========================
    private struct CachedWalkability
    {
        public bool walkable;
        public float timestamp; // Time.time of write
        public CachedWalkability(bool w, float t) { walkable = w; timestamp = t; }
    }

    // Quantized cache key: pack x,y,r into a 64-bit integer
    private static long MakeWalkKey(Vector3 position, float clearanceRadius)
    {
        int qx = Mathf.RoundToInt(position.x * 4f);        // 0.25 precision
        int qy = Mathf.RoundToInt(position.y * 4f);
        int qr = Mathf.RoundToInt(clearanceRadius * 10f);  // 0.1 precision
        unchecked
        {
            return ((long)(qx & 0xFFFFF) << 44)  // 20 bits
                 | ((long)(qy & 0xFFFFF) << 24)  // 20 bits
                 | ((long)(qr & 0xFFFFFF));      // 24 bits
        }
    }

    private Dictionary<long, CachedWalkability> walkabilityCache; // lazily created
    private float lastCacheCleanup = 0f;
    private const float CACHE_CLEANUP_INTERVAL = 5f;

    // ===========================
    // Neighbour-mask cache (per cell + clearance bucket, 8 bits for 8 neighbours)
    // ===========================
    // bit order matches loops: dx=-1..1, dy=-1..1 skipping center
    private readonly Dictionary<long, byte> neighbourMaskCache = new Dictionary<long, byte>(4096);

    private int ClearanceBucket(float r) => Mathf.Max(0, Mathf.CeilToInt(r / (nodeRadius + 1e-6f)));

    private static long MakeMaskKey(int gx, int gy, int bucket)
    {
        unchecked
        {
            // 16 bits each (safe for typical grid sizes)
            return ((long)(gx & 0xFFFF) << 32)
                 | ((long)(gy & 0xFFFF) << 16)
                 | (long)(bucket & 0xFFFF);
        }
    }

    // ===========================
    // Unity lifecycle
    // ===========================
    private void Awake()
    {
        nodeDiameter = nodeRadius * 2f;
        _cellSize = nodeDiameter; // Match AreaChunker2D naming

        // Auto-find bounds if needed
        if (boundsSource == null && autoFindBounds)
        {
            boundsSource = FindBoundsSource();
        }

        if (boundsSource == null)
        {
            Debug.LogError($"[Grid] No boundsSource found! Grid coordinate system will be misaligned with AreaChunker2D.", this);
            // Fallback to old behavior for backwards compatibility
            SetupGridFromOldSystem();
        }
        else
        {
            // CRITICAL: Use EXACT same coordinate system as AreaChunker2D
            SetupGridFromBounds();
        }

        Debug.Log($"[Grid] Grid dimensions: {gridSizeX}x{gridSizeY} (cellSize={_cellSize}, worldSize=({gridSizeX * _cellSize:F2}, {gridSizeY * _cellSize:F2}))");

        if (useWalkabilityCache && walkabilityCache == null)
            walkabilityCache = new Dictionary<long, CachedWalkability>(2048);

        CreateGrid();
    }

    private BoxCollider2D FindBoundsSource()
    {
        // Look for AreaChunker2D component and use its bounds
        var areaChunker = GetComponent<EnemyAI.AreaChunker2D>()
                         ?? GetComponentInParent<EnemyAI.AreaChunker2D>()
                         ?? FindObjectOfType<EnemyAI.AreaChunker2D>();

        if (areaChunker != null)
        {
            var boundsField = areaChunker.GetType().GetField("boundsSource",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (boundsField != null)
            {
                var bounds = boundsField.GetValue(areaChunker) as BoxCollider2D;
                if (bounds != null)
                {
                    Debug.Log($"[Grid] Found boundsSource from AreaChunker2D: {bounds.name}", this);
                    return bounds;
                }
            }
        }

        // Fallback: look for any BoxCollider2D in scene
        var allBoxColliders = FindObjectsOfType<BoxCollider2D>();
        foreach (var box in allBoxColliders)
        {
            if (box.name.Contains("Bounds") || box.name.Contains("Area"))
            {
                Debug.Log($"[Grid] Auto-found potential boundsSource: {box.name}", this);
                return box;
            }
        }

        return null;
    }

    private void SetupGridFromBounds()
    {
        Bounds b = boundsSource.bounds;
        _origin = b.min; // EXACT same as AreaChunker2D._origin

        // EXACT same calculation as AreaChunker2D
        gridSizeX = Mathf.Max(1, Mathf.CeilToInt(b.size.x / _cellSize));
        gridSizeY = Mathf.Max(1, Mathf.CeilToInt(b.size.y / _cellSize));
    }

    private void SetupGridFromOldSystem()
    {
        // Fallback to old behavior (will cause coordinate mismatch with AreaChunker2D)
        gridSizeX = Mathf.Max(1, Mathf.CeilToInt(gridWorldSize.x / nodeDiameter));
        gridSizeY = Mathf.Max(1, Mathf.CeilToInt(gridWorldSize.y / nodeDiameter));

        _origin = (Vector2)transform.position - gridWorldSize * 0.5f;
        Debug.LogWarning($"[Grid] Using fallback coordinate system - may not align with AreaChunker2D!", this);
    }

    private void Start()
    {
        // Optional background prewarm (runtime only)
        if (Application.isPlaying && prewarmOnStart && prewarmClearances != null && prewarmClearances.Length > 0)
        {
            StartCoroutine(PrewarmNeighbourMasksAsync(prewarmClearances, prewarmCellsPerFrame));
        }
    }

    private void Update()
    {
        // Periodic cache cleanup (runtime only)
        if (!useWalkabilityCache || !Application.isPlaying) return;

        if (Time.time - lastCacheCleanup > CACHE_CLEANUP_INTERVAL)
        {
            CleanupCaches();
            lastCacheCleanup = Time.time;
        }
    }

    // ===========================
    // Build / rebuild
    // ===========================
    private void CreateGrid()
    {
        grid = new Node[gridSizeX, gridSizeY];

        // CRITICAL: Use same origin as AreaChunker2D (bounds.min)
        Vector3 worldBottomLeft = new Vector3(_origin.x, _origin.y, 0f);

        // One-time baked baseline walkability (used for gizmos; dynamic queries go through cache)
        float bakedBuffer = nodeRadius * 1.5f;

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int y = 0; y < gridSizeY; y++)
            {
                // EXACT same cell center calculation as AreaChunker2D
                Vector3 worldPoint = worldBottomLeft +
                    Vector3.right * (x + 0.5f) * _cellSize +
                    Vector3.up * (y + 0.5f) * _cellSize;

                bool walkable = !Physics2D.OverlapCircle(worldPoint, bakedBuffer, unwalkableMask);
                grid[x, y] = new Node(walkable, worldPoint, x, y);
            }
        }

        // Fresh grid → flush caches so dynamic results recompute
        walkabilityCache?.Clear();
        neighbourMaskCache.Clear();
    }

    // ===========================
    // Public API — base lookups (baked index & position)
    // ===========================
    public Node NodeFromWorldPoint(Vector3 worldPosition)
    {
        // EXACT same calculation as AreaChunker2D.WorldToIndex
        Vector2Int cellCoords = new Vector2Int(
            Mathf.FloorToInt((worldPosition.x - _origin.x) / _cellSize),
            Mathf.FloorToInt((worldPosition.y - _origin.y) / _cellSize)
        );

        // Clamp to valid range
        cellCoords.x = Mathf.Clamp(cellCoords.x, 0, gridSizeX - 1);
        cellCoords.y = Mathf.Clamp(cellCoords.y, 0, gridSizeY - 1);

        return grid[cellCoords.x, cellCoords.y];
    }

    public Node GetNodeByGridCoords(int x, int y)
    {
        if ((uint)x >= gridSizeX || (uint)y >= gridSizeY) return null;
        return grid[x, y];
    }

    // Keep legacy overloads (no proxy-node allocations) — callers must use IsWalkableCached for clearance.
    [Obsolete("Use NodeFromWorldPoint(pos) and then Grid.IsWalkableCached(node, clearanceRadius).")]
    public Node NodeFromWorldPoint(Vector3 worldPosition, float clearanceRadius) => NodeFromWorldPoint(worldPosition);

    [Obsolete("Use GetNodeByGridCoords(x,y) and then Grid.IsWalkableCached(node, clearanceRadius).")]
    public Node GetNodeByGridCoords(int x, int y, float clearanceRadius) => GetNodeByGridCoords(x, y);

    // ===========================
    // Neighbours (baked and clearance-aware)
    // ===========================
    /// <summary>Baked neighbours (allocating). Prefer NonAlloc variants in hot paths.</summary>
    public List<Node> GetNeighbours(Node node)
    {
        var neighbours = new List<Node>(8);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = node.gridX + dx, ny = node.gridY + dy;
                if ((uint)nx < gridSizeX && (uint)ny < gridSizeY)
                    neighbours.Add(grid[nx, ny]);
            }
        }
        return neighbours;
    }

    /// <summary>NON-ALLOC baked neighbours. Writes into buffer and returns count.</summary>
    public int GetNeighboursNonAlloc(Node node, Node[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return 0;

        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = node.gridX + dx, ny = node.gridY + dy;
                if ((uint)nx < gridSizeX && (uint)ny < gridSizeY)
                {
                    if (count < buffer.Length) buffer[count++] = grid[nx, ny];
                    else return count; // buffer full
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Clearance-aware neighbours (allocating). Uses neighbour-mask cache and proper no-corner-cut diagonals.
    /// Prefer NonAlloc in hot paths.
    /// </summary>
    public List<Node> GetNeighbours(Node node, float clearanceRadius)
    {
        var neighbours = new List<Node>(8);
        byte mask = GetNeighbourMask(node, clearanceRadius);

        int bit = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) { bit++; continue; }

                if (((mask >> bit) & 1) != 0)
                {
                    int nx = node.gridX + dx, ny = node.gridY + dy;
                    if ((uint)nx < gridSizeX && (uint)ny < gridSizeY)
                        neighbours.Add(grid[nx, ny]);
                }
                bit++;
            }
        }
        return neighbours;
    }

    /// <summary>
    /// NON-ALLOC clearance-aware neighbours. Writes into buffer and returns count.
    /// Uses neighbour-mask cache and includes diagonal corner blocking (both orthogonals must be clear).
    /// </summary>
    public int GetNeighboursNonAlloc(Node node, Node[] buffer, float clearanceRadius)
    {
        if (buffer == null || buffer.Length == 0) return 0;

        byte mask = GetNeighbourMask(node, clearanceRadius);

        int count = 0;
        int bit = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) { bit++; continue; }

                if (((mask >> bit) & 1) != 0)
                {
                    int nx = node.gridX + dx, ny = node.gridY + dy;
                    if ((uint)nx < gridSizeX && (uint)ny < gridSizeY)
                    {
                        if (count < buffer.Length) buffer[count++] = grid[nx, ny];
                        else return count; // buffer full
                    }
                }
                bit++;
            }
        }
        return count;
    }

    // ===========================
    // Walkability (public cached)
    // ===========================
    /// <summary>
    /// Public cached query. This is the preferred entry-point for dynamic clearance walkability.
    /// </summary>
    public bool IsWalkableCached(Node node, float clearanceRadius)
    {
        if (node == null) return false;
        if (!useWalkabilityCache || walkabilityCache == null)
            return IsWalkable_Internal(node, clearanceRadius);

        long key = MakeWalkKey(node.worldPosition, clearanceRadius);
        if (walkabilityCache.TryGetValue(key, out var cached))
        {
            if (!Application.isPlaying || Time.time - cached.timestamp < cacheLifetime)
                return cached.walkable;
        }

        bool w = IsWalkable_Internal(node, clearanceRadius);
        walkabilityCache[key] = new CachedWalkability(w, Application.isPlaying ? Time.time : 0f);
        return w;
    }

    // Legacy public methods kept for compatibility; discourage usage.
    [Obsolete("Use IsWalkableCached(node, clearanceRadius). This method performs a Physics2D check every call.")]
    public bool IsWalkable(Node node, float clearanceRadius) => IsWalkable_Internal(node, clearanceRadius);

    [Obsolete("Use IsWalkableCached(NodeFromWorldPoint(worldPosition), clearanceRadius). This method performs a Physics2D check every call.")]
    public bool IsWalkableWorld(Vector3 worldPosition, float clearanceRadius) => IsWalkableWorld_Internal(worldPosition, clearanceRadius);

    // ===========================
    // Walkability (private core)
    // ===========================
    private bool IsWalkable_Internal(Node node, float clearanceRadius)
        => IsWalkableWorld_Internal(node.worldPosition, clearanceRadius);

    private bool IsWalkableWorld_Internal(Vector3 worldPosition, float clearanceRadius)
    {
        // Optional instrumentation if you have a PathRequestManager
        if (PathRequestManager.Instance != null)
            PathRequestManager.ReportPhysicsCall();

        float r = Mathf.Max(nodeRadius, clearanceRadius);
        return !Physics2D.OverlapCircle(worldPosition, r, unwalkableMask);
    }

    // ===========================
    // Neighbour-mask cache
    // ===========================
    private byte GetNeighbourMask(Node node, float clearanceRadius)
    {
        int bucket = ClearanceBucket(clearanceRadius);
        long key = MakeMaskKey(node.gridX, node.gridY, bucket);

        if (neighbourMaskCache.TryGetValue(key, out var mask))
            return mask;

        mask = BuildNeighbourMask(node, clearanceRadius);
        neighbourMaskCache[key] = mask;
        return mask;
    }

    private byte BuildNeighbourMask(Node node, float clearanceRadius)
    {
        byte m = 0;
        int bit = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) { bit++; continue; }

                int nx = node.gridX + dx;
                int ny = node.gridY + dy;

                if ((uint)nx >= gridSizeX || (uint)ny >= gridSizeY)
                {
                    bit++;
                    continue;
                }

                Node n = grid[nx, ny];

                if (dx != 0 && dy != 0)
                {
                    // diagonal: require both orthogonal sides clear (no corner cutting)
                    int sx = node.gridX + dx; // same y
                    int sy = node.gridY;
                    int tx = node.gridX;      // same x
                    int ty = node.gridY + dy;

                    if ((uint)sx >= gridSizeX || (uint)sy >= gridSizeY ||
                        (uint)tx >= gridSizeX || (uint)ty >= gridSizeY)
                    {
                        bit++;
                        continue;
                    }

                    Node sideA = grid[sx, sy];
                    Node sideB = grid[tx, ty];

                    if (IsWalkableCached(sideA, clearanceRadius) &&
                        IsWalkableCached(sideB, clearanceRadius) &&
                        IsWalkableCached(n, clearanceRadius))
                    {
                        m |= (byte)(1 << bit);
                    }
                }
                else
                {
                    if (IsWalkableCached(n, clearanceRadius))
                        m |= (byte)(1 << bit);
                }

                bit++;
            }
        }

        return m;
    }

    // ===========================
    // Cache management (public via interface)
    // ===========================
    public void ClearWalkabilityCache()
    {
        walkabilityCache?.Clear();
        neighbourMaskCache.Clear();
    }

    public void ClearCache() => ClearWalkabilityCache();

    /// <summary>
    /// Region-based invalidation. Current implementation conservatively clears all caches.
    /// (Walkability key packing is not reversible across negatives; refine later if needed.)
    /// </summary>
    public void InvalidateCacheRegion(Vector3 position, float radius)
    {
        ClearWalkabilityCache();
    }

    private void CleanupCaches()
    {
        // Walkability cache: expire entries by age
        if (walkabilityCache != null && walkabilityCache.Count > 0)
        {
            float now = Time.time;
            var toRemove = new List<long>(64);

            foreach (var kvp in walkabilityCache)
            {
                if (now - kvp.Value.timestamp > cacheLifetime)
                    toRemove.Add(kvp.Key);
            }

            for (int i = 0; i < toRemove.Count; i++)
                walkabilityCache.Remove(toRemove[i]);

            // Emergency clear if ballooned
            if (walkabilityCache.Count > maxCacheEntries)
                walkabilityCache.Clear();
        }

        // Neighbour mask cache: cheap, clear opportunistically if huge
        if (neighbourMaskCache.Count > maxCacheEntries * 2)
            neighbourMaskCache.Clear();
    }

    // ===========================
    // Prewarm API (async, budgeted)
    // ===========================
    /// <summary>
    /// Background prewarm that fills neighbour masks (and walkability cache as a side-effect)
    /// for the given radii. Runs over multiple frames to avoid spikes.
    /// </summary>
    public IEnumerator PrewarmNeighbourMasksAsync(IList<float> radii, int cellsPerFrame = 6000)
    {
        if (grid == null || radii == null || radii.Count == 0) yield break;

        // Build distinct buckets once
        var buckets = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < radii.Count; i++)
        {
            int b = ClearanceBucket(Mathf.Max(0f, radii[i]));
            if (seen.Add(b)) buckets.Add(b);
        }

        if (prewarmLog) Debug.Log($"[Grid] Prewarm start: buckets={buckets.Count}, cells={gridSizeX * gridSizeY}");

        int processedThisFrame = 0;
        int totalProcessed = 0;
        int perFrame = Mathf.Max(64, cellsPerFrame);

        for (int y = 0; y < gridSizeY; y++)
        {
            for (int x = 0; x < gridSizeX; x++)
            {
                Node n = grid[x, y];

                // Fill per requested bucket
                for (int bi = 0; bi < buckets.Count; bi++)
                {
                    int bucket = buckets[bi];
                    long key = MakeMaskKey(x, y, bucket);
                    if (!neighbourMaskCache.ContainsKey(key))
                    {
                        float r = bucket * nodeRadius;
                        byte m = BuildNeighbourMask(n, r);
                        neighbourMaskCache[key] = m;
                    }
                }

                processedThisFrame++;
                totalProcessed++;

                if (processedThisFrame >= perFrame)
                {
                    processedThisFrame = 0;
                    if (prewarmLog) Debug.Log($"[Grid] Prewarm progress: {totalProcessed}/{gridSizeX * gridSizeY}");
                    yield return null; // spread over frames
                }
            }
        }

        if (prewarmLog) Debug.Log($"[Grid] Prewarm complete: {totalProcessed} cells.");
    }

    // ===========================
    // Gizmos
    // ===========================
    private void OnDrawGizmos()
    {
        if (showGridBoundaryAlways || (Application.isPlaying && showGridGizmos))
        {
            Gizmos.color = Color.cyan;
            Vector3 center = new Vector3(_origin.x + gridSizeX * _cellSize * 0.5f, _origin.y + gridSizeY * _cellSize * 0.5f, 0f);
            Vector3 boundarySize = new Vector3(gridSizeX * _cellSize, gridSizeY * _cellSize, 1f);
            Gizmos.DrawWireCube(center, boundarySize);
        }

        if (!Application.isPlaying || !showGridGizmos || grid == null) return;

        float nodeSize = _cellSize * 0.85f;
        foreach (Node n in grid)
        {
            if (!n.walkable)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.3f);
                Gizmos.DrawCube(n.worldPosition, new Vector3(nodeSize, nodeSize, 1f));
            }
        }
    }
}

/// <summary>
/// Interface implemented by Grid so the pathfinder can clear/trim caches directly.
/// </summary>
public interface IPathfindingCache
{
    void ClearCache();
    void InvalidateCacheRegion(Vector3 position, float radius);
}