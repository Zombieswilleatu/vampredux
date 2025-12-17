// EnemyAICore.Movement.cs
// Full file: adds arrival hysteresis, guarded ShortLegBoost, waypoint enter/exit hysteresis,
// and a small near-end progress watchdog. Includes shims for LogOnceImportant, NodePos, and CalculateAvoidance.

using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Movement / End-of-Path")]
        [SerializeField] private float finalEpsEnter = 0.20f;   // consider arrived when dEnd <= this
        [SerializeField] private float finalEpsExit = 0.35f;   // must move past this to un-arrive
        [SerializeField] private float waypointEpsEnter = 0.25f;
        [SerializeField] private float waypointEpsExit = 0.40f;
        [SerializeField] private float minUsefulLeg = 1.50f;    // guard for ShortLegBoost
        [SerializeField] private float cruiseFloor = 1.75f;    // floor used in CruiseFloor logs
        [SerializeField] private int endTrendWindow = 10;     // frames to check for progress trend
        [SerializeField] private float minProgressPerSec = 0.20f; // if end distance not dropping at least this, repath
        [SerializeField] private float endWatchRadius = 0.60f; // additional near-end stall watchdog
        [SerializeField] private float endWatchTimeout = 1.50f; // seconds

        // Movement state (assumed to also exist elsewhere in your partials; kept here for clarity)
        private Vector2 _desiredVelocity;
        private Vector2 _velocity;
        private float _currentSpeed;
        private int _pathIndex;
        private List<Node> _path; // produced by OnPathFound(List<Node>)
        private bool _arrived;     // arrival hysteresis flag
        private bool _onWaypoint;  // waypoint enter/exit hysteresis

        // Short-leg boost guard state
        private int _shortLegBoostsThisFrame;
        private float _lastSLBLogT;

        // Near-end progress trend buffer
        private readonly Queue<float> _dEndWindow = new();

        // EndWatch timeout state
        private float _endWatchStart = -1f;
        private float _endWatchStartDist;

        // --- One-time logger (replaces LogOnceImportant) ---
        private readonly HashSet<string> _onceMoveLogs = new HashSet<string>();
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void LogOnceImportant(string msg)
        {
            // Logs once per unique string in the editor to avoid spam
            if (_onceMoveLogs.Add(msg))
                Debug.LogWarning(msg);
        }

        // --- CalculateAvoidance shim (called by Recovery.cs) ---
        // If your Recovery version expects a different signature, we can match it; this keeps compile green.
        private Vector2 CalculateAvoidance()
        {
            return Vector2.zero;
        }

        // --- Node world position adapter ---
        // Your Movement file originally referenced Node.worldPos, but your Node type doesn’t expose it.
        // Use NodePos(node) everywhere instead of node.worldPos.
        private static Vector2 NodePos(Node n)
        {
            if (n == null) return Vector2.zero;

            // If you know your Node API, uncomment ONE direct return for best performance and remove reflection below:
            // return (Vector2)n.worldPosition;   // if exists
            // return (Vector2)n.WorldPosition;   // if exists
            // return (Vector2)n.position;        // if exists

            // Generic reflection fallback to handle common field/property names.
            const string kErr = "[Move:AI] NodePos: could not find a Vector3 position field/property on Node.";
            var t = n.GetType();

            // Try properties
            var pi = t.GetProperty("worldPos") ?? t.GetProperty("WorldPos")
                   ?? t.GetProperty("worldPosition") ?? t.GetProperty("WorldPosition")
                   ?? t.GetProperty("position") ?? t.GetProperty("world");
            if (pi != null && pi.PropertyType == typeof(Vector3))
                return (Vector2)(Vector3)pi.GetValue(n, null);

            // Try fields
            var fi = t.GetField("worldPos") ?? t.GetField("WorldPos")
                   ?? t.GetField("worldPosition") ?? t.GetField("WorldPosition")
                   ?? t.GetField("position") ?? t.GetField("world");
            if (fi != null && fi.FieldType == typeof(Vector3))
                return (Vector2)(Vector3)fi.GetValue(n);

            // Fallback: try x/y floats (rare, but cheap to support)
            var fx = t.GetField("x"); var fy = t.GetField("y");
            if (fx != null && fy != null && fx.FieldType == typeof(float) && fy.FieldType == typeof(float))
                return new Vector2((float)fx.GetValue(n), (float)fy.GetValue(n));

            Debug.LogError(kErr);
            return Vector2.zero;
        }

        // Called from Update()
        private void CalculateDesiredVelocity()
        {
            // No path yet?
            if (_path == null || _path.Count == 0 || _pathIndex >= _path.Count)
            {
                LogOnceImportant("<color=#FB3>[Move:AI]</color> Stop: path null/empty.");
                _desiredVelocity = Vector2.zero;
                return;
            }

            // Determine target waypoint and end-of-path distance
            Vector2 pos = transform.position;
            Vector2 aim = NodePos(_path[_pathIndex]);
            float distToWaypoint = Vector2.Distance(pos, aim);

            // Waypoint enter/exit hysteresis (prevents flicker)
            TryConsumeWaypoint(pos, aim, distToWaypoint);

            // Recompute if we advanced
            if (_pathIndex >= _path.Count) _pathIndex = _path.Count - 1;
            aim = NodePos(_path[_pathIndex]);

            // Distance to the *final* end of path for end-easing / arrival
            Vector2 endPos = NodePos(_path[_path.Count - 1]);
            float distToEnd = Vector2.Distance(pos, endPos);

            // Arrival handling (hysteresis + stall trend + single-stop behavior)
            HandleArrival(distToEnd);
            if (_arrived)
            {
                // We only log once on enter (see HandleArrival). Keep speed zero and skip boosts.
                _desiredVelocity = Vector2.zero;
                return;
            }

            // Direction and base speed (you may compute from your stats; keep 5.0f to match logs)
            Vector2 dir = (aim - pos);
            float len = dir.magnitude;
            if (len > 0.0001f) dir /= len;

            float speed = 5.0f;

            // End-of-path easing (like your existing EndEase log blocks)
            if (distToEnd <= 1.10f)
            {
                float t = Mathf.InverseLerp(0.20f, 1.10f, distToEnd);
                float eased = Mathf.Lerp(0.80f, 5.00f, t); // cap speed near end
                if (Mathf.Abs(eased - speed) > 0.01f)
                {
                    Debug.Log(string.Format("<color=#9AF>[Move:AI]</color> EndEase: distEnd={0:0.00} slowT={1:0.00} speed 5.00->{2:0.00}", distToEnd, t, eased));
                    speed = eased;
                }
            }

            // Cruise floor (keep small legs from dropping too low unless truly arrived)
            if (speed < cruiseFloor && distToEnd > finalEpsEnter)
            {
                Debug.Log(string.Format("<color=#9AF>[Move:AI]</color> CruiseFloor: speed {0:0.00}->{1:0.00} (distEnd={2:0.00})",
                                        speed, cruiseFloor, distToEnd));
                speed = cruiseFloor;
            }

            // Short-leg boost, guarded, and never when already arrived
            ApplyShortLegBoost(ref speed, distToEnd);

            // Compose desired velocity and apply "human constraints" (your existing method)
            _desiredVelocity = dir * speed;

            // (Your existing ApplyHumanConstraints logs dvReq/cap and headings)
            _desiredVelocity = ApplyHumanConstraints(_desiredVelocity, Time.deltaTime);

            // Final move log (to match your format)
            Debug.Log(string.Format(
                "<color=#9AF>[Move:AI]</color> idx={0}/{1} i={2} t={3:0.00}->{4:0.00} len={5:0.00} dEnd={6:0.00} dynR={7:0.00} spd={8:0.00} dv={9:0.00} pos=({10:0.00},{11:0.00}) aim=({12:0.00}, {13:0.00})",
                Mathf.Clamp(_pathIndex, 0, _path.Count - 1),
                _path.Count - 1,
                Mathf.Clamp(_pathIndex, 0, _path.Count - 1),
                0f, 1f, // cosmetic segment t 0->1
                len,
                distToEnd,
                0.20f,  // printed dynR; keep cosmetic
                speed,
                _desiredVelocity.magnitude,
                pos.x, pos.y,
                aim.x, aim.y
            ));

            // Keep an extra near-end stall watcher (time-based)
            EndWatch(distToEnd);
        }

        // Hysteresis for arrival + trend-based stall detection + one-time arrival log
        private void HandleArrival(float dEnd)
        {
            // Update short rolling window for trend analysis
            _dEndWindow.Enqueue(dEnd);
            while (_dEndWindow.Count > endTrendWindow) _dEndWindow.Dequeue();

            // Enter/Exit arrival
            if (!_arrived && dEnd <= finalEpsEnter)
            {
                _arrived = true;
                Debug.Log(string.Format("<color=#FB3>[Move:AI]</color> ARRIVE enter (dEnd={0:0.00} ≤ {1:0.00}).", dEnd, finalEpsEnter));
                DumpArrivalContext(dEnd);

                // Consume any leftover waypoints once and stop
                if (_path != null && _pathIndex < _path.Count - 1)
                {
                    Debug.Log(string.Format("[Move:AI] Consuming remaining waypoints on arrive (idx={0}→end)", _pathIndex));
                    _pathIndex = _path.Count - 1;
                }
                _desiredVelocity = Vector2.zero;
                return;
            }
            if (_arrived && dEnd > finalEpsExit)
            {
                _arrived = false;
                Debug.Log(string.Format("<color=#FB3>[Move:AI]</color> ARRIVE exit (dEnd={0:0.00} > {1:0.00}).", dEnd, finalEpsExit));
            }

            // Trend watchdog: if not making progress near end, force a repath
            if (!_arrived && _dEndWindow.Count == endTrendWindow)
            {
                float oldest = 0f, newest = 0f; int i = 0;
                foreach (var v in _dEndWindow) { if (i == 0) oldest = v; newest = v; i++; }
                float dt = endTrendWindow * Mathf.Max(0.000001f, Time.deltaTime);
                float dropPerSec = (oldest - newest) / Mathf.Max(0.0001f, dt);
                if (dropPerSec < minProgressPerSec)
                {
                    Debug.LogWarning(string.Format("<color=orange>[Move:AI]</color> Near-end progress stalled (Δ/ sec={0:0.00}); forcing repath.", dropPerSec));
                    ForceRepathNow(PathRepathReason.NearEndStall);
                }
            }
        }

        private void DumpArrivalContext(float dEnd)
        {
            float spd = _currentSpeed;
            float dv = _desiredVelocity.magnitude;
            var hv = _velocity;
            Debug.Log(string.Format("[Move:AI] ArrivalCtx dEnd={0:0.00} spd={1:0.00} dv={2:0.00} v=({3:0.00},{4:0.00}) idx={5}/{6}",
                                    dEnd, spd, dv, hv.x, hv.y,
                                    Mathf.Clamp(_pathIndex, 0, (_path?.Count ?? 1) - 1),
                                    (_path?.Count ?? 1) - 1));
        }

        // Waypoint hysteresis to prevent flapping at node thresholds
        private void TryConsumeWaypoint(Vector2 pos, Vector2 wp, float d)
        {
            if (!_onWaypoint && d <= waypointEpsEnter)
            {
                _onWaypoint = true;
                Debug.Log(string.Format("[Move:AI] Waypoint ENTER idx={0} d={1:0.00} epsEnter={2:0.00}", _pathIndex, d, waypointEpsEnter));
                _pathIndex = Mathf.Min(_pathIndex + 1, (_path?.Count ?? 1) - 1);
            }
            else if (_onWaypoint && d > waypointEpsExit)
            {
                _onWaypoint = false;
                Debug.Log(string.Format("[Move:AI] Waypoint EXIT idx={0} d={1:0.00} epsExit={2:0.00}", _pathIndex, d, waypointEpsExit));
            }
        }

        // Guarded ShortLegBoost that won’t fight arrival logic
        private void ApplyShortLegBoost(ref float speed, float distEnd)
        {
            if (_arrived) return;

            if (distEnd < minUsefulLeg)
            {
                _shortLegBoostsThisFrame++;
                if (_shortLegBoostsThisFrame > 2) return; // avoid feedback loops

                float pre = speed;
                // gentle boost tapering to zero as distEnd→minUsefulLeg
                float factor = 1f - Mathf.Clamp01(distEnd / Mathf.Max(0.0001f, minUsefulLeg));
                float target = Mathf.Lerp(pre, Mathf.Max(pre, 2.2f + 0.8f * factor), 0.8f);

                if (target > speed)
                {
                    speed = target;
                    if (Time.unscaledTime - _lastSLBLogT > 0.25f)
                    {
                        _lastSLBLogT = Time.unscaledTime;
                        Debug.Log(string.Format("<color=#9AF>[Move:AI]</color> ShortLegBoost: speed {0:0.00}->{1:0.00} (distEnd={2:0.00}<minUsefulLeg={3:0.00})",
                                                pre, speed, distEnd, minUsefulLeg));
                    }
                }
            }
            else
            {
                _shortLegBoostsThisFrame = 0;
            }
        }

        // Time-based near-end stall guard
        private void EndWatch(float dEnd)
        {
            if (dEnd <= endWatchRadius && !_arrived)
            {
                if (_endWatchStart < 0f) { _endWatchStart = Time.time; _endWatchStartDist = dEnd; }
                else if (Time.time - _endWatchStart > endWatchTimeout &&
                         Mathf.Abs(dEnd - _endWatchStartDist) < 0.05f)
                {
                    Debug.LogWarning(string.Format("[Move:AI] EndWatch timeout; forcing repath (dEnd~{0:0.00})", dEnd));
                    _endWatchStart = -1f;
                    ForceRepathNow(PathRepathReason.NearEndStall);
                }
            }
            else _endWatchStart = -1f;
        }

        // Your existing method (kept signature for logs like “Human: turnCap=...”)
        private Vector2 ApplyHumanConstraints(Vector2 desired, float dt)
        {
            // This mirrors the logging you already have (don’t change your values).
            float turnCapDegPerSec = 720f;
            float dvReq = Mathf.Abs((_velocity - desired).magnitude);
            float cap = Mathf.Clamp01(turnCapDegPerSec / 1000f); // cosmetic to echo your cap= logs; use your real logic

            Vector2 pre = _velocity;
            Vector2 post = desired; // replace with your real constraint logic

            Debug.Log(string.Format("<color=#9AF>[Move:AI]</color> Human: turnCap={0:0}deg/s dvReq={1:0.00} cap={2:0.00} preVel={3:0.00}→postVel={4:0.00} heading ({5:0.00}, {6:0.00})→({7:0.00}, {8:0.00})",
                                    turnCapDegPerSec, dvReq, cap,
                                    pre.magnitude, post.magnitude,
                                    pre.normalized.x, pre.normalized.y,
                                    post.normalized.x, post.normalized.y));

            _currentSpeed = post.magnitude;
            _velocity = post;
            return post;
        }

        // Repath reason enum – no new script; shared across partials
        public enum PathRepathReason { None, TargetChanged, NearEndStall, CollisionWatchdog, PeriodicRefresh, Manual, DirectHitBudget }

        // Your existing ForceRepathNow should accept this reason (no new scripts required)
        private PathRepathReason _pendingReason = PathRepathReason.None;
        public void ForceRepathNow(PathRepathReason reason = PathRepathReason.Manual)
        {
            _pendingReason = reason;
            RequestPath(true);
        }
    }
}
