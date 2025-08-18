// ================================
// File: EnemyAICore.Movement.cs
// ================================
using UnityEngine;
using System.Collections.Generic;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        // --- New tuneables for segment-follow ---
        [SerializeField] private float lookAheadDistance = 0.6f;   // aim ahead on segment
        [SerializeField] private float dynamicNodeRadiusFactor = 0.25f; // scales with speed
        [SerializeField] private float sharpTurnSlowAngle = 120f;  // degrees to consider a hairpin
        [SerializeField] private float sharpTurnSpeedScale = 0.6f; // slow factor on hairpins

        // --- Human-like movement constraints (NEW) ---
        [Header("Human Movement")]
        [SerializeField] private float maxTurnRateDeg = 280f;        // limit how fast heading can rotate
        [SerializeField] private float maxAccel = 40f;               // units/sec^2 toward target velocity
        [SerializeField] private float maxDecel = 60f;               // units/sec^2 when slowing/reversing
        [SerializeField, Range(0f, 0.3f)]
        private float desiredVelSmoothing = 0.10f;                   // low-pass on target vel (blend factor)
        [SerializeField] private float arrivalSlowdownRadius = 1.6f; // ease off near the path end

        // Internal state (NEW)
        private Vector2 _heading = Vector2.right;      // smoothed facing/intent direction
        private Vector2 _targetVelFiltered = Vector2.zero;

        void CalculateDesiredVelocity()
        {
            // Path must exist and have at least one point
            if (path == null || path.Count == 0)
            { desiredVelocity = Vector2.zero; return; }

            // Clamp targetIndex into valid range for any access below
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex >= path.Count) targetIndex = path.Count - 1;

            // Handle single-point paths explicitly to avoid segment math
            if (path.Count == 1)
            {
                Vector2 goal = path[0];
                float dist = Vector2.Distance(transform.position, goal);
                if (dist <= nodeReachDistance) { desiredVelocity = Vector2.zero; return; }

                Vector2 dir1 = (goal - (Vector2)transform.position);
                if (dir1.sqrMagnitude < 0.0001f) { desiredVelocity = Vector2.zero; return; }
                dir1.Normalize();

                // Arrival slowdown (human-like easing near goal)
                float speedTarget = moveSpeed;
                float slowT1 = Mathf.Clamp01(dist / Mathf.Max(arrivalSlowdownRadius, 0.0001f));
                speedTarget *= Mathf.SmoothStep(0f, 1f, slowT1);

                Vector2 avoidance1 = CalculateAvoidance();
                Vector2 desiredDir1 = (avoidance1.sqrMagnitude > 0.001f && prioritizeAvoidance)
                    ? (dir1 + avoidance1 * avoidanceStrength).normalized
                    : dir1;

                ApplyHumanConstraints(desiredDir1, speedTarget);
                // Debug helpers
                Debug.DrawLine(transform.position, goal, Color.magenta); // aim point
                Debug.DrawRay(transform.position, desiredVelocity, Color.green);
                return;
            }

            // If we're basically at the end, stop
            if (targetIndex >= path.Count - 1 && Vector2.Distance(transform.position, path[path.Count - 1]) < nodeReachDistance)
            { desiredVelocity = Vector2.zero; return; }

            // Advance waypoint index with dynamic radius to avoid ping-pong
            float dynRadius = Mathf.Max(nodeReachDistance, rb.velocity.magnitude * dynamicNodeRadiusFactor);
            while (targetIndex < path.Count - 1 && Vector2.Distance(transform.position, path[targetIndex]) < dynRadius)
                targetIndex++;

            // Re-clamp after advancing, then build current segment [A -> B]
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex >= path.Count) targetIndex = path.Count - 1;

            int i = Mathf.Clamp(targetIndex, 0, path.Count - 2);
            Vector2 A = path[i];
            Vector2 B = path[i + 1];
            Vector2 AB = B - A;
            float abLen = AB.magnitude;
            if (abLen < 0.0001f) { desiredVelocity = Vector2.zero; return; }
            Vector2 abDir = AB / abLen;

            // Project onto segment and look ahead
            Vector2 AP = (Vector2)transform.position - A;
            float t = Mathf.Clamp(Vector2.Dot(AP, abDir) / Mathf.Max(abLen, 0.0001f), 0f, 1f);
            float tLook = Mathf.Clamp(t + (lookAheadDistance / Mathf.Max(abLen, 0.0001f)), 0f, 1f);
            Vector2 aimPoint = A + abDir * (tLook * abLen);

            if (t > 0.98f && targetIndex < path.Count - 1) targetIndex++;

            Vector2 dir = (aimPoint - (Vector2)transform.position);
            if (dir.sqrMagnitude < 0.0001f) { desiredVelocity = Vector2.zero; return; }
            dir.Normalize();

            // Slow down on sharp upcoming turn
            float speed = moveSpeed;
            if (i + 2 < path.Count)
            {
                Vector2 nextDir = ((Vector2)path[i + 2] - B).normalized;
                float angle = Vector2.Angle(abDir, nextDir);
                if (angle > sharpTurnSlowAngle) speed *= sharpTurnSpeedScale;
            }

            // Arrival slowdown near overall end of path (NEW)
            float distToEnd = Vector2.Distance(transform.position, path[path.Count - 1]);
            float slowT = Mathf.Clamp01(distToEnd / Mathf.Max(arrivalSlowdownRadius, 0.0001f));
            speed *= Mathf.SmoothStep(0f, 1f, slowT);

            // Avoidance blend
            Vector2 avoidance = CalculateAvoidance();
            Vector2 desiredDir = (avoidance.sqrMagnitude > 0.001f && prioritizeAvoidance)
                ? (dir + avoidance * avoidanceStrength).normalized
                : dir;

            // Apply human constraints (turn rate, accel/decel, mild smoothing)
            ApplyHumanConstraints(desiredDir, speed);

            // Debug helpers
            Debug.DrawLine(transform.position, aimPoint, Color.magenta);   // aim point
            Debug.DrawLine(A, B, Color.yellow);                            // current segment
            Debug.DrawRay(transform.position, desiredVelocity, Color.green);
        }

        // Apply heading limit, target velocity smoothing, and accel/decels caps (NEW)
        private void ApplyHumanConstraints(Vector2 desiredDir, float desiredSpeed)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            // 1) Limit turn rate: rotate heading toward desiredDir
            if (_heading.sqrMagnitude < 1e-6f) _heading = desiredDir;
            float maxDelta = maxTurnRateDeg * dt;
            _heading = RotateTowards(_heading, desiredDir, maxDelta);

            // 2) Compose target velocity from limited heading
            Vector2 targetVel = _heading * Mathf.Max(0f, desiredSpeed);

            // 3) Mild low-pass on target velocity to damp twitch
            float alpha = Mathf.Clamp01(desiredVelSmoothing); // frame-rate independent enough for small values
            _targetVelFiltered = Vector2.Lerp(_targetVelFiltered, targetVel, alpha);

            // 4) Acceleration / deceleration caps toward filtered target
            Vector2 dv = _targetVelFiltered - desiredVelocity;
            bool slowing = (Vector2.Dot(dv, desiredVelocity) < 0f);
            float limit = slowing ? maxDecel : maxAccel;
            float maxStep = limit * dt;

            if (dv.magnitude > maxStep)
                dv = dv.normalized * maxStep;

            desiredVelocity += dv;
        }

        // Helper: rotate 2D vector 'from' toward 'to' by at most 'deg' degrees this frame
        private static Vector2 RotateTowards(Vector2 from, Vector2 to, float deg)
        {
            if (from.sqrMagnitude < 1e-6f) return to.normalized;
            if (to.sqrMagnitude < 1e-6f) return from.normalized;

            from.Normalize(); to.Normalize();
            float angle = Vector2.SignedAngle(from, to);
            float step = Mathf.Clamp(angle, -deg, deg);
            float final = step * Mathf.Deg2Rad;

            float s = Mathf.Sin(final);
            float c = Mathf.Cos(final);
            // rotate 'from' by 'final' radians
            return new Vector2(from.x * c - from.y * s, from.x * s + from.y * c).normalized;
        }

        Vector2 CalculateAvoidance()
        {
            Vector2 avoidance = Vector2.zero; int count = 0;
            float actualRadius = isRecovering ? avoidanceRadius * 2f : avoidanceRadius;
            Collider2D[] nearby = Physics2D.OverlapCircleAll(transform.position, actualRadius, LayerMask.GetMask("Enemy"));
            foreach (var col in nearby)
            {
                if (col.gameObject == gameObject) continue;
                Vector2 away = (Vector2)(transform.position - col.transform.position); float d = away.magnitude;
                if (d > 0.01f && d < actualRadius)
                { float strength = Mathf.Pow(1f - (d / actualRadius), 2f); avoidance += away.normalized * strength; count++; }
            }
            if (count > 0) avoidance = avoidance.normalized;
            return avoidance;
        }
    }
}
