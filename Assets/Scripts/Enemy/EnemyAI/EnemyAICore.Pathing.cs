// -----------------------------
// File: EnemyAICore.Pathing.cs (updated)
// Focus: kill short-hops *and* guarantee multi-point path consumption
// Changes vs last:
//  • EnsureMinWaypoints(): enforces N≥minResampledWaypoints even after smoothing.
//  • If path <= 3 nodes, we soften repath locks so we don't freeze on tiny paths.
//  • ResamplePath() unchanged API but now paired with EnsureMinWaypoints().
//  • Logs clarify exactly why path was accepted/rejected.
// -----------------------------
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Navigation Safety")]
        [SerializeField] private LayerMask navBlockers;
        [SerializeField] private float navClearancePadding = 0.06f;
        [SerializeField] private float cornerInset = 0.08f;
        [SerializeField] private int losMaxHops = 64;
        [SerializeField] private float clearanceSampleStep = 0.12f;
        [SerializeField] private bool useCapsuleClearance = false;
        [SerializeField] private float bendInsetMinDot = 0.25f;

        [Header("Path Resampling (stride)")]
        [Tooltip("Base spacing of points along accepted path.")]
        [SerializeField] private float pathResampleStep = 0.60f;

        [Tooltip("After resampling/smoothing, enforce at least this many waypoints.")]
        [SerializeField] private int minResampledWaypoints = 7; // ensure 6–8+ points to follow

        [Header("Repath Locks (anti-short-hop)")]
        [SerializeField] private int minNodesAdvanceBeforeRepath = 5;
        [SerializeField] private float minTravelBeforeRepath = 1.5f;
        [SerializeField] private float repathCooldownWhileAdvancing = 0.8f;

        [Header("Congestion Avoidance (cheap)")]
        [SerializeField] private bool avoidCongestionInPathing = true;
        [SerializeField] private float congestionProbeRadius = 1.0f;
        [SerializeField] private float congestionProbeRing = 1.4f;
        [SerializeField] private int congestionProbeSamples = 4;
        [SerializeField] private float congestionDistanceWeight = 0.20f;

        [Header("Lite Path Choke Scan (optional)")]
        [SerializeField] private bool scanPathForCongestion = false;
        [SerializeField] private int chokeScanNodes = 4;
        [SerializeField] private float chokeScanRadius = 0.9f;
        [SerializeField] private int chokeScanThreshold = 4;
        [SerializeField] private float chokeDetourCooldown = 1.0f;

        private float lastChokeDetourTime = -999f;

        // progress + locks
        private int nodesAdvancedSinceRepath = 0;
        private float repathLockUntil = -1f;
        private Vector3 posAtLastRepath;
        private int _lastObservedTargetIndex = -1;

        private float agentRadius = -1f;
        private static readonly Collider2D[] _congBuf = new Collider2D[64];

        // ---------- REQUIRED DEP CHECK ----------
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

        // ---------- RADIUS CACHE ----------
        void CacheAgentRadius()
        {
            if (agentRadius > 0f) return;

            if (TryGetComponent<CircleCollider2D>(out var cc))
            {
                float s = Mathf.Max(Mathf.Abs(transform.localScale.x), Mathf.Abs(transform.localScale.y));
                agentRadius = Mathf.Abs(cc.radius) * s; return;
            }
            if (TryGetComponent<CapsuleCollider2D>(out var cap))
            {
                Vector2 size = cap.size;
                float sx = Mathf.Abs(transform.localScale.x);
                float sy = Mathf.Abs(transform.localScale.y);
                agentRadius = Mathf.Max(size.x * sx, size.y * sy) * 0.5f; return;
            }
            if (TryGetComponent<BoxCollider2D>(out var bc))
            {
                Vector2 size = bc.size;
                float sx = Mathf.Abs(transform.localScale.x);
                float sy = Mathf.Abs(transform.localScale.y);
                agentRadius = Mathf.Max(size.x * sx, size.y * sy) * 0.5f; return;
            }
            agentRadius = Mathf.Max(0.3f, fallbackClearanceRadius);
        }

        // ---------- WAYPOINT ADVANCE OBSERVER ----------
        void LateUpdate()
        {
            if (!hasValidPath || path == null) return;

            if (_lastObservedTargetIndex < 0)
                _lastObservedTargetIndex = targetIndex;

            if (targetIndex > _lastObservedTargetIndex)
            {
                nodesAdvancedSinceRepath += (targetIndex - _lastObservedTargetIndex);
                _lastObservedTargetIndex = targetIndex;
            }
        }

        // ---------- REQUEST LOOP ----------
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

        // ---------- REQUEST ----------
        void RequestPath(bool force = false)
        {
            if (!EnsureGraph()) return;
            if (isRecovering) return;

            bool hasPathNow = path != null && path.Count > 0;
            if (!hasPathNow) nextPathRequestTime = Time.time;
            if (!force && Time.time < nextPathRequestTime)
            {
                if (showDebugLogs) Debug.Log($"[AI-{enemyID}] RequestPath throttled ({(nextPathRequestTime - Time.time):F2}s)");
                return;
            }

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

            bool needNewPath =
                force ||
                path == null || path.Count == 0 ||
                targetIndex >= path.Count - minNodesRemainingForNewPath ||
                stuckRecoveryAttempts > 0 ||
                ((targetMode == TargetMode.FollowTarget || targetMode == TargetMode.FindByTag) && targetTransform != null &&
                 (Vector3.Distance(lastKnownTargetPos, currentTarget) > targetMoveRepathThreshold ||
                  (Time.time - lastRepathTime) > maxRepathIntervalWhileFollowing));

            if (!force && path != null && path.Count > 0)
            {
                Vector3 end = path[path.Count - 1];
                if ((end - currentTarget).sqrMagnitude <= 0.20f * 0.20f)
                    needNewPath = false;
            }

            // Repath lock (anti short-hop) — but allow tiny paths to repath out quickly
            if (needNewPath && !force)
            {
                bool cooldownActive = Time.time < repathLockUntil;
                bool notEnoughNodes = nodesAdvancedSinceRepath < minNodesAdvanceBeforeRepath;
                bool notEnoughTravel = Vector3.Distance(transform.position, posAtLastRepath) < minTravelBeforeRepath;

                // If our *current* path is tiny, don't enforce the lock too hard
                bool tinyPath = hasValidPath && path != null && path.Count <= 3;

                if (!tinyPath && (cooldownActive || notEnoughNodes || notEnoughTravel) && hasValidPath)
                    needNewPath = false;
            }

            if (!needNewPath) return;

            CacheAgentRadius();
            float clearance = agentRadius + navClearancePadding;

            Vector3 safeStart = NearestWalkableFromWorld(transform.position, 0.25f, 3.0f, 16, clearance);
            Vector3 safeTarget = NearestWalkableFromWorld(currentTarget, 0.25f, 3.0f, 16, clearance);
            if (safeStart == Vector3.negativeInfinity || safeTarget == Vector3.negativeInfinity)
            { nextPathRequestTime = Time.time + 0.25f; return; }

            if (avoidCongestionInPathing)
                safeTarget = PickDecongestedTarget(safeTarget, clearance);

            lastKnownTargetPos = safeTarget;
            lastRepathTime = Time.time;

            PathRequestManager.Instance.RequestPath(safeStart, safeTarget, clearance, OnPathFound, enemyID);
            nextPathRequestTime = force ? Time.time : Time.time + actualPathUpdateInterval;
        }

        // ---------- ACCEPT ----------
        void OnPathFound(List<Node> nodePath)
        {
            if (nodePath == null || nodePath.Count == 0)
            {
                hasValidPath = false; nextPathRequestTime = Time.time;
                if (stuckRecoveryAttempts > 0) FindAlternativeTarget();
                return;
            }

            // Nodes -> world positions
            var newPath = new List<Vector3>(nodePath.Count);
            for (int i = 0; i < nodePath.Count; i++) newPath.Add(nodePath[i].worldPosition);

            CacheAgentRadius();
            float clearance = agentRadius + navClearancePadding;

            // 1) Corner inset
            var inset = BendCornerInset(newPath, cornerInset, navBlockers, clearance, bendInsetMinDot);

            // 2) LOS smoothing
            var smoothed = usePathSmoothing && inset.Count > 2 ? ClearanceSmooth(inset) : inset;

            // 3) Base resample
            float step = Mathf.Max(0.2f, pathResampleStep);
            var resampled = ResamplePath(smoothed, step);

            // 4) GUARANTEE minimum waypoint count
            var ensured = EnsureMinWaypoints(resampled, Mathf.Max(3, minResampledWaypoints), step);

            // (Optional) lite choke check → detour request
            if (scanPathForCongestion && Time.time - lastChokeDetourTime >= chokeDetourCooldown)
            {
                if (TryFindChokePoint(ensured, out Vector3 chokePos, out int chokeCount))
                {
#if UNITY_EDITOR
                    if (showDebugLogs)
                        Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Lite choke near {chokePos:F2} (n={chokeCount})");
#endif
                    Vector3 detour = PickDecongestedTarget(chokePos, clearance);
                    if (Vector2.Distance(detour, chokePos) > 0.1f)
                    {
                        lastChokeDetourTime = Time.time;
                        PathRequestManager.Instance.RequestPath(transform.position, detour, clearance, OnPathFound, enemyID);
                        return;
                    }
                }
            }

            // Accept path & arm locks
            path = ensured;
            targetIndex = 0;
            hasValidPath = true;

            nodesAdvancedSinceRepath = 0;
            _lastObservedTargetIndex = 0;
            posAtLastRepath = transform.position;

            // If we still ended up with a short path (<=3), keep cooldown tiny so we can quickly repath out of it
            repathLockUntil = Time.time + (path.Count <= 3 ? Mathf.Min(0.25f, repathCooldownWhileAdvancing * 0.25f)
                                                           : repathCooldownWhileAdvancing);

#if UNITY_EDITOR
            if (showDebugLogs) Debug.Log($"<color=green>[AI]</color> Path nodes (resampled+ensured): {path.Count}");
#endif
        }

        // ---------- CORNER / SMOOTH ----------
        List<Vector3> BendCornerInset(List<Vector3> raw, float inset, LayerMask blockers, float radius, float minDotForInset)
        {
            if (raw.Count <= 2 || inset <= 0f) return raw;

            var outPath = new List<Vector3>(raw.Count);
            outPath.Add(raw[0]);

            for (int i = 1; i < raw.Count - 1; i++)
            {
                Vector3 prev = raw[i - 1];
                Vector3 p = raw[i];
                Vector3 next = raw[i + 1];

                Vector2 a = ((Vector2)p - (Vector2)prev);
                Vector2 b = ((Vector2)next - (Vector2)p);
                float aMag = a.magnitude;
                float bMag = b.magnitude;
                if (aMag < 1e-4f || bMag < 1e-4f) { outPath.Add(p); continue; }

                a /= aMag; b /= bMag;
                float dot = Vector2.Dot(a, b);

                if (dot < minDotForInset)
                {
                    Vector2 bis = (a + b);
                    if (bis.sqrMagnitude > 1e-6f)
                    {
                        bis = -bis.normalized;
                        Vector3 candidate = p + (Vector3)(bis * inset);

                        const int dirs = 8;
                        Vector2 repel = Vector2.zero;
                        for (int d = 0; d < dirs; d++)
                        {
                            float ang = (Mathf.PI * 2f / dirs) * d;
                            Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                            Vector2 probe = (Vector2)candidate + dir * radius;
                            if (Physics2D.OverlapCircle(probe, 0.02f, blockers) != null)
                                repel -= dir;
                        }
                        if (repel.sqrMagnitude > 1e-6f)
                            candidate += (Vector3)(repel.normalized * Mathf.Min(inset * 0.5f, 0.06f));

                        outPath.Add(candidate);
                        continue;
                    }
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
                if (outPath[outPath.Count - 1] != raw[far])
                    outPath.Add(raw[far]);
                i = far;
            }

            if (outPath[outPath.Count - 1] != raw[raw.Count - 1])
                outPath.Add(raw[raw.Count - 1]);

            return outPath;
        }

        bool HasClearanceAlongSegment(Vector2 from, Vector2 to)
        {
            float r = agentRadius + navClearancePadding;

            if (Physics2D.Linecast(from, to, navBlockers)) return false;

            float dist = Vector2.Distance(from, to);
            if (dist <= 0.0001f) return true;

            if (useCapsuleClearance)
            {
                RaycastHit2D hit = Physics2D.CapsuleCast(
                    from,
                    new Vector2(r * 2f, r * 2f),
                    CapsuleDirection2D.Vertical,
                    0f,
                    (to - from).normalized,
                    dist,
                    navBlockers
                );
                return hit.collider == null;
            }
            else
            {
                int steps = Mathf.Max(2, Mathf.CeilToInt(dist / Mathf.Max(0.04f, clearanceSampleStep)));
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
        }

        // ---------- RESAMPLER ----------
        List<Vector3> ResamplePath(List<Vector3> src, float step)
        {
            if (src == null || src.Count <= 1) return src;

            var outPath = new List<Vector3>(Mathf.Max(2, Mathf.CeilToInt(LengthOf(src) / step)));
            Vector3 curr = src[0];
            outPath.Add(curr);

            float remaining = step;

            for (int i = 1; i < src.Count; i++)
            {
                Vector3 a = curr;
                Vector3 b = src[i];
                float segLen = Vector3.Distance(a, b);

                while (segLen >= remaining)
                {
                    Vector3 dir = (b - a).normalized;
                    a += dir * remaining;
                    outPath.Add(a);
                    segLen -= remaining;
                    remaining = step;
                }

                curr = b;
                remaining -= segLen;
                if (remaining < 0.001f) remaining = step;
            }

            if (outPath[outPath.Count - 1] != src[src.Count - 1])
                outPath.Add(src[src.Count - 1]);

            return outPath;
        }

        // ---------- MIN WAYPOINT ENFORCER ----------
        List<Vector3> EnsureMinWaypoints(List<Vector3> src, int minCount, float step)
        {
            if (src == null || src.Count == 0) return src;

            if (src.Count >= minCount)
                return src;

            // Subdivide uniformly along the polyline to hit minCount
            float total = LengthOf(src);
            if (total <= 1e-4f)
                return src;

            int needed = Mathf.Max(minCount, Mathf.CeilToInt(total / Mathf.Max(0.1f, step)));

            var outPath = new List<Vector3>(needed);
            outPath.Add(src[0]);

            float spacing = total / (needed - 1);
            float distAccum = 0f;

            int segIdx = 0;
            float segPos = 0f; // distance along current segment
            while (outPath.Count < needed)
            {
                float targetDist = (outPath.Count) * spacing;

                // advance along src polyline to reach targetDist
                while (segIdx < src.Count - 1)
                {
                    float thisSegLen = Vector3.Distance(src[segIdx], src[segIdx + 1]);
                    if (segPos + thisSegLen >= targetDist)
                    {
                        float t = Mathf.InverseLerp(segPos, segPos + thisSegLen, targetDist);
                        Vector3 p = Vector3.Lerp(src[segIdx], src[segIdx + 1], t);
                        outPath.Add(p);
                        break;
                    }
                    else
                    {
                        segPos += thisSegLen;
                        segIdx++;
                    }
                }

                // safety: if we ran out of segments due to numeric issues, append last
                if (segIdx >= src.Count - 1 && outPath.Count < needed)
                {
                    outPath.Add(src[src.Count - 1]);
                }
            }

            // ensure exact end
            if (outPath[outPath.Count - 1] != src[src.Count - 1])
                outPath[outPath.Count - 1] = src[src.Count - 1];

            return outPath;
        }

        float LengthOf(List<Vector3> pts)
        {
            float L = 0f;
            for (int i = 1; i < pts.Count; i++)
                L += Vector3.Distance(pts[i - 1], pts[i]);
            return L;
        }

        // ---------- HELPERS ----------
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

        int CountAlliesNear(Vector2 pos, float radius)
        {
            if (allyMask.value == 0) allyMask = LayerMask.GetMask("Enemy");
            int count = Physics2D.OverlapCircleNonAlloc(pos, radius, _congBuf, allyMask);
            for (int i = 0; i < count; i++)
            {
                if (_congBuf[i] && _congBuf[i].gameObject == gameObject)
                {
                    _congBuf[i] = _congBuf[count - 1];
                    count--; i--;
                }
            }
            return count;
        }

        Vector3 PickDecongestedTarget(Vector3 target, float clearance)
        {
            int samples = Mathf.Max(0, congestionProbeSamples);
            if (samples == 0) return target;

            int baseNeighbors = Mathf.Max(0, CountAlliesNear(target, congestionProbeRadius));
            float bestCost = baseNeighbors;
            Vector3 best = target;

            float step = Mathf.PI * 2f / samples;
            for (int i = 0; i < samples; i++)
            {
                float ang = i * step;
                Vector3 cand = target + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * congestionProbeRing;

                Vector3 safe = NearestWalkableFromWorld(cand, 0.25f, 1.25f, 8, clearance);
                if (safe == Vector3.negativeInfinity) continue;

                int n = Mathf.Max(0, CountAlliesNear(safe, congestionProbeRadius));
                float distBias = Vector2.Distance((Vector2)target, (Vector2)safe) * congestionDistanceWeight;
                float cost = n + distBias;

                if (cost + 1e-3f < bestCost)
                {
                    bestCost = cost;
                    best = safe;
                }
            }

#if UNITY_EDITOR
            if (showDebugLogs && best != target)
                Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Decongest target: {baseNeighbors} → {CountAlliesNear(best, congestionProbeRadius)}");
#endif
            return best;
        }

        bool TryFindChokePoint(List<Vector3> candidatePath, out Vector3 chokePos, out int chokeCount)
        {
            chokePos = Vector3.zero; chokeCount = 0;

            if (!scanPathForCongestion || candidatePath == null || candidatePath.Count == 0)
                return false;

            int checks = Mathf.Min(chokeScanNodes, candidatePath.Count);
            int bestCount = 0;
            Vector3 bestPos = candidatePath[0];

            for (int i = 0; i < checks; i++)
            {
                Vector3 p = candidatePath[i];
                int n = CountAlliesNear(p, Mathf.Max(0.05f, chokeScanRadius));
                if (n > bestCount) { bestCount = n; bestPos = p; }
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
