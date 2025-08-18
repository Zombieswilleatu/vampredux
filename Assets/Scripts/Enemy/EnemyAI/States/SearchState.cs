// -----------------------------
// File: EnemyAI/States/SearchState.cs
// PERF + PORTAL-HOP version
// - Adds _forceLeaving flag so ValidateAndClampCandidate won't clamp back into the room
// - When leaving, portal redirection is always allowed
// - Small spam guard on repaths
// -----------------------------
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace EnemyAI.States
{
    public class SearchState : IEnemyState
    {
        public string DebugName => "Search";

        private const float MIN_SEPARATION = 1.25f;
        private const float SEARCH_RADIUS = 4f;

        // state
        private float _pickTimer, _scanTimer, _trailTimer;
        private bool _isScanning;

        private Vector3 _anchor, _lastArrivePos, _lastScanTarget;
        private readonly List<Vector3> _recent = new(16);
        private const int MAX_RECENT = 16;
        private const int MAX_TRIES = 10;

        // area tracking
        private AreaChunker2D _chunker;
        private int _currentAreaId = -1;   // STRICT id (=-1 if on portal)
        private int _lastSolidAreaId = -1; // last non-portal area we were in
        private Vector3 _lastSolidAreaPos;
        private int _effectiveAreaAtLastPick = -999;
        private int _consecutiveEmptyLocalChecks = 0;

        private int _pickSeq;
        private int _visionBlockMask = Physics2D.DefaultRaycastLayers;

        // new: set true for the current pick when we *intend* to leave the room
        private bool _forceLeaving = false;

        // ---------- Debug helpers ----------
        private static void DBG(EnemyAICore core, string msg)
        {
            if (!core.showDebugLogs) return;
            Debug.Log($"<color=#7ABFFF>[SearchState]</color> {core.name}: {msg}");
        }

        private static void SetDebugPoints(EnemyAICore core, Vector3 candidate, Vector3 clamped)
        {
#if UNITY_EDITOR
            try
            {
                var t = core.GetType();
                t.GetField("debugSearchCandidate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 ?.SetValue(core, candidate);
                t.GetField("debugSearchClamped", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 ?.SetValue(core, clamped);
            }
            catch { }
#endif
        }
        // -----------------------------------

        public void OnEnter(EnemyAICore core)
        {
            DBG(core, "Enter Search");

            core.ApplySearchTuningFromSkill();
            core.ApplyDetectionPreset(DetectionMode.Search);

            _visionBlockMask = core.visionBlockMask.value != 0
                ? core.visionBlockMask.value
                : Physics2D.DefaultRaycastLayers;

            _chunker = core.GetComponent<AreaChunker2D>()
                       ?? core.GetComponentInParent<AreaChunker2D>()
                       ?? Object.FindObjectOfType<AreaChunker2D>(true);
            _chunker?.EnsureBuilt();

            bool useLast = core.LastKnownTargetPos != Vector3.zero &&
                           Vector2.Distance(core.transform.position, core.LastKnownTargetPos) < 6f;

            _anchor = useLast ? core.LastKnownTargetPos : core.transform.position;
            _pickTimer = _scanTimer = 0f;
            _trailTimer = 0f;
            _isScanning = false;
            _recent.Clear(); _pickSeq = 0;

            _lastArrivePos = core.transform.position;
            _lastScanTarget = new Vector3(float.NaN, float.NaN, float.NaN);

            core.ResetSearchPlan(_anchor);

            UpdateAreaIds(core);
            _effectiveAreaAtLastPick = EffectiveAreaId();
            _consecutiveEmptyLocalChecks = 0;
            _forceLeaving = false;

            // First target
            Vector3 next;
            if (!PickAreaLocal(core, out next, "init"))
                if (!core.TryGetNextSearchPoint(core.transform.position, out next))
                    next = core.transform.position;

            ValidateAndClampCandidate(core, core.transform.position, ref next, "init", _forceLeaving);
            core.SetFixedTarget(next);
            core.ForceRepathNow();

            DBG(core, $"Search init -> {next:F2} (area {EffectiveAreaId()}, onPortal={IsOnPortal(core)}, cov={AreaCoverageLocal(core, EffectiveAreaId()):0.00})");
        }

        public void OnExit(EnemyAICore core)
        {
            DBG(core, "Exit Search");
            core.ApplyDetectionPreset(DetectionMode.Patrol);
            _isScanning = false;
            _scanTimer = 0f;
            _forceLeaving = false;
        }

        public void Tick(EnemyAICore core, float dt)
        {
            core.CommsTick(dt);

            if (core.targetTransform != null)
            {
                DBG(core, "Search -> Chase (target reacquired)");
                core.SwitchState(EnemyState.Chase);
                return;
            }

            UpdateAreaIds(core);

            // continuous marking while moving — BUDGETED sweep
            _trailTimer -= dt;
            if (_trailTimer <= 0f)
            {
                _trailTimer = Mathf.Max(0.05f, core.searchMarkInterval);

                Vector2 heading = (core.CurrentFixedTarget - core.transform.position);
                if (heading.sqrMagnitude < 1e-6f) heading = Vector2.right;

                float radius = core.searchLookSweepRadius * core.searchRadiusBoost;
                float half = core.searchLookHalfAngle * core.searchAngleBoost;

                core.MarkSearchedConeBudgeted(core.transform.position, heading, radius, half);
            }

            // arrival -> maybe dwell
            if (core.Reached(core.CurrentFixedTarget, core.waypointTolerance))
            {
                bool haveLast = !float.IsNaN(_lastScanTarget.x);
                Vector2 lastTarget2 = haveLast ? (Vector2)_lastScanTarget : Vector2.zero;
                Vector2 currentTarget2 = (Vector2)core.CurrentFixedTarget;
                bool sameTarget = haveLast && Vector2.Distance(lastTarget2, currentTarget2) < 0.05f;

                float movedSinceLastArrive = Vector2.Distance((Vector2)_lastArrivePos, (Vector2)core.transform.position);

                if (!_isScanning && (!sameTarget || movedSinceLastArrive >= 0.50f))
                {
                    _lastArrivePos = core.transform.position;
                    _lastScanTarget = core.CurrentFixedTarget;

                    float hop = movedSinceLastArrive;
                    float distNow = Vector2.Distance(core.transform.position, core.CurrentFixedTarget);
                    bool trivial = hop < 1.25f || distNow < 1.25f;

                    float dwell = trivial ? 0f
                               : Mathf.Clamp(hop * core.searchLookHoldPerMeter, core.searchLookHoldMin, core.searchLookHoldMax);

                    if (dwell > 0f)
                    {
                        _scanTimer = dwell;
                        _isScanning = true;
                        DBG(core, $"Arrived {core.CurrentFixedTarget:F2} (area {EffectiveAreaId()}, onPortal={IsOnPortal(core)}), scanning {_scanTimer:0.00}s");
                    }
                    else
                    {
                        SelectNext(core);
                        return;
                    }
                }
            }

            if (_isScanning)
            {
                _scanTimer -= dt;
                if (_scanTimer > 0f) return;

                _isScanning = false;

                Vector2 lookDir = (core.CurrentFixedTarget - core.transform.position);
                if (lookDir.sqrMagnitude < 1e-6f) lookDir = Vector2.right;

                float radius = core.searchLookSweepRadius * core.searchRadiusBoost;
                float half = core.searchLookHalfAngle * core.searchAngleBoost;

                core.MarkSearchedCone(core.transform.position, lookDir, radius, half, core.searchMaxMarksPerSweep);

                SelectNext(core);
                return;
            }

            _pickTimer -= dt;
            if (_pickTimer <= 0f) SelectNext(core);
        }

        private void SelectNext(EnemyAICore core)
        {
            _pickTimer = core.searchPickInterval;
            _anchor = Vector3.Lerp(_anchor, core.transform.position, 0.15f);

            core.EnsureFrontierHasWork(core.transform.position);
            UpdateAreaIds(core);

            int eff = EffectiveAreaId();

            if (eff != _effectiveAreaAtLastPick)
            {
                DBG(core, $"Area change (eff {_effectiveAreaAtLastPick} -> {eff}). Reset empty-checks.");
                _effectiveAreaAtLastPick = eff;
                _consecutiveEmptyLocalChecks = 0;
            }

            Vector3 next;
            bool picked = false;
            _forceLeaving = false;

            // 1) Prefer finishing current area
            if (PickAreaLocal(core, out next, "pick"))
            {
                picked = true;
                _consecutiveEmptyLocalChecks = 0;
            }
            else
            {
                _consecutiveEmptyLocalChecks++;
                DBG(core, $"Local empty check {_consecutiveEmptyLocalChecks}/{core.searchEmptyChecksBeforeLeave} (area={eff}, cov={AreaCoverageLocal(core, eff):0.00})");
            }

            // 2) Same-side try (before giving up)
            if (!picked && _consecutiveEmptyLocalChecks < core.searchEmptyChecksBeforeLeave)
            {
                var local = FindLocalSameSide(core, core.transform.position);
                if (local.HasValue)
                {
                    next = local.Value;
                    picked = true;
                    DBG(core, $"Same-side poke -> {next:F2}");
                }
            }

            // 3) Frontier fallback (likely moving to another area) → we are *leaving*
            if (!picked)
            {
                _forceLeaving = true;

                if (!core.TryGetNextSearchPoint(core.transform.position, out next))
                {
                    next = core.transform.position;
                }
            }

            ValidateAndClampCandidate(core, core.transform.position, ref next, "pick", _forceLeaving);

            // only repath if meaningfully different
            if (Vector2.Distance((Vector2)core.CurrentFixedTarget, (Vector2)next) > 0.20f)
            {
                DBG(core, $"Pick#{++_pickSeq} -> {next:F2} (repath, leaving={_forceLeaving})");
                core.SetFixedTarget(next);
                core.ForceRepathNow();
            }
            else
            {
                DBG(core, $"Pick#{++_pickSeq} -> {next:F2} (leaving={_forceLeaving})");
                core.SetFixedTarget(next);
            }
        }

        /// Try to pick a point inside the current effective area while we’re still “finishing” it.
        private bool PickAreaLocal(EnemyAICore core, out Vector3 next, string reason)
        {
            next = core.transform.position;
            if (_chunker == null) return false;

            int eff = EffectiveAreaId();
            if (eff < 0) return false;

            float covLocal = core.GetAreaCoverageLocal(eff);
            bool shouldStick = covLocal < core.searchCoverageTarget ||
                               _consecutiveEmptyLocalChecks < core.searchEmptyChecksBeforeLeave;

            if (!shouldStick) return false;

            if (core.TryGetUnsearchedPointInAreaLocal(eff, core.transform.position, out var areaPick))
            {
                // While finishing an area, we never target portals (nudge off them)
                areaPick = AntiPortalAdjust(areaPick, eff, core.transform.position, allowPortalTargets: false);
                next = areaPick;
                DBG(core, $"Area-first pick (area={eff}, covLocal={covLocal:0.00}{(covLocal >= core.searchCoverageTarget ? " •stick" : "")}) -> {next:F2}");
                return true;
            }
            return false;
        }

        // ───────────────────────────────────────────────────────────────
        // Validation & helpers
        // ───────────────────────────────────────────────────────────────
        private void ValidateAndClampCandidate(EnemyAICore core, Vector3 origin, ref Vector3 candidate, string reasonTag, bool forceLeaving)
        {
            Vector2 o = origin;
            Vector2 c = candidate;
            SetDebugPoints(core, candidate, candidate);

            UpdateAreaIds(core);
            int eff = EffectiveAreaId();

            // If finishing this area, clamp inside it and off portals — unless we’re leaving now.
            if (!forceLeaving && _chunker != null && eff >= 0 && core.GetAreaCoverageLocal(eff) < core.searchCoverageTarget)
            {
                int candStrict = _chunker.GetAreaIdStrict(c);
                bool candPortal = _chunker.IsPortal(c);

                if (candPortal || candStrict != eff)
                {
                    Vector3 clamped = ClampAlongLineInsideArea(o, c, eff);
                    clamped = AntiPortalAdjust(clamped, eff, origin, allowPortalTargets: false);
                    DBG(core, $"[{reasonTag}] Clamped to stay in area={eff}: {candidate:F2} -> {clamped:F2}");
                    candidate = clamped;
                    c = candidate;
                }
            }

            // LOS soft-fix
            var hit = Physics2D.Linecast(o, c, _visionBlockMask);
            bool blocked = hit.collider != null;
            float straight = Vector2.Distance(o, c);
            float blockDist = blocked ? hit.distance : float.PositiveInfinity;

            if (straight <= core.searchLosNearBlockDist && blocked)
            {
                var local = FindLocalSameSide(core, origin);
                if (local.HasValue)
                {
                    DBG(core, $"[{reasonTag}] Near-blocked; switched to local same-side {local.Value:F2}");
                    candidate = local.Value; SetDebugPoints(core, origin, candidate); return;
                }
                DBG(core, $"[{reasonTag}] Near-blocked; fell back to origin");
                candidate = origin; SetDebugPoints(core, origin, candidate); return;
            }

            if (blocked && blockDist <= core.searchBlockHitNear && straight <= core.searchAcrossWallMaxDist)
            {
                var edge = NudgeToVisibleEdge(o, c);
                if (edge.HasValue)
                {
                    DBG(core, $"[{reasonTag}] Nudge to visible edge {edge.Value:F2}");
                    candidate = edge.Value; SetDebugPoints(core, origin, candidate); return;
                }

                if (_chunker != null && eff >= 0 && !forceLeaving)
                {
                    Vector3 clamped = ClampAlongLineInsideArea(o, c, eff);
                    clamped = AntiPortalAdjust(clamped, eff, origin, allowPortalTargets: false);
                    DBG(core, $"[{reasonTag}] Across-wall; clamped inside area {clamped:F2}");
                    candidate = clamped; SetDebugPoints(core, origin, candidate); return;
                }
            }

            // Cross-area redirection via cached portal next-hop.
            // When forceLeaving==true we ALWAYS allow this redirection (ignores searchAllowPortalTargets).
            if (_chunker != null && eff >= 0)
            {
                int candArea = _chunker.GetAreaIdStrict(c);
                if (candArea >= 0 && candArea != eff)
                {
                    Vector3 portalPt;
                    if (_chunker.TryGetNextPortalToward(eff, candArea, o, out portalPt) ||
                        _chunker.TryGetPortalStep(eff, candArea, o, out portalPt))
                    {
                        DBG(core, $"[{reasonTag}] Cross-area -> portal step {portalPt:F2} (forceLeaving={forceLeaving})");
                        candidate = portalPt; c = candidate;
                    }
                }
            }

            // Don’t target a portal unless allowed, *or* we’re leaving now.
            candidate = AntiPortalAdjust(candidate, preferAreaId: -1, from: origin,
                                         allowPortalTargets: core.searchAllowPortalTargets || forceLeaving);
            SetDebugPoints(core, origin, candidate);
        }

        private Vector3 AntiPortalAdjust(Vector3 cand, int preferAreaId, Vector3 from, bool allowPortalTargets)
        {
            if (_chunker == null || allowPortalTargets) return cand;

            Vector2 p = cand;
            if (!_chunker.IsPortal(p)) return cand;

            Vector2 origin = from;
            Vector2 dir = (p - origin).normalized;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right;

            for (float t = 0.05f; t <= 0.8f; t += 0.05f)
            {
                Vector2 q = p + dir * t;
                if (_chunker.IsPortal(q)) continue;
                int id = _chunker.GetAreaIdStrict(q);
                if (preferAreaId < 0 || id == preferAreaId)
                    return new Vector3(q.x, q.y, 0f);
            }
            for (float t = 0.05f; t <= 0.8f; t += 0.05f)
            {
                Vector2 q = p - dir * t;
                if (_chunker.IsPortal(q)) continue;
                int id = _chunker.GetAreaIdStrict(q);
                if (preferAreaId < 0 || id == preferAreaId)
                    return new Vector3(q.x, q.y, 0f);
            }
            return cand;
        }

        private Vector3 ClampAlongLineInsideArea(Vector2 origin, Vector2 target, int areaId)
        {
            if (_chunker == null || areaId < 0) return new Vector3(origin.x, origin.y, 0f);

            if (_chunker.IsPortal(origin) && _lastSolidAreaId == areaId)
            {
                Vector2 back = ((Vector2)_lastSolidAreaPos - origin);
                if (back.sqrMagnitude > 1e-6f) origin += back.normalized * 0.05f;
            }

            float dist = Vector2.Distance(origin, target);
            if (dist < 0.01f) return new Vector3(origin.x, origin.y, 0f);

            Vector2 dir = (target - origin).normalized;
            float step = 0.25f;
            Vector2 last = origin;

            for (float t = step; t <= dist; t += step)
            {
                Vector2 p = origin + dir * t;
                if (_chunker.IsPortal(p)) break;
                int idStrict = _chunker.GetAreaIdStrict(p);
                if (idStrict != areaId) break;
                last = p;
            }
            return new Vector3(last.x, last.y, 0f);
        }

        private void UpdateAreaIds(EnemyAICore core)
        {
            if (_chunker == null) { _currentAreaId = -1; return; }
            Vector2 pos = core.transform.position;
            _currentAreaId = _chunker.GetAreaIdStrict(pos);
            if (_currentAreaId >= 0) { _lastSolidAreaId = _currentAreaId; _lastSolidAreaPos = core.transform.position; }
        }

        private int EffectiveAreaId() => _currentAreaId >= 0 ? _currentAreaId : _lastSolidAreaId;
        private bool IsOnPortal(EnemyAICore core) => _chunker != null && _chunker.IsPortal(core.transform.position);
        private float AreaCoverageLocal(EnemyAICore c, int areaId) => (areaId >= 0) ? c.GetAreaCoverageLocal(areaId) : 1f;

        private bool IsLOSBlocked(Vector2 a, Vector2 b)
            => Physics2D.Linecast(a, b, _visionBlockMask).collider != null;

        private Vector3? NudgeToVisibleEdge(Vector2 from, Vector2 to)
        {
            Vector2 dir = (to - from).normalized;
            const int steps = 6;
            const float maxAngle = 35f * Mathf.Deg2Rad;
            float dist = Vector2.Distance(from, to);

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float ang = t * maxAngle;
                Vector2 d1 = Rotate(dir, ang);
                Vector2 d2 = Rotate(dir, -ang);

                Vector2 p1 = from + d1 * dist * 0.9f;
                Vector2 p2 = from + d2 * dist * 0.9f;

                if (!IsLOSBlocked(from, p1)) return new Vector3(p1.x, p1.y, 0f);
                if (!IsLOSBlocked(from, p2)) return new Vector3(p2.x, p2.y, 0f);
            }
            return null;
        }

        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float ca = Mathf.Cos(radians), sa = Mathf.Sin(radians);
            return new Vector2(ca * v.x - sa * v.y, sa * v.x + ca * v.y);
        }

        private Vector3? FindLocalSameSide(EnemyAICore core, Vector3 origin)
        {
            Vector2 o = origin;
            int seed = _pickSeq + _recent.Count;

            int count = Mathf.Max(1, core.searchLocalSampleCount);
            float rMin = core.searchLocalSampleRMin;
            float rMax = Mathf.Max(rMin, core.searchLocalSampleRMax);

            int eff = EffectiveAreaId();

            for (int i = 0; i < count; i++)
            {
                float t = (i + ((seed * 0.37f) % 1f)) / count;
                float ang = t * Mathf.PI * 2f;
                float r = Mathf.Lerp(rMin, rMax, ((i * 0.618f) % 1f));

                Vector2 p = o + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                Vector3 cand = new(p.x, p.y, 0f);

                if (TooCloseToRecent(cand)) continue;
                if (IsLOSBlocked(o, p)) continue;

                if (_chunker != null && eff >= 0)
                {
                    if (_chunker.IsPortal(p)) continue;
                    if (_chunker.GetAreaIdStrict(p) != eff) continue;
                }

                Remember(cand);
                return cand;
            }
            return null;
        }

        private Vector3 FindNewSearchPoint(EnemyAICore core, Vector3 around)
        {
            for (int i = 0; i < MAX_TRIES; i++)
            {
                Vector2 rnd = Random.insideUnitCircle * SEARCH_RADIUS;
                Vector3 p = around + new Vector3(rnd.x, rnd.y, 0f);
                if (!TooCloseToRecent(p)) { Remember(p); return p; }
            }
            Vector2 fb = Random.insideUnitCircle * SEARCH_RADIUS;
            Vector3 last = around + new Vector3(fb.x, fb.y, 0f);
            Remember(last);
            return last;
        }

        private bool TooCloseToRecent(Vector3 p)
        {
            for (int i = 0; i < _recent.Count; i++)
                if (Vector2.Distance(_recent[i], p) < MIN_SEPARATION) return true;
            return false;
        }

        private void Remember(Vector3 p)
        {
            _recent.Add(p);
            if (_recent.Count > MAX_RECENT) _recent.RemoveAt(0);
        }
    }
}
