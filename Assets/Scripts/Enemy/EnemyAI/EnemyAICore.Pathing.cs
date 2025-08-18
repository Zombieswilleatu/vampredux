// -----------------------------
// File: EnemyAICore.Pathing.cs (partial)
// Purpose: Request paths and post-process them so segments NEVER cut corners.
// Adds: Path choke scan + detour on jam
// -----------------------------
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Navigation Safety")]
        [SerializeField] private LayerMask navBlockers;                // Set to Wall | Obstacle
        [SerializeField] private float navClearancePadding = 0.06f;    // Extra safety around agent radius
        [SerializeField] private float cornerInset = 0.08f;            // Push waypoints a hair off corners
        [SerializeField] private int losMaxHops = 64;                  // safety bound for smoothing loop
        [SerializeField] private float clearanceSampleStep = 0.12f;    // distance between clearance samples

        // --- Congestion-aware target biasing (existing) ---
        [Header("Congestion Avoidance")]
        [SerializeField] private bool avoidCongestionInPathing = true;
        [SerializeField] private float congestionProbeRadius = 1.0f;    // how big an area around a point we consider "occupied"
        [SerializeField] private float congestionProbeRing = 1.75f;     // distance from original target to sample alternatives
        [SerializeField] private int congestionProbeSamples = 8;        // ring samples to try (plus the original)
        [SerializeField] private float congestionDistanceWeight = 0.25f; // small bias to keep close to original target

        // --- NEW: scan the *path* itself for early choke points ---
        [Header("Path Choke Scan")]
        [SerializeField] private bool scanPathForCongestion = true;
        [SerializeField] private int chokeScanNodes = 6;               // how many leading nodes to check
        [SerializeField] private float chokeScanRadius = 0.9f;         // radius around each node to count allies
        [SerializeField] private int chokeScanThreshold = 3;           // neighbors needed to call it a choke
        [SerializeField] private float chokeDetourCooldown = 0.75f;    // don't detour more often than this (sec)

        private float lastChokeDetourTime = -999f;

        private readonly Collider2D[] _congBuf = new Collider2D[48];   // temp buffer for non-alloc queries

        private float agentRadius = -1f; // cached from collider

        // --- graph safety ---
        private bool EnsureGraph()
        {
            if (grid != null && pathfinder != null) return true;
            if (grid == null) grid = Object.FindObjectOfType<Grid>(true);
            if (pathfinder == null) pathfinder = Object.FindObjectOfType<Pathfinding>(true);
            if (grid == null || pathfinder == null)
            {
                if (showDebugLogs) Debug.LogError($"[AI-{enemyID}] Missing Grid or Pathfinding in scene.");
                return false;
            }
            return true;
        }

        // --- radius cache ---
        void CacheAgentRadius()
        {
            if (agentRadius > 0f) return;

            var cc = GetComponent<CircleCollider2D>();
            if (cc != null)
            {
                float scale = Mathf.Max(Mathf.Abs(transform.localScale.x), Mathf.Abs(transform.localScale.y));
                agentRadius = Mathf.Abs(cc.radius) * scale;
                return;
            }

            var cap = GetComponent<CapsuleCollider2D>();
            if (cap != null)
            {
                Vector2 size = cap.size;
                float scaleX = Mathf.Abs(transform.localScale.x);
                float scaleY = Mathf.Abs(transform.localScale.y);
                float maxAxis = Mathf.Max(size.x * scaleX, size.y * scaleY);
                agentRadius = maxAxis * 0.5f;
                return;
            }

            var bc = GetComponent<BoxCollider2D>();
            if (bc != null)
            {
                Vector2 size = bc.size;
                float scaleX = Mathf.Abs(transform.localScale.x);
                float scaleY = Mathf.Abs(transform.localScale.y);
                float maxAxis = Mathf.Max(size.x * scaleX, size.y * scaleY);
                agentRadius = maxAxis * 0.5f;
                return;
            }

            agentRadius = 0.3f; // fallback
        }

        // ---------- PATH REQUESTING ----------
        IEnumerator RequestPathRepeatedly(float initialDelay)
        {
            if (initialDelay > 0f) yield return new WaitForSeconds(initialDelay);
            yield return new WaitForEndOfFrame();

            while (true)
            {
                float waitTime = actualPathUpdateInterval;

                if (enableDynamicThrottling && PathRequestManager.Instance != null)
                {
                    int queueSize = PathRequestManager.Instance.GetQueueLength();
                    if (queueSize > 20)
                        waitTime = actualPathUpdateInterval * Mathf.Min(queueSize / 10f, 10f);
                }

                if (skipPathIfStationary && hasValidPath && path != null && path.Count > 0)
                {
                    float distanceMoved = Vector3.Distance(transform.position, lastPosition);
                    if (distanceMoved < stationaryThreshold)
                    {
                        timeSinceLastMove += waitTime;
                        if (timeSinceLastMove < waitTime * 3)
                        { yield return new WaitForSeconds(waitTime); continue; }
                    }
                    else timeSinceLastMove = 0f;
                    lastPosition = transform.position;
                }

                RequestPath();
                yield return new WaitForSeconds(waitTime);
            }
        }

        // EnemyAICore.Pathing.cs
        void RequestPath(bool force = false)
        {
            if (!EnsureGraph()) return;
            if (isRecovering) return;

            // ---- throttle gate (skip if force) ----
            bool hasPathNow = path != null && path.Count > 0;
            if (!hasPathNow) nextPathRequestTime = Time.time;
            if (!force && Time.time < nextPathRequestTime)
            {
                if (showDebugLogs) Debug.Log($"[AI-{enemyID}] RequestPath throttled ({(nextPathRequestTime - Time.time):F2}s)");
                return;
            }

            // ---- dynamic backoff (skip if force) ----
            if (!force && enableDynamicThrottling && PathRequestManager.Instance != null && hasPathNow)
            {
                int q = PathRequestManager.Instance.GetQueueLength();
                if (q > 10)
                {
                    float mult = Mathf.Min(q / 10f, 5f);
                    nextPathRequestTime = Time.time + (actualPathUpdateInterval * mult);
                    if (showDebugLogs) Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Backoff: queue {q}");
                    if (q > 30 && hasValidPath) return;
                }
            }

            UpdateTarget();

            // ---- needNewPath (force overrides) ----
            bool needNewPath =
                force ||
                path == null || path.Count == 0 ||
                targetIndex >= path.Count - minNodesRemainingForNewPath ||
                stuckRecoveryAttempts > 0 ||
                ((targetMode == TargetMode.FollowTarget || targetMode == TargetMode.FindByTag) && targetTransform != null &&
                 (Vector3.Distance(lastKnownTargetPos, currentTarget) > targetMoveRepathThreshold ||
                  (Time.time - lastRepathTime) > maxRepathIntervalWhileFollowing));

            if (!needNewPath) return;

            CacheAgentRadius();
            float clearance = agentRadius + navClearancePadding;

            Vector3 safeStart = NearestWalkableFromWorld(transform.position, 0.25f, 3.0f, 24, clearance);
            Vector3 safeTarget = NearestWalkableFromWorld(currentTarget, 0.25f, 3.0f, 24, clearance);
            if (safeStart == Vector3.negativeInfinity || safeTarget == Vector3.negativeInfinity) { nextPathRequestTime = Time.time + 0.25f; return; }

            // (optional) congestion bias here...

            lastKnownTargetPos = safeTarget;
            lastRepathTime = Time.time;

            PathRequestManager.Instance.RequestPath(safeStart, safeTarget, clearance, OnPathFound, enemyID);

            // IMPORTANT: do not push the cooldown when forced
            nextPathRequestTime = force ? Time.time : Time.time + actualPathUpdateInterval;
        }



        void OnPathFound(List<Node> nodePath)
        {
            if (nodePath == null || nodePath.Count == 0)
            {
                hasValidPath = false; nextPathRequestTime = Time.time;
                if (stuckRecoveryAttempts > 0) FindAlternativeTarget();
                return;
            }

            // Convert to positions
            var newPath = new List<Vector3>(nodePath.Count);
            for (int i = 0; i < nodePath.Count; i++) newPath.Add(nodePath[i].worldPosition);

            // Corner-safe processing
            CacheAgentRadius();

            // 1) inset corners slightly away from walls
            var inset = CornerInsetPath(newPath, cornerInset, navBlockers, agentRadius + navClearancePadding);

            // 2) only merge segments if full segment has clearance
            var smoothed = usePathSmoothing && inset.Count > 2 ? ClearanceSmooth(inset) : inset;

            // --- NEW: scan first few nodes for congestion and optionally detour ---
            if (scanPathForCongestion && Time.time - lastChokeDetourTime >= chokeDetourCooldown)
            {
                Vector3 chokePos;
                int chokeCount;
                if (TryFindChokePoint(smoothed, out chokePos, out chokeCount))
                {
                    float clearance = agentRadius + navClearancePadding;
                    Vector3 detour = PickDecongestedTarget(chokePos, clearance);

                    // Only detour if it actually moves away from the choke
                    if (Vector2.Distance(detour, chokePos) > 0.1f)
                    {
                        lastChokeDetourTime = Time.time;
                        if (showDebugLogs)
                            Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Choke detected (n={chokeCount}) near {chokePos:F2}. Detouring to {detour:F2}");

                        // Re-request a path from current position to the detour point
                        Vector3 start = transform.position;
                        PathRequestManager.Instance.RequestPath(start, detour, clearance, OnPathFound, enemyID);
                        return; // don't accept current jammed path
                    }
                }
            }

            // Accept path
            path = smoothed;
            targetIndex = 0; hasValidPath = true; if (stuckRecoveryAttempts > 0) stuckRecoveryAttempts = 0;
            if (showDebugLogs) Debug.Log($"<color=green>[AI]</color> Path final nodes: {path.Count}");
        }

        // ---------- CORNER-SAFE UTILITIES ----------
        List<Vector3> CornerInsetPath(List<Vector3> raw, float inset, LayerMask blockers, float radius)
        {
            if (raw.Count <= 2 || inset <= 0f) return raw;

            var outPath = new List<Vector3>(raw.Count);
            outPath.Add(raw[0]);

            for (int i = 1; i < raw.Count - 1; i++)
            {
                Vector3 p = raw[i];

                // sample 8 directions, apply small repulsion if near blockers
                Vector2 repulse = Vector2.zero;
                const int dirs = 8;
                for (int d = 0; d < dirs; d++)
                {
                    float ang = (Mathf.PI * 2f / dirs) * d;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector2 probe = (Vector2)p + dir * radius;
                    if (Physics2D.OverlapPoint(probe, blockers) != null ||
                        Physics2D.OverlapCircle(probe, 0.02f, blockers) != null)
                    {
                        repulse -= dir;
                    }
                }

                if (repulse.sqrMagnitude > 0.0001f)
                {
                    repulse = repulse.normalized * inset;
                    p += (Vector3)repulse;
                }

                outPath.Add(p);
            }

            outPath.Add(raw[raw.Count - 1]);
            return outPath;
        }

        List<Vector3> ClearanceSmooth(List<Vector3> raw)
        {
            var outPath = new List<Vector3>();
            int i = 0; int guard = 0;
            outPath.Add(raw[0]);

            while (i < raw.Count - 1 && guard++ < losMaxHops)
            {
                int far = raw.Count - 1;
                while (far > i + 1)
                {
                    if (HasClearanceAlongSegment(raw[i], raw[far])) break;
                    far--;
                }
                outPath.Add(raw[far]);
                i = far;
            }

            return outPath;
        }

        bool HasClearanceAlongSegment(Vector2 from, Vector2 to)
        {
            float r = agentRadius + navClearancePadding;

            // quick reject
            if (Physics2D.Linecast(from, to, navBlockers)) return false;

            float dist = Vector2.Distance(from, to);
            if (dist <= 0.0001f) return true;

            int steps = Mathf.Max(2, Mathf.CeilToInt(dist / Mathf.Max(0.02f, clearanceSampleStep)));
            Vector2 step = (to - from) / steps;
            Vector2 p = from;

            for (int i = 0; i <= steps; i++)
            {
                if (Physics2D.OverlapCircle(p, r, navBlockers) != null)
                    return false;
                p += step;
            }

            return true;
        }

        // ---------- HELPERS ----------

        /// <summary>
        /// Finds the nearest walkable node to a world position by sampling rings around it.
        /// Returns Vector3.negativeInfinity if none found within maxRadius, or if graph missing.
        /// Respects a clearance radius around blockers.
        /// </summary>
        public Vector3 NearestWalkableFromWorld(Vector3 worldPos, float ringStep, float maxRadius, int samplesPerRing, float clearanceRadius)
        {
            if (!EnsureGraph()) return worldPos;

            Node n0 = grid.NodeFromWorldPoint(worldPos);
            if (n0 != null && n0.walkable &&
                Physics2D.OverlapCircle(n0.worldPosition, clearanceRadius, navBlockers) == null)
                return n0.worldPosition;

            float r = ringStep;
            while (r <= maxRadius)
            {
                for (int i = 0; i < samplesPerRing; i++)
                {
                    float t = (i / (float)samplesPerRing) * Mathf.PI * 2f;
                    Vector3 p = worldPos + new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f) * r;

                    Node n = grid.NodeFromWorldPoint(p);
                    if (n != null && n.walkable &&
                        Physics2D.OverlapCircle(n.worldPosition, clearanceRadius, navBlockers) == null)
                        return n.worldPosition;
                }
                r += ringStep;
            }

            return Vector3.negativeInfinity;
        }

        // --- Congestion scoring + target selection (existing + reused) ---

        // Counts nearby allies at 'pos' within 'radius' (non-alloc).
        int CountAlliesNear(Vector2 pos, float radius)
        {
            if (allyMask.value == 0) allyMask = LayerMask.GetMask("Enemy"); // fallback if not set elsewhere
            int count = Physics2D.OverlapCircleNonAlloc(pos, radius, _congBuf, allyMask);
            // exclude self if present
            for (int i = 0; i < count; i++)
            {
                if (_congBuf[i] == null) continue;
                if (_congBuf[i].gameObject == gameObject)
                {
                    _congBuf[i] = _congBuf[count - 1];
                    count--; i--;
                }
            }
            return count;
        }

        // Returns an alternative near 'target' with lower congestion (or 'target' if best).
        Vector3 PickDecongestedTarget(Vector3 target, float clearance)
        {
            int bestNeighbors = Mathf.Max(0, CountAlliesNear(target, congestionProbeRadius));
            float bestCost = bestNeighbors + congestionDistanceWeight * 0f;
            Vector3 best = target;

            int samples = Mathf.Max(0, congestionProbeSamples);
            if (samples == 0) return target;

            float step = Mathf.PI * 2f / samples;
            for (int i = 0; i < samples; i++)
            {
                float ang = i * step;
                Vector3 cand = target + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * congestionProbeRing;

                // snap to safe nearby node (respecting clearance)
                Vector3 safe = NearestWalkableFromWorld(cand, 0.25f, 1.75f, 16, clearance);
                if (safe == Vector3.negativeInfinity) continue;

                int n = Mathf.Max(0, CountAlliesNear(safe, congestionProbeRadius));
                float distBias = Vector2.Distance((Vector2)target, (Vector2)safe) * congestionDistanceWeight;
                float cost = n + distBias;

                if (cost + 1e-3f < bestCost)
                {
                    bestCost = cost;
                    bestNeighbors = n;
                    best = safe;
                }
            }

            if (showDebugLogs && best != target)
                Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Congestion reroute: neighbors orig={CountAlliesNear(target, congestionProbeRadius)}, best={bestNeighbors}");

            return best;
        }

        // --- NEW: choke finder on a candidate path ---
        bool TryFindChokePoint(List<Vector3> candidatePath, out Vector3 chokePos, out int chokeCount)
        {
            chokePos = Vector3.zero;
            chokeCount = 0;

            if (!scanPathForCongestion || candidatePath == null || candidatePath.Count == 0)
                return false;

            int checks = Mathf.Min(chokeScanNodes, candidatePath.Count);
            int bestCount = 0;
            Vector3 bestPos = candidatePath[0];

            for (int i = 0; i < checks; i++)
            {
                Vector3 p = candidatePath[i];
                int n = CountAlliesNear(p, Mathf.Max(0.05f, chokeScanRadius));
                if (n > bestCount)
                {
                    bestCount = n;
                    bestPos = p;
                }
            }

            if (bestCount >= chokeScanThreshold)
            {
                chokePos = bestPos;
                chokeCount = bestCount;
                return true;
            }

            return false;
        }
    }
}
