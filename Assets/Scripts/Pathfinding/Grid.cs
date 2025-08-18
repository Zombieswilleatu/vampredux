using UnityEngine;
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

    // --- Internal grid data ---
    private Node[,] grid;
    private float nodeDiameter;
    private int gridSizeX, gridSizeY;

    // --- Caching (walkability with clearance) ---
    private Dictionary<long, CachedWalkability> walkabilityCache;
    private float lastCacheCleanup = 0f;
    private const float CACHE_CLEANUP_INTERVAL = 5f;

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

    void Update()
    {
        // Periodic cache cleanup
        if (!useWalkabilityCache || walkabilityCache == null) return;

        if (Application.isPlaying && Time.time - lastCacheCleanup > CACHE_CLEANUP_INTERVAL)
        {
            CleanupCache();
            lastCacheCleanup = Time.time;
        }
    }

    private void CleanupCache()
    {
        if (walkabilityCache == null || walkabilityCache.Count == 0) return;

        float now = Time.time;
        var toRemove = new List<long>(64);

        foreach (var kvp in walkabilityCache)
        {
            if (now - kvp.Value.timestamp > cacheLifetime)
                toRemove.Add(kvp.Key);
        }

        for (int i = 0; i < toRemove.Count; i++)
            walkabilityCache.Remove(toRemove[i]);

        // Emergency clear if we’ve ballooned
        if (walkabilityCache.Count > maxCacheEntries)
            walkabilityCache.Clear();
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

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = node.gridX + dx;
                int ny = node.gridY + dy;

                if (nx < 0 || nx >= gridSizeX || ny < 0 || ny >= gridSizeY) continue;

                Node n = grid[nx, ny];

                if (dx != 0 && dy != 0)
                {
                    int sx = node.gridX + dx; // side x (same y)
                    int sy = node.gridY;
                    int tx = node.gridX;      // side y (same x)
                    int ty = node.gridY + dy;

                    if (sx < 0 || sx >= gridSizeX || sy < 0 || sy >= gridSizeY) continue;
                    if (tx < 0 || tx >= gridSizeX || ty < 0 || ty >= gridSizeY) continue;

                    Node sideA = grid[sx, sy];
                    Node sideB = grid[tx, ty];

                    if (!IsWalkableCached(sideA, clearanceRadius)) continue;
                    if (!IsWalkableCached(sideB, clearanceRadius)) continue;
                    if (!IsWalkableCached(n, clearanceRadius)) continue;
                }
                else
                {
                    if (!IsWalkableCached(n, clearanceRadius)) continue;
                }

                neighbours.Add(n);
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

        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = node.gridX + dx;
                int ny = node.gridY + dy;

                if (nx < 0 || nx >= gridSizeX || ny < 0 || ny >= gridSizeY) continue;

                Node n = grid[nx, ny];

                if (dx != 0 && dy != 0)
                {
                    int sx = node.gridX + dx; // side x (same y)
                    int sy = node.gridY;
                    int tx = node.gridX;      // side y (same x)
                    int ty = node.gridY + dy;

                    if (sx < 0 || sx >= gridSizeX || sy < 0 || sy >= gridSizeY) continue;
                    if (tx < 0 || tx >= gridSizeX || ty < 0 || ty >= gridSizeY) continue;

                    Node sideA = grid[sx, sy];
                    Node sideB = grid[tx, ty];

                    if (!IsWalkableCached(sideA, clearanceRadius)) continue;
                    if (!IsWalkableCached(sideB, clearanceRadius)) continue;
                    if (!IsWalkableCached(n, clearanceRadius)) continue;
                }
                else
                {
                    if (!IsWalkableCached(n, clearanceRadius)) continue;
                }

                if (count < buffer.Length) buffer[count++] = n;
                else return count; // buffer full
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
        int qx = Mathf.RoundToInt(position.x * 4f);   // 0.25-unit precision
        int qy = Mathf.RoundToInt(position.y * 4f);
        int qr = Mathf.RoundToInt(clearanceRadius * 10f); // 0.1-unit precision

        unchecked
        {
            long key = ((long)(qx & 0xFFFFF) << 44)  // 20 bits
                     | ((long)(qy & 0xFFFFF) << 24)  // 20 bits
                     | ((long)(qr & 0xFFFFFF));      // 24 bits
            return key;
        }
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
