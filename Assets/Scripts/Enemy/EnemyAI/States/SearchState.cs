// -----------------------------
// File: EnemyAI/States/SearchState.cs
// SEARCH-MEMORY AWARE + ANTI-THRASH (rev11-FIXED)
// - Finishes current area first: multi-anchor reseed + strong snap-to-unsearched
// - Defers "leaving" picks to strict coverage gates (>= 85% of target)
// - Local-only fallback (no global frontier commit) to avoid spikes
// - Latches + stuck watchdog retained
// - FIXES: Increased local hop range by 50%, lowered coverage gate to 85%
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
        private const float SEARCH_RADIUS = 7f;  // INCREASED from 4f

        // Thinking/analysis time
        private const float THINK_TIME_MIN = 0.10f;
        private const float THINK_TIME_MAX = 0.25f;

        // Latch tuning
        private const float LOCK_LOCAL_SEC = 0.90f;
        private const float LOCK_LEAVING_SEC = 1.50f;
        private const float PROGRESS_EPS = 0.10f;
        private const float STUCK_SECS = 1.00f;

        // Coverage gates - NOW USES 85% OF TARGET
        private const float COVERAGE_GATE_MULTIPLIER = 0.85f;  // NEW: 85% of target is good enough
        private const float EXIT_GATE_STAGNATION = 1.00f;
        private const float EXIT_GATE_POKE = 1.00f;
        private const float EXIT_GATE_EMPTY = 1.00f;

        // --- reseed & snap tuning ---
        private const int AREA_RESEED_PROBES = 10;
        private static readonly float[] AREA_RESEED_RADII = { 3.0f, 5.0f, 7.5f, 10.0f };  // EXTENDED from { 2.0f, 3.5f, 5.0f }

        // Extra-strong snap toward unsearched: ring samples around candidate
        private static readonly float[] SNAP_RING_RADII = { 0.75f, 1.25f, 2.0f, 2.75f };
        private const int SNAP_SAMPLES_PER_RING = 12;

        // state
        private float _pickTimer, _scanTimer, _trailTimer;
        private bool _isScanning;
        private bool _isThinking;
        private float _thinkTimer;

        private Vector3 _anchor, _lastArrivePos, _lastScanTarget;
        private readonly List<Vector3> _recent = new(16);
        private const int MAX_RECENT = 16;
        private const int MAX_TRIES = 10;

        // area tracking
        private AreaChunker2D _chunker;
        private int _currentAreaId = -1;   // STRICT id (=-1 if on portal)
        private int _lastSolidAreaId = -1; // last non-portal id
        private Vector3 _lastSolidAreaPos;
        private int _effectiveAreaAtLastPick = -999;

        // leave / coverage bookkeeping
        private int _consecutiveEmptyLocalChecks = 0;
        private int _localPokes = 0;
        private float _lastCoverage = -1f;
        private int _stagnantPicks = 0;

        private int _pickSeq;
        private int _visionBlockMask = Physics2D.DefaultRaycastLayers;

        // intent for current pick
        private bool _forceLeaving = false;

        // latches & progress watchdog
        private float _lockUntil = 0f;
        private float _lastDistToTarget = float.MaxValue;
        private float _noProgressTime = 0f;

        private bool _leavingLatchActive = false;
        private int _leavingLatchAreaId = -1;

        private float _nextLatchLogAt = 0f;
        private float _nextStatusLogAt = 0f;  // For periodic status updates

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

        // Helper method for better debug output
        private string GetAreaStats(EnemyAICore core)
        {
            int areaId = EffectiveAreaId();
            if (areaId < 0) return "area=PORTAL";

            float coverage = core.GetAreaCoverageLocal(areaId);
            float coveragePercent = coverage * 100f;

            return $"area={areaId} (searched={coveragePercent:0.0}%)";
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
            _isThinking = false;
            _thinkTimer = 0f;
            _recent.Clear(); _pickSeq = 0;

            _lastArrivePos = core.transform.position;
            _lastScanTarget = new Vector3(float.NaN, float.NaN, float.NaN);

            core.ResetSearchPlan(_anchor);

            UpdateAreaIds(core);
            _effectiveAreaAtLastPick = EffectiveAreaId();

            _consecutiveEmptyLocalChecks = 0;
            _localPokes = 0;
            _lastCoverage = -1f;
            _stagnantPicks = 0;
            _forceLeaving = false;

            // reset latches
            _lockUntil = 0f;
            _lastDistToTarget = float.MaxValue;
            _noProgressTime = 0f;
            _leavingLatchActive = false;
            _leavingLatchAreaId = -1;
            _nextLatchLogAt = 0f;
            _nextStatusLogAt = 0f;

            // First target: immediate selection for initial movement
            Vector3 next;
            bool lv;
            if (!TryFindValidSearchTarget(core, out next, out lv))
                next = core.transform.position;

            ValidateAndClampCandidate(core, core.transform.position, ref next, "init", _forceLeaving);
            CommitToNewTarget(core, next, _forceLeaving, forceRepath: true);

            DBG(core, $"Search init -> {next:F2} ({GetAreaStats(core)})");
        }

        public void OnExit(EnemyAICore core)
        {
            DBG(core, "Exit Search");
            core.ApplyDetectionPreset(DetectionMode.Patrol);
            _isScanning = false;
            _isThinking = false;
            _scanTimer = 0f;
            _thinkTimer = 0f;
            _forceLeaving = false;

            _lockUntil = 0f;
            _leavingLatchActive = false;
            _leavingLatchAreaId = -1;
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

            // progress watchdog
            float dist = Vector2.Distance(core.transform.position, (Vector2)core.CurrentFixedTarget);
            if (_lastDistToTarget < float.MaxValue)
            {
                if (dist <= _lastDistToTarget - PROGRESS_EPS) _noProgressTime = 0f;
                else _noProgressTime += dt;
            }
            _lastDistToTarget = dist;

            // Periodic status logging
            if (Time.time >= _nextStatusLogAt && core.showDebugLogs)
            {
                _nextStatusLogAt = Time.time + 3.0f; // Log every 3 seconds
                DBG(core, $"STATUS: {GetAreaStats(core)}, target={core.CurrentFixedTarget:F2}, dist={dist:0.1f}");
            }

            // continuous marking while moving
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

            // thinking?
            if (_isThinking)
            {
                _thinkTimer -= dt;
                if (_thinkTimer > 0f) return;

                _isThinking = false;
                DBG(core, "Analysis complete, selecting target");
                PerformTargetSelection(core);
                return;
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
                        DBG(core, $"Arrived, scanning {_scanTimer:0.00}s");
                    }
                    else
                    {
                        BeginThinking(core);
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

                BeginThinking(core);
                return;
            }

            _pickTimer -= dt;
            if (_pickTimer <= 0f) BeginThinking(core);
        }

        private void BeginThinking(EnemyAICore core)
        {
            int eff = EffectiveAreaId();
            float cov = AreaCoverageLocal(core, eff);
            float covPercent = cov * 100f;

            // Longer thinking time when coverage is low (need more analysis)
            float thinkTime = Mathf.Lerp(THINK_TIME_MIN, THINK_TIME_MAX, 1f - cov);

            _isThinking = true;
            _thinkTimer = thinkTime;

            DBG(core, $"Analyzing area (searched={covPercent:0.0}%, think={thinkTime:0.00}s)");
        }

        private void PerformTargetSelection(EnemyAICore core)
        {
            _pickTimer = core.searchPickInterval;
            _anchor = Vector3.Lerp(_anchor, core.transform.position, 0.15f);

            core.EnsureFrontierHasWork(core.transform.position);
            UpdateAreaIds(core);

            // Release leaving latch once area changes
            if (_leavingLatchActive && EffectiveAreaId() != _leavingLatchAreaId)
            {
                _leavingLatchActive = false;
                DBG(core, "Leaving latch released (area changed)");
            }

            // respect latch if active & not stuck & not already at waypoint
            if (ShouldRespectLatch(core))
            {
                _pickTimer = Mathf.Max(_pickTimer, 0.15f);
                if (Time.time >= _nextLatchLogAt)
                {
                    DBG(core, $"Latch hold (leaving={_leavingLatchActive}, tLeft={Mathf.Max(0f, _lockUntil - Time.time):0.00}s)");
                    _nextLatchLogAt = Time.time + 0.75f;
                }
                return;
            }

            int eff = EffectiveAreaId();
            float cov = AreaCoverageLocal(core, eff);
            float covPercent = cov * 100f;

            if (eff != _effectiveAreaAtLastPick)
            {
                DBG(core, $"Area change ({_effectiveAreaAtLastPick} -> {eff}). Reset counters. Current: {GetAreaStats(core)}");
                _effectiveAreaAtLastPick = eff;
                _consecutiveEmptyLocalChecks = 0;
                _localPokes = 0;
                _stagnantPicks = 0;
                _lastCoverage = -1f;
            }

            Vector3 next = core.transform.position;
            bool picked = false;
            _forceLeaving = false;

            // A) If area is truly complete, leave immediately
            if (eff >= 0 && cov >= core.searchCoverageTarget + core.searchCoverageDoneSlack)
            {
                _forceLeaving = true;
                if (!core.TryGetNextSearchPoint(core.transform.position, out next)) next = core.transform.position;
                DBG(core, $"Area complete (searched={covPercent:0.0}%) -> leaving to {next:F2}");
                picked = true;
            }

            // B) Prefer a memory-true local pick; if suggestion implies leaving, defer to leave gates
            if (!picked)
            {
                bool leavingSuggestion;
                Vector3 cand;
                if (TryFindValidSearchTarget(core, out cand, out leavingSuggestion))
                {
                    if (!leavingSuggestion) // stay in the room
                    {
                        cand = StrongSnapTowardUnsearched(core, eff, cand);
                        next = cand;
                        picked = true;
                        _consecutiveEmptyLocalChecks = _localPokes = _stagnantPicks = 0;
                        DBG(core, $"Valid LOCAL search target -> {next:F2} (searched={covPercent:0.0}%)");
                    }
                    else
                    {
                        DBG(core, $"Frontier suggested leaving to {cand:F2}, deferring to leave gates.");
                    }
                }
            }

            // C) Local same-side poke with strong bias toward unsearched (snap)
            if (!picked)
            {
                _consecutiveEmptyLocalChecks++;
                if (_lastCoverage >= 0f && cov <= _lastCoverage + core.searchCoverageStagnationEpsilon) _stagnantPicks++;
                else _stagnantPicks = 0;

                bool expand = cov < (core.searchCoverageTarget * 0.75f);
                var local = FindLocalSameSide(core, core.transform.position, expand);
                if (local.HasValue && _localPokes < core.searchMaxLocalPokesBeforeLeave)
                {
                    var snapped = StrongSnapTowardUnsearched(core, eff, local.Value);
                    next = snapped;
                    picked = true;
                    _localPokes++;
                    DBG(core, $"Local poke {_localPokes}/{core.searchMaxLocalPokesBeforeLeave} -> {next:F2} (searched={covPercent:0.0}%)");
                }
            }

            // D) Coverage-gated leave (RELAXED: only when cov >= 85% of target)
            if (!picked)
            {
                bool hitEmptyLimit = _consecutiveEmptyLocalChecks >= core.searchEmptyChecksBeforeLeave;
                bool hitPokeLimit = _localPokes >= core.searchMaxLocalPokesBeforeLeave;
                bool hitStagnation = _stagnantPicks >= core.searchStagnantPicksBeforeLeave;

                // FIXED: Use 85% of target instead of 100%
                float gateTarget = core.searchCoverageTarget * COVERAGE_GATE_MULTIPLIER;
                float gatePercent = gateTarget * 100f;

                if (hitStagnation && cov < gateTarget)
                {
                    hitStagnation = false;
                    DBG(core, $"Stagnation ignored (searched {covPercent:0.0}% < gate {gatePercent:0.0}%)");
                }
                if (hitPokeLimit && cov < gateTarget)
                {
                    hitPokeLimit = false;
                    DBG(core, $"Poke limit ignored (searched {covPercent:0.0}% < gate {gatePercent:0.0}%)");
                }
                if (hitEmptyLimit && cov < gateTarget)
                {
                    hitEmptyLimit = false;
                    DBG(core, $"Empty limit ignored (searched {covPercent:0.0}% < gate {gatePercent:0.0}%)");
                }

                // Safety valve: if we're *truly* stuck (no progress) for a while, allow leave even below target.
                bool stuckEscape = _noProgressTime >= (STUCK_SECS * 2f);

                if (hitEmptyLimit || hitPokeLimit || hitStagnation || stuckEscape)
                {
                    _forceLeaving = true;
                    if (!core.TryGetNextSearchPoint(core.transform.position, out next))
                        next = core.transform.position;

                    DBG(core, $"Leaving area (searched={covPercent:0.0}%, empty={_consecutiveEmptyLocalChecks}, poke={_localPokes}, stag={_stagnantPicks}, stuckEsc={stuckEscape}) -> {next:F2}");

                    _consecutiveEmptyLocalChecks = 0;
                    _localPokes = 0;
                    _stagnantPicks = 0;
                    picked = true;
                }
            }

            // E) LAST resort: strictly local random (no global frontier here)
            if (!picked)
            {
                next = FindNewSearchPoint(core, core.transform.position, preferAreaId: eff);
                next = StrongSnapTowardUnsearched(core, eff, next);
                DBG(core, $"Fallback target -> {next:F2} (searched={covPercent:0.0}%)");
            }

            ValidateAndClampCandidate(core, core.transform.position, ref next, "select", _forceLeaving);

            // only repath if meaningfully different
            if (Vector2.Distance((Vector2)core.CurrentFixedTarget, (Vector2)next) > 0.20f)
            {
                DBG(core, $"Pick#{++_pickSeq} -> {next:F2} (repath, leaving={_forceLeaving}, {GetAreaStats(core)})");
                CommitToNewTarget(core, next, _forceLeaving, forceRepath: true);
            }
            else
            {
                DBG(core, $"Pick#{++_pickSeq} -> {next:F2} (leaving={_forceLeaving}, {GetAreaStats(core)})");
                CommitToNewTarget(core, next, _forceLeaving, forceRepath: false);
            }

            _lastCoverage = cov;
        }

        // ---------- AREA-FIRST TARGETING ----------

        // Strong snap: try candidate, then multi-ring samples; then reseed
        private Vector3 StrongSnapTowardUnsearched(EnemyAICore core, int areaId, Vector3 cand)
        {
            if (_chunker == null || areaId < 0) return cand;

            // 1) direct near candidate
            if (core.TryGetUnsearchedPointInAreaLocal(areaId, cand, out var snap))
                return snap;

            // 2) ring samples around candidate
            float seed = (_pickSeq * 0.37f) + _recent.Count * 0.13f;
            for (int r = 0; r < SNAP_RING_RADII.Length; r++)
            {
                float radius = SNAP_RING_RADII[r];
                for (int i = 0; i < SNAP_SAMPLES_PER_RING; i++)
                {
                    float t = (i + seed) / SNAP_SAMPLES_PER_RING;
                    float ang = t * Mathf.PI * 2f;
                    Vector2 dir = new(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector3 probe = cand + new Vector3(dir.x * radius, dir.y * radius, 0f);

                    // keep probe inside the same area and off portals
                    probe = ClampAlongLineInsideArea(cand, probe, areaId);
                    probe = AntiPortalAdjust(probe, areaId, cand, false);

                    if (core.TryGetUnsearchedPointInAreaLocal(areaId, probe, out snap))
                        return snap;
                }
            }

            // 3) broader reseed within the same area
            if (ReseedAreaUnsearched(core, areaId, cand, out var rs)) return rs;

            return cand;
        }

        // Try several anchors inside the current area to discover a fresh cell.
        private bool ReseedAreaUnsearched(EnemyAICore core, int areaId, Vector3 origin, out Vector3 next)
        {
            next = origin;
            if (_chunker == null || areaId < 0) return false;

            // try origin first
            if (core.TryGetUnsearchedPointInAreaLocal(areaId, origin, out next))
                return true;

            float seed = (_pickSeq * 0.37f) + _recent.Count * 0.13f;
            for (int r = 0; r < AREA_RESEED_RADII.Length; r++)
            {
                float radius = AREA_RESEED_RADII[r];
                for (int i = 0; i < AREA_RESEED_PROBES; i++)
                {
                    float t = (i + seed) / AREA_RESEED_PROBES;
                    float ang = t * Mathf.PI * 2f;
                    Vector2 dir = new(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector2 probe = (Vector2)origin + dir * radius;

                    Vector3 clamped = ClampAlongLineInsideArea(origin, probe, areaId);
                    clamped = AntiPortalAdjust(clamped, areaId, origin, false);

                    if (core.TryGetUnsearchedPointInAreaLocal(areaId, clamped, out next))
                        return true;
                }
            }
            return false;
        }

        /// Find a valid search target that respects search memory (returns if it implies leaving).
        private bool TryFindValidSearchTarget(EnemyAICore core, out Vector3 next, out bool leaving)
        {
            next = core.transform.position;
            leaving = false;

            int eff = EffectiveAreaId();

            // Strategy 1: Use the proper search memory method for current area
            if (eff >= 0 && core.TryGetUnsearchedPointInAreaLocal(eff, core.transform.position, out next))
                return true;

            // Strategy 2: Try other anchor points in this area (+ reseed)
            if (eff >= 0)
            {
                if (core.TryGetUnsearchedPointInAreaLocal(eff, _anchor, out next)) return true;
                if (core.TryGetUnsearchedPointInAreaLocal(eff, _lastSolidAreaPos, out next)) return true;
                if (ReseedAreaUnsearched(core, eff, core.transform.position, out next)) return true;
            }

            // Strategy 3: Use the global frontier (may imply leaving) — DO NOT COMMIT here
            if (core.TryGetNextSearchPoint(core.transform.position, out next))
            {
                leaving = (eff >= 0 && _chunker != null && _chunker.GetAreaIdStrict(next) != eff);
                return true;
            }

            return false;
        }

        /// Apply latch(es) and set the target.
        private void CommitToNewTarget(EnemyAICore core, Vector3 next, bool leaving, bool forceRepath)
        {
            core.SetFixedTarget(next);
            if (forceRepath) core.ForceRepathNow();

            float commit = leaving ? LOCK_LEAVING_SEC : LOCK_LOCAL_SEC;
            _lockUntil = Time.time + commit;

            _lastDistToTarget = Vector2.Distance(core.transform.position, (Vector2)next);
            _noProgressTime = 0f;

            if (leaving)
            {
                _leavingLatchActive = true;
                _leavingLatchAreaId = EffectiveAreaId();
            }
        }

        private bool ShouldRespectLatch(EnemyAICore core)
        {
            bool atWaypoint = core.Reached(core.CurrentFixedTarget, core.waypointTolerance);
            bool timeLock = Time.time < _lockUntil;
            bool leavingLock = _leavingLatchActive && EffectiveAreaId() == _leavingLatchAreaId;
            bool stuck = _noProgressTime >= STUCK_SECS;

            return !atWaypoint && !stuck && (timeLock || leavingLock);
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

            // If finishing this area, clamp inside it and off portals — unless we're leaving now.
            if (!forceLeaving && _chunker != null && eff >= 0 && core.GetAreaCoverageLocal(eff) < core.searchCoverageTarget)
            {
                int candStrict = _chunker.GetAreaIdStrict(c);
                bool candPortal = _chunker.IsPortal(c);

                if (candPortal || candStrict != eff)
                {
                    Vector3 clamped = ClampAlongLineInsideArea(o, c, eff);
                    clamped = AntiPortalAdjust(clamped, eff, origin, allowPortalTargets: false);
                    DBG(core, $"[{reasonTag}] Clamped to area={eff}: {candidate:F2} -> {clamped:F2}");
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
                    DBG(core, $"[{reasonTag}] Near-blocked; local fallback {local.Value:F2}");
                    candidate = StrongSnapTowardUnsearched(core, eff, local.Value);
                    SetDebugPoints(core, origin, candidate);
                    return;
                }
                DBG(core, $"[{reasonTag}] Near-blocked; origin fallback");
                candidate = origin; SetDebugPoints(core, origin, candidate); return;
            }

            if (blocked && blockDist <= core.searchBlockHitNear && straight <= core.searchAcrossWallMaxDist)
            {
                var edge = NudgeToVisibleEdge(o, c);
                if (edge.HasValue)
                {
                    DBG(core, $"[{reasonTag}] Nudged to visible edge {edge.Value:F2}");
                    candidate = StrongSnapTowardUnsearched(core, eff, edge.Value);
                    SetDebugPoints(core, origin, candidate);
                    return;
                }

                if (_chunker != null && eff >= 0 && !forceLeaving)
                {
                    Vector3 clamped = ClampAlongLineInsideArea(o, c, eff);
                    clamped = AntiPortalAdjust(clamped, eff, origin, false);
                    DBG(core, $"[{reasonTag}] Wall-clamped inside area {clamped:F2}");
                    candidate = StrongSnapTowardUnsearched(core, eff, clamped);
                    SetDebugPoints(core, origin, candidate);
                    return;
                }
            }

            // Cross-area redirection via cached portal next-hop.
            if (_chunker != null && eff >= 0)
            {
                int candArea = _chunker.GetAreaIdStrict(c);
                if (candArea >= 0 && candArea != eff)
                {
                    if (_chunker.TryGetNextPortalToward(eff, candArea, o, out var portalPt) ||
                        _chunker.TryGetPortalStep(eff, candArea, o, out portalPt))
                    {
                        DBG(core, $"[{reasonTag}] Portal step to {portalPt:F2}");
                        candidate = portalPt; c = candidate;
                    }
                }
            }

            // Don't target a portal unless allowed, or we're leaving now.
            candidate = AntiPortalAdjust(candidate, -1, origin, core.searchAllowPortalTargets || forceLeaving);
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

        private Vector3? FindLocalSameSide(EnemyAICore core, Vector3 origin, bool expandSearch = false)
        {
            Vector2 o = origin;
            int seed = _pickSeq + _recent.Count;

            int count = Mathf.Max(1, core.searchLocalSampleCount);

            // FIXED: Base increase of 50% to search range
            float baseMultiplier = 1.5f;
            float rMin = core.searchLocalSampleRMin * baseMultiplier;
            float rMax = Mathf.Max(rMin, core.searchLocalSampleRMax * baseMultiplier);

            // Progressive expansion based on how many times we've failed to leave
            float stuckMultiplier = 1f + (_consecutiveEmptyLocalChecks * 0.1f);  // +10% per failed attempt
            rMin *= stuckMultiplier;
            rMax *= stuckMultiplier;

            if (expandSearch)
            {
                count = Mathf.RoundToInt(count * 1.5f);
                rMax *= 1.4f;  // Additional 40% when explicitly expanding
                DBG(core, $"Expanding local search: count={count}, rMax={rMax:0.1f}");
            }

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

                // Allow intermediary point; final snap will pull to unsearched
                Remember(cand);
                return cand;
            }
            return null;
        }

        private Vector3 FindNewSearchPoint(EnemyAICore core, Vector3 around, int preferAreaId)
        {
            // strictly local random samples (no global frontier)
            for (int i = 0; i < MAX_TRIES; i++)
            {
                Vector2 rnd = Random.insideUnitCircle * SEARCH_RADIUS;
                Vector3 p = around + new Vector3(rnd.x, rnd.y, 0f);

                if (_chunker != null && preferAreaId >= 0)
                {
                    p = ClampAlongLineInsideArea(around, p, preferAreaId);
                    p = AntiPortalAdjust(p, preferAreaId, around, false);
                }

                if (!TooCloseToRecent(p)) { Remember(p); return p; }
            }

            // final attempt
            Vector2 fb = Random.insideUnitCircle * SEARCH_RADIUS;
            Vector3 last = around + new Vector3(fb.x, fb.y, 0f);
            if (_chunker != null && preferAreaId >= 0)
            {
                last = ClampAlongLineInsideArea(around, last, preferAreaId);
                last = AntiPortalAdjust(last, preferAreaId, around, false);
            }
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