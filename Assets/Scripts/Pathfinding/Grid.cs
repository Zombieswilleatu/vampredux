using UnityEngine;
using System.Collections;
using System.Collections.Generic;

[ExecuteAlways]
public class Grid : MonoBehaviour
{
    [Header("Collision / Layout")]
    public LayerMask unwalkableMask;
    public Vector2 gridWorldSize;
    [Min(0.05f)] public float nodeRadius = 0.5f;

    [Header("Gizmos")]
    public bool showGridGizmos = true;
    public bool showGridBoundaryAlways = true;

    [Header("Performance")]
    [Tooltip("Cache walkability checks to reduce Physics2D calls.")]
    public bool useWalkabilityCache = true;

    [Tooltip("Seconds a cached result remains valid.")]
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
    [Tooltip("Log basic progress while prewarming.")]
    public bool prewarmLog = false;

    // --- Internal grid data ---
    private Node[,] grid;
    private float nodeDiameter;
    private int gridSizeX, gridSizeY;

    // --- Caching (walkability with clearance) ---
    private Dictionary<long, CachedWalkability> walkabilityCache;
    private float lastCacheCleanup = 0f;
    private const float CACHE_CLEANUP_INTERVAL = 5f;

    // --- Caching (8-way neighbour mask per cell/clearance bucket) ---
    // bit order matches loops dx=-1..1, dy=-1..1 skipping center
    private Dictionary<long, byte> neighbourMaskCache = new Dictionary<long, byte>(4096);

    // Optional public readouts
    public int gridSizeXPublic => gridSizeX;
    public int gridSizeYPublic => gridSizeY;

    private struct CachedWalkability
    {
        public bool walkable;
        public float timestamp;
        public CachedWalkability(bool walkable, float timestamp)
        {
            this.walkable = walkable;
            this.timestamp = timestamp;
        }
    }

    // ---------------------------
    // Unity
    // ---------------------------
    void Awake()
    {
        nodeDiameter = nodeRadius * 2f;
        gridSizeX = Mathf.Max(1, Mathf.RoundToInt(gridWorldSize.x / nodeDiameter));
        gridSizeY = Mathf.Max(1, Mathf.RoundToInt(gridWorldSize.y / nodeDiameter));

        if (useWalkabilityCache)
            walkabilityCache = new Dictionary<long, CachedWalkability>(2048);

        CreateGrid();
    }

    void Start()
    {
        // Optional background prewarm (runtime only)
        if (Application.isPlaying && prewarmOnStart && prewarmClearances != null && prewarmClearances.Length > 0)
        {
            StartCoroutine(PrewarmNeighbourMasksAsync(prewarmClearances, prewarmCellsPerFrame));
        }
    }

    void Update()
    {
        // Periodic cache cleanup
        if (!useWalkabilityCache) return;

        if (Application.isPlaying && Time.time - lastCacheCleanup > CACHE_CLEANUP_INTERVAL)
        {
            CleanupCache();
            lastCacheCleanup = Time.time;
        }
    }

    private void CleanupCache()
    {
        // walkability cache: expire entries by age
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

        // neighbour mask cache: cheap, clear opportunistically
        if (neighbourMaskCache != null && neighbourMaskCache.Count > maxCacheEntries * 2)
            neighbourMaskCache.Clear();
    }

