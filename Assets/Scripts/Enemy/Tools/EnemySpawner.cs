// ================================
// File: EnemySpawner.cs
// ================================
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("AI/Enemy Spawner")]
public class EnemySpawner : MonoBehaviour
{
    [System.Serializable]
    public struct SpawnEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight;   // if all weights 0, selection falls back to uniform
    }

    [Header("Spawn Control")]
    public bool autoSpawn = true;
    [Min(0.05f)] public float spawnInterval = 0.5f;   // seconds between spawn ticks (if autoSpawn)
    [Min(1)] public int spawnPerTick = 1;             // how many to spawn per tick
    [Min(0)] public int maxAlive = 50;                // cap on concurrent living enemies

    [Header("Placement")]
    [Min(0f)] public float spawnRadius = 5f;          // circle radius around spawner
    public float minSeparation = 0.35f;               // overlap check radius
    public LayerMask blockLayers;                     // e.g., Wall, Obstacle, Enemy
    [Range(4, 64)] public int placementTries = 16;    // attempts to find a clear spot per enemy
    public bool useGridWalkable = true;               // if you have a custom Grid with NodeFromWorldPoint
    public float spawnZ = 0f;                          // z-depth for 2D

    [Header("Prefabs (Drag & Drop)")]
    public List<SpawnEntry> enemies = new List<SpawnEntry>(); // supports multiple types

    [Header("Extras")]
    public Transform parentForSpawns;                 // optional parenting
    public bool applyDispersalImpulse = true;
    public float dispersalImpulse = 1.25f;            // impulse strength on spawn

    [Header("Debug")]
    public bool drawGizmos = true;
    public Color gizmoColor = new Color(0.2f, 0.9f, 1f, 0.25f);

    // internals
    private readonly List<GameObject> _alive = new List<GameObject>();
    private float _timer = 0f;
    private Grid _grid; // your custom Grid class used elsewhere in the project (NodeFromWorldPoint)

    void OnEnable()
    {
        if (useGridWalkable)
            _grid = Object.FindObjectOfType<Grid>(true);
    }

    void Update()
    {
        PruneDead();

        if (!autoSpawn) return;

        _timer += Time.deltaTime;
        while (_timer >= spawnInterval)
        {
            _timer -= spawnInterval;
            SpawnTick();
        }
    }

    public void SpawnTick()
    {
        int room = maxAlive <= 0 ? int.MaxValue : Mathf.Max(0, maxAlive - _alive.Count);
        if (room <= 0) return;

        int toSpawn = Mathf.Min(spawnPerTick, room);
        for (int i = 0; i < toSpawn; i++)
        {
            TrySpawnOne();
        }
    }

    public void Spawn(int count)
    {
        PruneDead();
        int room = maxAlive <= 0 ? int.MaxValue : Mathf.Max(0, maxAlive - _alive.Count);
        int toSpawn = Mathf.Min(count, room);
        for (int i = 0; i < toSpawn; i++)
            TrySpawnOne();
    }

    public void KillAll()
    {
        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            if (_alive[i] != null) Destroy(_alive[i]);
        }
        _alive.Clear();
    }

    // ---------- Core spawn helpers ----------

    private bool TrySpawnOne()
    {
        var prefab = ChoosePrefab();
        if (prefab == null) { Debug.LogWarning("[EnemySpawner] No valid prefab to spawn."); return false; }

        Vector3 pos;
        if (!FindPlacement(out pos)) return false;

        var go = Instantiate(prefab, pos, Quaternion.identity, parentForSpawns ? parentForSpawns : null);
        _alive.Add(go);

        if (applyDispersalImpulse)
        {
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                // Push roughly outward from spawner center with a little randomness
                Vector2 dir = ((Vector2)(pos - transform.position)).sqrMagnitude > 0.0001f
                    ? ((Vector2)(pos - transform.position)).normalized
                    : Random.insideUnitCircle.normalized;

                rb.AddForce(dir * dispersalImpulse, ForceMode2D.Impulse);
            }
        }

        return true;
    }

    private GameObject ChoosePrefab()
    {
        if (enemies == null || enemies.Count == 0) return null;

        float total = 0f;
        bool anyWeight = false;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].prefab == null) continue;
            total += Mathf.Max(0f, enemies[i].weight);
            if (enemies[i].weight > 0f) anyWeight = true;
        }

        if (!anyWeight)
        {
            // uniform among non-null entries
            var pool = new List<GameObject>();
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].prefab != null) pool.Add(enemies[i].prefab);
            if (pool.Count == 0) return null;
            return pool[Random.Range(0, pool.Count)];
        }

        float pick = Random.Range(0f, total);
        float acc = 0f;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].prefab == null) continue;
            acc += Mathf.Max(0f, enemies[i].weight);
            if (pick <= acc) return enemies[i].prefab;
        }

        // fallback
        for (int i = 0; i < enemies.Count; i++)
            if (enemies[i].prefab != null) return enemies[i].prefab;
        return null;
    }

    private bool FindPlacement(out Vector3 worldPos)
    {
        Vector3 center = transform.position;
        for (int attempt = 0; attempt < placementTries; attempt++)
        {
            Vector2 off = Random.insideUnitCircle * spawnRadius;
            Vector3 candidate = new Vector3(center.x + off.x, center.y + off.y, spawnZ);

            if (useGridWalkable && _grid != null)
            {
                Node n = _grid.NodeFromWorldPoint(candidate);
                if (n == null || !n.walkable) continue;
                candidate = new Vector3(n.worldPosition.x, n.worldPosition.y, spawnZ);
            }

            // overlap check versus world blockers (walls/obstacles/enemies)
            if (Physics2D.OverlapCircle(candidate, minSeparation, blockLayers) != null)
                continue;

            // optional separation from already alive instances (prevents tight stacking)
            bool tooClose = false;
            for (int i = 0; i < _alive.Count; i++)
            {
                var g = _alive[i];
                if (g == null) continue;
                if (Vector2.Distance(g.transform.position, candidate) < minSeparation * 2f)
                {
                    tooClose = true; break;
                }
            }
            if (tooClose) continue;

            worldPos = candidate;
            return true;
        }

        worldPos = center; // fallback (won't spawn if overlaps)
        return false;
    }

    private void PruneDead()
    {
        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            if (_alive[i] == null) _alive.RemoveAt(i);
        }
    }

    // ---------- Gizmos ----------
    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireSphere(transform.position, spawnRadius);
    }
}
