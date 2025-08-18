// -----------------------------
// File: EnemyAICore.IdleMicro.cs (partial)
// Purpose: Subtle micro-movement while "idle" so enemies don't look frozen.
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Idle Micro-Movement")]
        [Tooltip("Enable subtle idle drift when waiting/scanning.")]
        public bool enableIdleMicroMovement = true;

        [Tooltip("Max drift speed while idling (units/sec). Kept well below moveSpeed.")]
        public float idleMicroSpeed = 0.35f;

        [Tooltip("How often to pick a new idle direction (min..max seconds).")]
        public Vector2 idleMicroDirInterval = new Vector2(0.9f, 1.8f);

        [Tooltip("Ray distance to avoid immediately walking into walls during idle drift.")]
        public float idleMicroObstacleProbe = 0.35f;

        // runtime state
        private Vector2 idleMicroDir = Vector2.zero;
        private float idleMicroTimer = 0f;

        // Call from Core.Update() after CalculateDesiredVelocity()
        private void IdleMicroTick(float dt)
        {
            if (!enableIdleMicroMovement) return;
            if (isRecovering || isKnockedBack) return;

            // Drift only when effectively idle/waiting (no meaningful desired motion).
            bool noPath = (path == null || path.Count == 0);
            bool atEnd = hasValidPath && targetIndex >= path.Count - 1 &&
                         Vector2.Distance(transform.position, path[path.Count - 1]) <= nodeReachDistance * 2f;
            bool nearlyStill = desiredVelocity.sqrMagnitude < 0.01f && rb.velocity.sqrMagnitude < 0.04f;

            if (!(noPath || atEnd || nearlyStill)) return;

            // Pick/refresh direction periodically and avoid immediate wall bumps.
            idleMicroTimer -= dt;
            if (idleMicroTimer <= 0f || idleMicroDir == Vector2.zero)
            {
                idleMicroDir = Random.insideUnitCircle.normalized;

                if (idleMicroObstacleProbe > 0f)
                {
                    // Respect walls/obstacles when picking the drift direction.
                    var hit = Physics2D.Raycast(transform.position, idleMicroDir, idleMicroObstacleProbe, recoveryBlockers);
                    if (hit.collider != null)
                    {
                        // try perpendicular; otherwise reverse
                        Vector2 alt = new Vector2(-idleMicroDir.y, idleMicroDir.x);
                        var hit2 = Physics2D.Raycast(transform.position, alt, idleMicroObstacleProbe, recoveryBlockers);
                        idleMicroDir = (hit2.collider == null) ? alt : -idleMicroDir;
                    }
                }

                float tMin = Mathf.Max(0.2f, idleMicroDirInterval.x);
                float tMax = Mathf.Max(idleMicroDirInterval.x, idleMicroDirInterval.y);
                idleMicroTimer = Random.Range(tMin, tMax);
            }

            // Nudge desiredVelocity slightly so posture/anims look alive.
            float maxIdleSpeed = Mathf.Min(idleMicroSpeed, moveSpeed * 0.35f);
            desiredVelocity += idleMicroDir * maxIdleSpeed;

            if (desiredVelocity.magnitude > maxIdleSpeed)
                desiredVelocity = desiredVelocity.normalized * maxIdleSpeed;
        }
    }
}