    // ---------------------------
    // Build
    // ---------------------------
    private void CreateGrid()
    {
        grid = new Node[gridSizeX, gridSizeY];

        Vector3 worldBottomLeft =
            transform.position
            - Vector3.right * gridWorldSize.x * 0.5f
            - Vector3.up * gridWorldSize.y * 0.5f;

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int y = 0; y < gridSizeY; y++)
            {
                Vector3 worldPoint =
                    worldBottomLeft
                    + Vector3.right * (x * nodeDiameter + nodeRadius)
                    + Vector3.up * (y * nodeDiameter + nodeRadius);

                // Baked baseline walkability (for gizmos / coarse checks)
                float bakedBuffer = nodeRadius * 1.5f;
                bool walkable = !Physics2D.OverlapCircle(worldPoint, bakedBuffer, unwalkableMask);

                grid[x, y] = new Node(walkable, worldPoint, x, y);
            }
        }

        // fresh grid => flush caches so dynamic results recompute
        walkabilityCache?.Clear();
        neighbourMaskCache?.Clear();
    }

    // ---------------------------
    // Base API (baked)
    // ---------------------------
    public Node NodeFromWorldPoint(Vector3 worldPosition)
    {
        float percentX = Mathf.Clamp01((worldPosition.x + gridWorldSize.x * 0.5f) / gridWorldSize.x);
        float percentY = Mathf.Clamp01((worldPosition.y + gridWorldSize.y * 0.5f) / gridWorldSize.y);

        int x = Mathf.RoundToInt((gridSizeX - 1) * percentX);
        int y = Mathf.RoundToInt((gridSizeY - 1) * percentY);

        return grid[x, y];
    }

    public List<Node> GetNeighbours(Node node)
    {
        var neighbours = new List<Node>(8);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = node.gridX + dx;
                int ny = node.gridY + dy;

                if (nx < 0 || nx >= gridSizeX || ny < 0 || ny >= gridSizeY) continue;

                neighbours.Add(grid[nx, ny]);
            }
        }

        return neighbours;
    }

    /// <summary>
    /// NON-ALLOC baked neighbours. Writes into <paramref name="buffer"/> and returns count.
    /// </summary>
    public int GetNeighboursNonAlloc(Node node, Node[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return 0;

        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = node.gridX + dx;
                int ny = node.gridY + dy;

                if (nx < 0 || nx >= gridSizeX || ny < 0 || ny >= gridSizeY) continue;

                if (count < buffer.Length) buffer[count++] = grid[nx, ny];
                else return count; // buffer full
            }
        }
        return count;
    }

    // ---------------------------
    // Dynamic-clearance overloads
    // ---------------------------

    /// <summary>
    /// Returns a proxy Node at the cell under worldPosition whose 'walkable'
    /// reflects the requested clearance radius, without mutating the baked grid.
    /// </summary>
    public Node NodeFromWorldPoint(Vector3 worldPosition, float clearanceRadius)
    {
        Node baseNode = NodeFromWorldPoint(worldPosition);
        bool walkable = IsWalkableCached(baseNode, clearanceRadius);

        // Return a proxy node if walkability differs; else return the baked node.
        if (walkable == baseNode.walkable) return baseNode;
        return new Node(walkable, baseNode.worldPosition, baseNode.gridX, baseNode.gridY);
    }

    /// <summary>Get node by grid coordinates with clearance checking.</summary>
    public Node GetNodeByGridCoords(int x, int y, float clearanceRadius)
    {
        if (x < 0 || x >= gridSizeX || y < 0 || y >= gridSizeY) return null;

        Node baseNode = grid[x, y];
        bool walkable = IsWalkableCached(baseNode, clearanceRadius);

        if (walkable == baseNode.walkable) return baseNode;
        return new Node(walkable, baseNode.worldPosition, baseNode.gridX, baseNode.gridY);
    }

    /// <summary>
    /// Neighbours filtered by dynamic clearance. Includes proper diagonal corner blocking
    /// (both orthogonal sides must be clear for diagonal moves).
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
                    int nx = node.gridX + dx;
                    int ny = node.gridY + dy;
                    if ((uint)nx < gridSizeX && (uint)ny < gridSizeY)
                        neighbours.Add(grid[nx, ny]);
                }
                bit++;
            }
        }

        return neighbours;
    }

    /// <summary>
    /// NON-ALLOC dynamic-clearance neighbours. Writes into <paramref name="buffer"/> and returns count.
    /// Includes diagonal corner blocking.
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
                    int nx = node.gridX + dx;
                    int ny = node.gridY + dy;
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

    /// <summary>World-space walkability with clearance.</summary>
    public bool IsWalkableWorld(Vector3 worldPosition, float clearanceRadius)
    {
        // Optional instrumentation if you have PathRequestManager in your project.
        if (PathRequestManager.Instance != null)
            PathRequestManager.ReportPhysicsCall();

        float r = Mathf.Max(nodeRadius, clearanceRadius);
        return !Physics2D.OverlapCircle(worldPosition, r, unwalkableMask);
    }

    /// <summary>Node walkability with clearance.</summary>
    public bool IsWalkable(Node node, float clearanceRadius)
    {
        return IsWalkableWorld(node.worldPosition, clearanceRadius);
    }

    // Cached variant (key optimization)
    private bool IsWalkableCached(Node node, float clearanceRadius)
    {
        if (!useWalkabilityCache || walkabilityCache == null)
            return IsWalkable(node, clearanceRadius);

        long key = GetCacheKey(node.worldPosition, clearanceRadius);

        if (walkabilityCache.TryGetValue(key, out var cached))
        {
            if (!Application.isPlaying || Time.time - cached.timestamp < cacheLifetime)
                return cached.walkable;
        }

        bool w = IsWalkable(node, clearanceRadius);
        walkabilityCache[key] = new CachedWalkability(w, Application.isPlaying ? Time.time : 0f);
        return w;
    }

    // Quantized cache key: pack x,y,r into a 64-bit integer
    private long GetCacheKey(Vector3 position, float clearanceRadius)
    {
        int qx = Mathf.RoundToInt(position.x * 4f);        // 0.25-unit precision
        int qy = Mathf.RoundToInt(position.y * 4f);
        int qr = Mathf.RoundToInt(clearanceRadius * 10f);  // 0.1-unit precision

        unchecked
        {
            long key = ((long)(qx & 0xFFFFF) << 44)  // 20 bits
                     | ((long)(qy & 0xFFFFF) << 24)  // 20 bits
                     | ((long)(qr & 0xFFFFFF));      // 24 bits
            return key;
        }
    }

    // ---------------------------
    // Neighbour mask cache (per cell + clearance bucket)
    // ---------------------------
    private int ClearanceBucket(float r) => Mathf.Max(0, Mathf.CeilToInt(r / (nodeRadius + 1e-6f)));

    private long MaskKey(int gx, int gy, int bucket)
    {
        unchecked
        {
            // 16 bits each (safe for typical grid sizes)
            return ((long)(gx & 0xFFFF) << 32)
                 | ((long)(gy & 0xFFFF) << 16)
                 | (long)(bucket & 0xFFFF);
        }
    }

    private byte GetNeighbourMask(Node node, float clearanceRadius)
    {
        int bucket = ClearanceBucket(clearanceRadius);
        long key = MaskKey(node.gridX, node.gridY, bucket);

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
                    int sx = node.gridX + dx; // side x (same y)
                    int sy = node.gridY;
                    int tx = node.gridX;      // side y (same x)
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

    // ---------------------------
    // Cache management hooks (for Pathfinding.NotifyEnvironmentChange)
    // ---------------------------
    public void ClearWalkabilityCache()
    {
        walkabilityCache?.Clear();
        neighbourMaskCache?.Clear();
    }

    public void ClearCache() => ClearWalkabilityCache();

    /// <summary>
    /// Region-based invalidation. Current implementation conservatively clears all caches.
    /// (Cache key packing for walkability is not reversible across negatives.)
    /// </summary>
    public void InvalidateCacheRegion(Vector3 position, float radius)
    {
        ClearWalkabilityCache();
    }

    // ---------------------------
    // Prewarm API
    // ---------------------------

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

        for (int y = 0; y < gridSizeY; y++)
        {
            for (int x = 0; x < gridSizeX; x++)
            {
                Node n = grid[x, y];

                // Fill per requested bucket
                for (int bi = 0; bi < buckets.Count; bi++)
                {
                    int bucket = buckets[bi];
                    long key = MaskKey(x, y, bucket);
                    if (!neighbourMaskCache.ContainsKey(key))
                    {
                        float r = bucket * nodeRadius;
                        byte m = BuildNeighbourMask(n, r);
                        neighbourMaskCache[key] = m;
                    }
                }

                processedThisFrame++;
                totalProcessed++;

                if (processedThisFrame >= Mathf.Max(64, cellsPerFrame))
                {
                    processedThisFrame = 0;
                    if (prewarmLog) Debug.Log($"[Grid] Prewarm progress: {totalProcessed}/{gridSizeX * gridSizeY}");
                    yield return null; // spread over frames
                }
            }
        }

        if (prewarmLog) Debug.Log($"[Grid] Prewarm complete: {totalProcessed} cells.");
    }

    // ---------------------------
    // Gizmos
    // ---------------------------
    void OnDrawGizmos()
    {
        if (showGridBoundaryAlways || (Application.isPlaying && showGridGizmos))
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, new Vector3(gridWorldSize.x, gridWorldSize.y, 1f));
        }

        if (!Application.isPlaying || !showGridGizmos || grid == null) return;

        foreach (Node n in grid)
        {
            if (!n.walkable)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.3f);
                Gizmos.DrawCube(n.worldPosition, Vector3.one * (nodeDiameter * 0.85f));
            }
        }
    }
}
