// -----------------------------
// File: EnemyAICore.Recovery.cs
// Purpose: Robust stuck detection with intent gating + movement holds
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore
    {
        // --- Movement holds (states can pause movement intentionally) ---
        [Header("Movement Holds")]
        [SerializeField] private bool respectMovementHolds = true;
        private float movementHoldUntil = 0f;
        internal void BeginMovementHold(float seconds) => movementHoldUntil = Mathf.Max(movementHoldUntil, Time.time + seconds);
        internal void ClearMovementHold() => movementHoldUntil = 0f;
        internal bool MovementHoldActive => Time.time < movementHoldUntil;

        // --- Stuck detection (refined) ---
        [Header("Stuck Detection (Refined)")]
        [SerializeField] private float minSpeedForProgress = 0.12f;      // below this = "very slow"
        [SerializeField] private float minDesiredSpeedForStuck = 0.15f;  // if desire < this, we aren't trying to move
        [SerializeField] private float nodeProgressEpsilon = 0.06f;      // must get this much closer to waypoint
        [SerializeField] private float noProgressWindow = 0.65f;         // time with no progress before arming
        [SerializeField] private float repathPendingGrace = 0.75f;       // grace after we asked for a path
        [SerializeField] private int highQueueGraceThreshold = 40;     // queue > this? add more grace
        [SerializeField] private float highQueueExtraGrace = 0.5f;       // extra grace when queue is high
        [SerializeField] private float nearWaypointRelax = 2.0f;         // multiplier on nodeReachDistance for "we’re basically there"

        private float nodeNoProgressTimer = 0f;
        private float lastWaypointDist = float.MaxValue;
        private int lastProgressIndex = -1;

        // --- Congestion recovery (kept) ---
        [Header("Congestion Recovery")]
        [SerializeField] private bool enableCongestionRecovery = true;
        [SerializeField] private float congestionNeighborRadius = 1.1f;
        [SerializeField] private int congestionThreshold = 3;
        [SerializeField] private float congestionDwellTime = 0.35f;
        [SerializeField] private float yieldDuration = 0.40f;
        [SerializeField] private float yieldCooldown = 1.5f;
        [SerializeField] private float yieldSpeedScale = 0.65f;
        [SerializeField] private LayerMask allyMask;

        private float congestionTimer = 0f;
        private float lastYieldTime = -999f;
        private readonly Collider2D[] congestionHits = new Collider2D[32];

        private enum RecoveryKind { Bounce, Yield }
        private RecoveryKind recoveryKind = RecoveryKind.Bounce;

        void CheckIfStuck()
        {
            // 0) No path -> don't arm stuck; watchdog in Core will request a path.
            if (path == null || path.Count == 0)
            {
                stuckTimer = 0f;
                nodeNoProgressTimer = 0f;
                lastWaypointDist = float.MaxValue;
                lastProgressIndex = -1;
                return;
            }

            // 1) If a state is intentionally holding movement (e.g., scanning), don't arm stuck.
            if (respectMovementHolds && MovementHoldActive)
            {
                stuckTimer = 0f;
                nodeNoProgressTimer = 0f;
                return;
            }

            // 2) Soft jam resolution first (yield sidestep)
            if (enableCongestionRecovery && !isRecovering && CheckCongestionAndMaybeYield())
                return;

            // 3) Grace while a fresh path is pending or queue is high
            float sinceLastRepath = Time.time - lastRepathTime;
            bool graceRepath = sinceLastRepath < repathPendingGrace;

            bool graceQueue = false;
            if (PathRequestManager.Instance != null)
                graceQueue = PathRequestManager.Instance.GetQueueLength() > highQueueGraceThreshold;

            if (graceRepath || graceQueue)
            {
                float extra = graceQueue ? highQueueExtraGrace : 0f;
                stuckTimer = 0f;
                nodeNoProgressTimer = Mathf.Max(0f, nodeNoProgressTimer - Time.deltaTime - extra);
                return;
            }

            // 4) Intent gating: if we aren't *trying* to move, don't arm stuck.
            if (desiredVelocity.magnitude < minDesiredSpeedForStuck)
            {
                stuckTimer = 0f;
                nodeNoProgressTimer = 0f;
                return;
            }

            // 5) If we're basically at the current waypoint, relax stuck (we may be waiting for next pick)
            int idx = Mathf.Clamp(targetIndex, 0, path.Count - 1);
            Vector3 waypoint = path[idx];
            float dist = Vector2.Distance(transform.position, waypoint);
            if (dist <= nodeReachDistance * Mathf.Max(1.0f, nearWaypointRelax))
            {
                stuckTimer = 0f;
                nodeNoProgressTimer = 0f;
                lastWaypointDist = dist;
                lastProgressIndex = idx;
                return;
            }

            // 6) True progress gating: require BOTH (very slow) AND (no progress to waypoint for a window)
            if (idx != lastProgressIndex)
            {
                lastProgressIndex = idx;
                lastWaypointDist = dist;
                nodeNoProgressTimer = 0f;
            }

            bool progressed = (lastWaypointDist - dist) > nodeProgressEpsilon;
            if (progressed)
            {
                nodeNoProgressTimer = 0f;
                stuckTimer = 0f;
                lastWaypointDist = dist;
                return;
            }
            else
            {
                nodeNoProgressTimer += Time.deltaTime;
                lastWaypointDist = Mathf.Min(lastWaypointDist, dist + 0.01f);
            }

            bool verySlow = (rb != null ? rb.velocity.magnitude : 0f) < minSpeedForProgress;

            if (verySlow && nodeNoProgressTimer >= noProgressWindow)
            {
                stuckTimer += Time.deltaTime;
                if (stuckTimer >= stuckDetectionTime)
                {
                    if (showDebugLogs)
                        Debug.Log($"<color=orange>[AI-{enemyID}]</color> Stuck (intent={desiredVelocity.magnitude:0.00}, speed={(rb ? rb.velocity.magnitude : 0f):0.00}, no-progress={nodeNoProgressTimer:0.00}s). Attempt {stuckRecoveryAttempts + 1}");
                    InitiateStuckRecovery();
                }
            }
            else
            {
                stuckTimer = 0f;
            }
        }

        bool CheckCongestionAndMaybeYield()
        {
            if (Time.time - lastYieldTime < yieldCooldown) { congestionTimer = 0f; return false; }
            if (allyMask.value == 0) allyMask = LayerMask.GetMask("Enemy");

            int hitCount = Physics2D.OverlapCircleNonAlloc(transform.position, congestionNeighborRadius, congestionHits, allyMask);
            if (hitCount <= 1) { congestionTimer = 0f; return false; }

            int neighbors = 0;
            Vector2 sum = Vector2.zero;
            int minId = enemyID;

            for (int i = 0; i < hitCount; i++)
            {
                var c = congestionHits[i];
                if (c == null || c.gameObject == gameObject) continue;
                var other = c.GetComponentInParent<EnemyAICore>();
                if (other == null || !other.enabled) continue;

                neighbors++;
                sum += (Vector2)other.transform.position;
                if (other.enemyID >= 0) minId = Mathf.Min(minId, other.enemyID);
            }

            if (neighbors < congestionThreshold) { congestionTimer = 0f; return false; }

            congestionTimer += Time.deltaTime;
            if (congestionTimer < congestionDwellTime) return false;

            if (enemyID == minId) return false;

            Vector2 centroid = sum / Mathf.Max(1, neighbors);
            Vector2 away = ((Vector2)transform.position - centroid);
            if (away.sqrMagnitude < 1e-4f) away = Random.insideUnitCircle;

            InitiateYieldEvade(away.normalized);
            return true;
        }

        void InitiateYieldEvade(Vector2 dir)
        {
            recoveryKind = RecoveryKind.Yield;

            stuckTimer = 0f;
            nodeNoProgressTimer = 0f;
            lastWaypointDist = float.MaxValue;
            lastProgressIndex = -1;
            stuckCheckPosition = transform.position;

            isRecovering = true;
            recoveryDirection = dir.normalized;
            recoveryTimer = yieldDuration;
            recoveryBounces = 0;
            totalRecoveryClock = 0f;

            path = null; hasValidPath = false;

            if (showDebugLogs)
                Debug.Log($"<color=yellow>[AI-{enemyID}]</color> Yielding out of congestion for {yieldDuration:0.00}s");
        }

        void InitiateStuckRecovery()
        {
            recoveryKind = RecoveryKind.Bounce;

            stuckRecoveryAttempts++;
            stuckTimer = 0f;
            nodeNoProgressTimer = 0f;
            lastWaypointDist = float.MaxValue;
            lastProgressIndex = -1;
            stuckCheckPosition = transform.position;

            if (stuckRecoveryAttempts > maxStuckRecoveryAttempts)
            {
                if (showDebugLogs) Debug.LogWarning($"<color=red>[AI]</color> {gameObject.name} failed to recover after {maxStuckRecoveryAttempts} attempts. Finding new target.");
                FindAlternativeTarget();
                stuckRecoveryAttempts = 0;
                return;
            }

            path = null; hasValidPath = false; isRecovering = true; recoveryTimer = recoveryDuration; recoveryBounces = 0; totalRecoveryClock = 0f;

            Vector2 avoidance = CalculateAvoidance() * 2f;
            recoveryDirection = (avoidance.magnitude > 0.1f ? avoidance : FindOpenDirection()).normalized;

            if (showDebugLogs) Debug.Log($"<color=yellow>[AI]</color> {gameObject.name} using recovery movement");
        }

        Vector2 FindOpenDirection()
        {
            Vector2 bestDirection = Vector2.right; float maxDistance = 0f;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f; Vector2 checkDir = Quaternion.Euler(0, 0, angle) * Vector2.right;
                RaycastHit2D hit = Physics2D.Raycast(transform.position, checkDir, stuckRecoveryRadius, recoveryBlockers);
                float distance = hit.collider != null ? hit.distance : stuckRecoveryRadius;
                if (distance > maxDistance) { maxDistance = distance; bestDirection = checkDir; }
            }
            return bestDirection;
        }

        void HandleRecoveryMovement()
        {
            recoveryTimer -= Time.deltaTime;
            totalRecoveryClock += Time.deltaTime;

            if (totalRecoveryClock >= maxTotalRecoveryTime)
            {
                HardResetToNearestWalkable();
                return;
            }

            if (recoveryTimer <= 0f)
            {
                isRecovering = false;
                stuckCheckPosition = transform.position;

                if (recoveryKind == RecoveryKind.Yield)
                {
                    lastYieldTime = Time.time;
                    if (showDebugLogs) Debug.Log($"<color=green>[AI-{enemyID}]</color> Yield complete, requesting new path");
                }
                else
                {
                    if (showDebugLogs) Debug.Log($"<color=green>[AI-{enemyID}]</color> Recovery complete, requesting new path");
                }

                nextPathRequestTime = Time.time;
                RequestPath();
            }
        }

        void RecoveryStep()
        {
            float speed = (recoveryKind == RecoveryKind.Yield) ? recoverySpeed * yieldSpeedScale : recoverySpeed;
            float step = speed * Time.fixedDeltaTime;
            Vector2 dir = recoveryDirection.sqrMagnitude > 1e-6f ? recoveryDirection.normalized : Vector2.right;

            ContactFilter2D cf = new ContactFilter2D { layerMask = recoveryBlockers, useLayerMask = true, useTriggers = false };
            RaycastHit2D[] hits = new RaycastHit2D[1];
            int hitCount = rb.Cast(dir, cf, hits, step);

            if (hitCount > 0)
            {
                Vector2 n = hits[0].normal;
                recoveryDirection = Vector2.Reflect(dir, n).normalized;
                recoveryBounces++;

                if (recoveryBounces > recoveryMaxBounces)
                {
                    recoveryDirection = FindOpenDirection().normalized;
                    recoveryBounces = 0;
                }
            }
            else
            {
                rb.MovePosition(rb.position + dir * step);
            }

            HandleRecoveryMovement();
        }

        void HardResetToNearestWalkable()
        {
            isRecovering = false;
            stuckRecoveryAttempts = 0;
            stuckTimer = 0f;
            nodeNoProgressTimer = 0f;
            lastWaypointDist = float.MaxValue;
            lastProgressIndex = -1;
            recoveryBounces = 0;
            totalRecoveryClock = 0f;

            CacheAgentRadius();
            float clearance = agentRadius + navClearancePadding;

            Vector3 safePos = NearestWalkableFromWorld(transform.position, 0.25f, 3.0f, 24, clearance);
            if (safePos != Vector3.negativeInfinity)
            {
                rb.position = safePos;
                rb.velocity = Vector2.zero;
            }

            nextPathRequestTime = Time.time;
            RequestPath();
        }
    }
}
