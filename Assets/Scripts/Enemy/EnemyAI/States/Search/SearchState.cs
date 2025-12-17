using System.Collections.Generic;
using UnityEngine;
using EnemyAI;

namespace EnemyAI.States
{
    public class SearchState : IEnemyState
    {
        public string DebugName => "Search";

        // ── Tunables (kept compatible with your existing values) ──────────────────
        private const float MIN_SEPARATION = 2.0f;

        // Replaces fixed SEARCH_RADIUS with a base + ramp
        private const float BASE_SEARCH_RADIUS = 8f;     // was SEARCH_RADIUS
        private const float RADIUS_STEP = 1.0f;          // how much to expand after a failed local pick cycle
        private const float RADIUS_MAX_BOOST = 6.0f;     // cap on expansion
        private const float RADIUS_COOLDOWN = 0.75f;     // how fast we decay the boost per successful local pick

        private const float THINK_TIME = 0.1f;

        private const int MAX_BFS_EXPANSIONS = 32;
        private const int BFS_CALLS_PER_PICK = 2;

        private const float MIN_DWELL_TIME = 3.0f;
        private const int MIN_LOCAL_PICKS = 3;

        private const float MAX_DWELL_TIME = 20.0f;      // (kept) pacing only
        private const int MAX_LOCAL_PICKS = 15;          // (kept) pacing only

        // Raised so “complete” really means near-fully covered
        private const float MAX_SEARCH_TARGET = 0.98f;   // was 0.85
        private const float AREA_LEAVE_HYSTERESIS = 0.02f;

        private const float PLATEAU_DELTA = 0.01f;
        private const float PLATEAU_TIME = 5.0f;
        private const float PLATEAU_RESEED_COOLDOWN = 2.0f; // avoid hammering reseed

        private const float PORTAL_NUDGE_DIST = 0.75f;

        private const float EXIT_AREA_COOLDOWN_SECONDS = 3.0f;
        private const int RECENT_AREA_MEMORY = 3;

        // Directional hysteresis to prefer continuing roughly forward
        private const float DIR_HYSTERESIS_WEIGHT = 0.25f;  // 0..1 influence on scoring

        // Time budget for candidate scoring loops (ms)
        private const float SELECTION_TIME_BUDGET_MS = 1.5f;

        // Max candidates to consider per pass in local pick routines
        private const int MAX_LOCAL_CANDIDATES = 64;

        // ── Timers / budgets ──────────────────────────────────────────────────────
        private float _pickTimer = 0f;
        private float _thinkTimer = 0f;
        private bool _isThinking = false;
        private int _bfsBudget = BFS_CALLS_PER_PICK;

        // ── Topology references ───────────────────────────────────────────────────
        private AreaChunker2D _chunker;
        private int _currentArea = -1;
        private int _lastArea = -1;
        private Vector3 _anchor;
        private int _pickSeq = 0;

        // ── Area dwell accounting ─────────────────────────────────────────────────
        private float _areaEnterTime;
        private int _localPicksThisArea;
        private int _bfsFailsThisArea;
        private int _consecutiveBfsFailsThisArea;

        // ── Coverage progress tracking ────────────────────────────────────────────
        private float _lastCoverage;
        private float _lastCoverageT;
        private float _bestCoverageThisArea;
        private float _lastReseedT;

        // ── Recent targets (spatial LRU to avoid too-close picks) ────────────────
        private readonly Vector3[] _recent = new Vector3[8];
        private int _recentIndex = 0;

        // ── Recent areas LRU + cooldown per area ─────────────────────────────────
        private readonly Queue<int> _recentAreas = new Queue<int>(RECENT_AREA_MEMORY);
        private readonly Dictionary<int, float> _areaCooldownUntil = new Dictionary<int, float>();

        // ── Short-hop guard ──────────────────────────────────────────────────────
        private float _minHopDist = 1.5f;
        private float _effectiveMinHop = 1.5f;

        // ── New: directional hysteresis state ────────────────────────────────────
        private Vector2 _lastTravelDir = Vector2.right;
        private Vector3 _lastPos;

        // ── New: dynamic radius ramp ─────────────────────────────────────────────
        private float _radiusBoost = 0f;

        public void OnEnter(EnemyAICore core)
        {
            core.ApplySearchTuningFromSkill();
            core.ApplyDetectionPreset(DetectionMode.Search);

            _minHopDist = core.searchMinHopDist;
            _effectiveMinHop = Mathf.Max(1.5f, _minHopDist);

            _chunker = FindChunker(core);
            _anchor = core.LastKnownTargetPos != Vector3.zero ? core.LastKnownTargetPos : core.transform.position;

            bool hasExistingMemory = Clamp01(core.GetTotalSearchCoverage()) > 0.01f;

            if (hasExistingMemory)
                core.RefreshFrontierForPosition(_anchor);
            else
                core.ResetSearchPlan(_anchor);

            _lastCoverage = 0f;
            _lastCoverageT = Time.time;
            _bestCoverageThisArea = 0f;
            _lastReseedT = -999f;

            _radiusBoost = 0f;
            _lastPos = core.transform.position;
            _lastTravelDir = Vector2.right;

            UpdateCurrentArea(core, resetIfChanged: true);
            SetInitialTarget(core);
        }

        public void OnExit(EnemyAICore core)
        {
            core.ApplyDetectionPreset(DetectionMode.Patrol);
            _isThinking = false;
        }

        public void Tick(EnemyAICore core, float dt)
        {
            if (core.targetTransform != null)
            {
                core.SwitchState(EnemyState.Chase);
                return;
            }

            // Update last travel direction from actual motion
            Vector3 curPos = core.transform.position;
            Vector2 delta = (Vector2)(curPos - _lastPos);
            if (delta.sqrMagnitude > 0.0001f)
                _lastTravelDir = delta.normalized;
            _lastPos = curPos;

            UpdateCurrentArea(core, resetIfChanged: true);

            // === Stop marking when the current area is complete ===
            bool areaCompleteNow = AreaIsComplete(core, _currentArea, out float curCov, out _);
            if (!areaCompleteNow && core.enableSearchMarking && ShouldMark(core))
            {
                Vector2 heading = (core.CurrentFixedTarget - core.transform.position).normalized;
                if (heading.sqrMagnitude < 0.1f) heading = Vector2.right;
                core.MarkSearchedConeBudgeted(core.transform.position, heading,
                    core.searchLookSweepRadius, core.searchLookHalfAngle);
            }

            // === If area complete, seed/pick cross-area target and commit a long move ===
            if (areaCompleteNow)
            {
                int seeded = core.BuildCrossAreaFrontier(_currentArea, core.transform.position, core.searchBatchSize);
                if (seeded > 0 && core.showDebugLogs)
                    Debug.Log($"<color=#7ABFFF>[Search]</color> {core.name}: Area {_currentArea} complete ({curCov * 100f:0.0}%). Seeded {seeded} cross-area targets.");

                if (core.TryGetNextSearchPoint(core.transform.position, out var cross))
                {
                    var save = core.searchAllowPortalTargets;
                    core.searchAllowPortalTargets = true;

                    TrySatisfyMinHop(core, ref cross);
                    Commit(core, cross, localPick: false, reason: "CrossAreaFrontier");

                    core.searchAllowPortalTargets = save;

                    // success → ease radius back toward base
                    DecayRadiusBoost();
                    return;
                }
            }
            else
            {
                // Not complete. If plateaued a while, reseed locally.
                if (Time.time - _lastCoverageT > PLATEAU_TIME && Time.time - _lastReseedT > PLATEAU_RESEED_COOLDOWN)
                {
                    core.RefreshFrontierForPosition(core.transform.position);
                    if (core.showDebugLogs)
                        Debug.Log($"<color=#7ABFFF>[Search]</color> {core.name}: Plateau reseed. cov={curCov:0.00}");
                    _lastReseedT = Time.time;
                    _pickTimer = 0.05f; // nudge a faster pick
                }
            }

            if (_isThinking)
            {
                _thinkTimer -= dt;
                if (_thinkTimer <= 0f)
                {
                    _isThinking = false;
                    SelectNewTarget(core);
                }
                return;
            }

            bool needNewTarget = core.Reached(core.CurrentFixedTarget, core.waypointTolerance);
            if (!needNewTarget)
            {
                _pickTimer -= dt;
                needNewTarget = _pickTimer <= 0f;
            }

            if (needNewTarget)
            {
                _isThinking = true;
                _thinkTimer = THINK_TIME;
            }
        }

        private void SelectNewTarget(EnemyAICore core)
        {
            _pickTimer = core.searchPickInterval;
            _bfsBudget = BFS_CALLS_PER_PICK;
            _pickSeq++;

            float coverage;
            bool areaComplete = AreaIsComplete(core, _currentArea, out coverage, out _);

            if (coverage > _bestCoverageThisArea)
                _bestCoverageThisArea = coverage;

            Vector3 newTarget;

            if (!areaComplete && _currentArea >= 0)
            {
                // Try local BFS first (budgeted)
                bool bfsSucceededThisPass = false;
                while (_bfsBudget > 0)
                {
                    if (core.TryGetUnsearchedPointInAreaLocal(_currentArea, core.transform.position, out newTarget, MAX_BFS_EXPANSIONS))
                    {
                        _consecutiveBfsFailsThisArea = 0;

                        int candArea = AreaIdAt(newTarget);
                        if (candArea != _currentArea && AvoidArea(candArea))
                        {
                            ConsumeBfsFail();
                            continue;
                        }

                        if (!IsGoodDistance(core.transform.position, newTarget) && !TrySatisfyMinHop(core, ref newTarget))
                        {
                            ConsumeBfsFail();
                            continue;
                        }

                        Commit(core, newTarget, localPick: true, reason: "LocalBFS");
                        bfsSucceededThisPass = true;
                        // success → ease radius back toward base
                        DecayRadiusBoost();
                        return;
                    }
                    ConsumeBfsFail();
                }

                // If BFS didn’t find anything good, try scored local candidates (with time budget)
                if (!bfsSucceededThisPass)
                {
                    newTarget = GetScoredLocalTargetInArea(core, _currentArea);
                    if (newTarget != Vector3.zero)
                    {
                        TrySatisfyMinHop(core, ref newTarget);
                        Commit(core, newTarget, localPick: true, reason: "LocalInAreaScored");
                        // success → ease radius
                        DecayRadiusBoost();
                        return;
                    }
                }

                // Try next search point (frontier) as local if it stays in-area
                if (core.TryGetNextSearchPoint(core.transform.position, out newTarget))
                {
                    int areaOfTarget = AreaIdAt(newTarget);

                    if (areaOfTarget == _currentArea && !_chunker.IsPortal(newTarget))
                    {
                        TrySatisfyMinHop(core, ref newTarget);
                        Commit(core, newTarget, localPick: true, reason: "FrontierLocal");
                        DecayRadiusBoost();
                        return;
                    }

                    if (areaOfTarget != _lastArea || !IsAreaOnCooldown(_lastArea))
                    {
                        if (areaOfTarget == _currentArea)
                        {
                            TrySatisfyMinHop(core, ref newTarget);
                            Commit(core, newTarget, localPick: true, reason: "FrontierLocalAllowPortal");
                            DecayRadiusBoost();
                            return;
                        }
                    }
                }

                // As a gentle fallback, nudge inside area
                var inward = NudgeInsideArea(core.transform.position);
                TrySatisfyMinHop(core, ref inward);
                Commit(core, inward, localPick: true, reason: "NudgeInsideArea");

                // we only expand radius if we completely failed to secure any local frontier
                // (this path still commits, but marks that we struggled to find something richer)
                BumpRadiusBoost();
                return;
            }

            // area complete → prefer cross-area picks (frontier was seeded in Tick)
            if (core.TryGetNextSearchPoint(core.transform.position, out newTarget))
            {
                TrySatisfyMinHop(core, ref newTarget);
                Commit(core, newTarget, localPick: false, reason: "CrossAreaFrontier");
                DecayRadiusBoost();
                return;
            }

            // Final fallback: scored local (global) within expanded radius
            newTarget = GetScoredLocalTarget(core);
            TrySatisfyMinHop(core, ref newTarget);
            Commit(core, newTarget, localPick: false, reason: "LocalRandomFallback");
            BumpRadiusBoost();
        }

        private void ConsumeBfsFail()
        {
            _bfsBudget--;
            _bfsFailsThisArea++;
            _consecutiveBfsFailsThisArea++;
            // frequent fails → broaden search
            if (_bfsBudget == 0) BumpRadiusBoost();
        }

        private void Commit(EnemyAICore core, Vector3 target, bool localPick, string reason = "unspecified")
        {
            TrySatisfyMinHop(core, ref target);

            int targetAreaId = AreaIdAt(target);
            if (!localPick && targetAreaId != _currentArea && core.showDebugLogs)
            {
                Debug.Log($"<color=#7ABFFF>[Search]</color> {core.name}: Committing cross-area target → area {targetAreaId} @ {target}");
            }

            if (!AreaIsComplete(core, _currentArea, out float currentCoverage, out _) &&
                currentCoverage < EffectiveTarget(core) * 0.5f &&
                _chunker != null && _chunker.IsPortal(target))
            {
                Vector2 dir = ((Vector2)target - (Vector2)core.transform.position).normalized;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
                target = core.transform.position + (Vector3)(dir * Mathf.Max(PORTAL_NUDGE_DIST, 1.5f));
            }

            if (AvoidArea(targetAreaId))
            {
                var alt = NudgeInsideArea(core.transform.position);
                int altArea = AreaIdAt(alt);
                if (alt != Vector3.zero && !AvoidArea(altArea))
                {
                    target = alt;
                    targetAreaId = altArea;
                    reason = reason + "+AreaAvoided→Inward";
                }
            }

            core.SetFixedTarget(target);
            RememberTarget(target);
            if (localPick) _localPicksThisArea++;
            RememberArea(_currentArea);

            if (core.showDebugLogs)
            {
                float dist = Vector2.Distance(core.transform.position, target);
                float covPct = Clamp01(core.GetAreaCoverageLocal(_currentArea)) * 100f;
                Debug.Log(
                    $"<color=#7ABFFF>[Search]</color> {core.name}: pick#{_pickSeq} reason={reason} local={localPick} area {_currentArea}->{targetAreaId} d={dist:0.0}m cov={covPct:0.0}% bfsFails={_bfsFailsThisArea}/{_consecutiveBfsFailsThisArea} radius={GetEffectiveRadius():0.0}");
            }
        }

        private void UpdateCurrentArea(EnemyAICore core, bool resetIfChanged)
        {
            int prev = _currentArea;
            _currentArea = AreaIdStrictOrSoft(core);

            if (resetIfChanged && _currentArea != prev)
            {
                _lastArea = prev;
                if (_lastArea >= 0)
                {
                    _areaCooldownUntil[_lastArea] = Time.time + EXIT_AREA_COOLDOWN_SECONDS;
                }

                _areaEnterTime = Time.time;
                _localPicksThisArea = 0;
                _bfsFailsThisArea = 0;
                _consecutiveBfsFailsThisArea = 0;

                _lastCoverage = 0f;
                _lastCoverageT = Time.time;
                _bestCoverageThisArea = 0f;

                core.RefreshFrontierForPosition(core.transform.position);
            }
        }

        private float EffectiveTarget(EnemyAICore core)
            => Mathf.Min(Clamp01(core.searchCoverageTarget), MAX_SEARCH_TARGET);

        private bool AreaIsComplete(EnemyAICore core, int areaId, out float coverage, out string reason)
        {
            reason = "incomplete";
            coverage = 0f;
            if (areaId < 0) { reason = "no-area"; return true; }

            float target = EffectiveTarget(core);
            coverage = Clamp01(core.GetAreaCoverageLocal(areaId));

            if (coverage - _lastCoverage > PLATEAU_DELTA)
            {
                _lastCoverage = coverage;
                _lastCoverageT = Time.time;
            }

            float dwellTime = Time.time - _areaEnterTime;
            bool dwellOk = dwellTime >= MIN_DWELL_TIME && _localPicksThisArea >= MIN_LOCAL_PICKS;

            float required = Mathf.Min(target + AREA_LEAVE_HYSTERESIS, MAX_SEARCH_TARGET);
            bool covOk = coverage >= required;

            if (covOk && dwellOk)
            {
                reason = "coverage";
                return true;
            }

            return false;
        }

        private int AreaIdStrictOrSoft(EnemyAICore core)
        {
            if (_chunker == null) return -1;
            int id = _chunker.GetAreaIdStrict(core.transform.position);
            return (id >= 0) ? id : _chunker.GetAreaId(core.transform.position);
        }

        private int AreaIdAt(Vector3 worldPos)
        {
            if (_chunker == null) return -1;
            int id = _chunker.GetAreaIdStrict(worldPos);
            return (id >= 0) ? id : _chunker.GetAreaId(worldPos);
        }

        private Vector3 NudgeInsideArea(Vector3 from)
        {
            if (_chunker == null || _currentArea < 0) return from;

            for (int i = 0; i < 16; i++)
            {
                float ang = (i * 22.5f + (Time.frameCount % 360)) * Mathf.Deg2Rad;
                float dist = PORTAL_NUDGE_DIST + 0.25f * (i % 3);
                Vector3 candidate = from + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * dist;
                int id = AreaIdAt(candidate);
                if (id == _currentArea && !_chunker.IsPortal(candidate))
                    return candidate;
            }
            return from;
        }

        // ── Scored candidate selection with time budget + hysteresis ─────────────
        private Vector3 GetScoredLocalTarget(EnemyAICore core)
        {
            // Global (not restricted to current area), but still avoids recent/blocked/cooldown areas
            return GetScoredLocalTargetInternal(core, areaIdFilter: -1);
        }

        private Vector3 GetScoredLocalTargetInArea(EnemyAICore core, int areaId)
        {
            return GetScoredLocalTargetInternal(core, areaId);
        }

        private Vector3 GetScoredLocalTargetInternal(EnemyAICore core, int areaIdFilter)
        {
            Vector2 pos = core.transform.position;
            float tStart = Time.realtimeSinceStartup;

            float radius = GetEffectiveRadius();
            Vector3 best = Vector3.zero;
            float bestScore = float.NegativeInfinity;
            int considered = 0;

            // Sample a ring of candidates; we bias by radius but evaluate with scoring.
            for (int i = 0; i < MAX_LOCAL_CANDIDATES; i++)
            {
                float a = (i * (360f / Mathf.Max(1, MAX_LOCAL_CANDIDATES))) * Mathf.Deg2Rad;
                float dist = Random.Range(Mathf.Max(2.5f, _effectiveMinHop), radius);
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Vector3 cand = pos + dir * dist;

                // Area filter / cooldown avoidance
                int candArea = AreaIdAt(cand);
                if (areaIdFilter >= 0 && candArea != areaIdFilter) continue;
                if (AvoidArea(candArea)) continue;

                if (TooCloseToRecent(cand)) continue;
                if (IsBlocked(core, pos, cand)) continue;

                // Enforce min hop (allow projection)
                Vector3 eval = cand;
                if (!IsGoodDistance(core.transform.position, eval) && !TrySatisfyMinHop(core, ref eval))
                    continue;

                float s = ScoreCandidate(pos, eval);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = eval;
                }

                considered++;

                // Time budget
                if ((Time.realtimeSinceStartup - tStart) * 1000f > SELECTION_TIME_BUDGET_MS)
                    break;
            }

            if (best != Vector3.zero)
            {
                RememberTarget(best);
                return best;
            }

            // As a last resort, toss a random point within radius (respecting min hop and area avoidance)
            Vector2 randomDir = Random.insideUnitCircle.normalized;
            Vector3 result = pos + randomDir * Mathf.Max(_effectiveMinHop, radius * 0.5f);
            if (!AvoidArea(AreaIdAt(result)))
            {
                TrySatisfyMinHop(core, ref result);
                RememberTarget(result);
            }
            return result;
        }

        private float ScoreCandidate(Vector2 from, Vector3 candidate)
        {
            float score = 0f;

            // Distance shaping: prefer distances at or above min hop, gently penalize beyond radius
            float d = Vector2.Distance(from, candidate);
            float r = GetEffectiveRadius();

            if (d >= _effectiveMinHop)
                score += 0.1f * Mathf.Clamp01((d - _effectiveMinHop) / Mathf.Max(0.001f, r * 0.5f)); // small nudge for not-too-short legs

            if (d > r)
                score -= 0.05f * Mathf.Clamp01((d - r) / Mathf.Max(0.001f, r)); // discourage excessive leaps

            // Directional hysteresis: reward continuing roughly in the last travel direction
            Vector2 toCand = ((Vector2)candidate - from);
            if (toCand.sqrMagnitude > 0.0001f)
            {
                toCand.Normalize();
                float dot = Vector2.Dot(toCand, _lastTravelDir); // [-1..1]
                score += ((dot + 1f) * 0.5f) * DIR_HYSTERESIS_WEIGHT; // map to [0..1]
            }

            // Light penalty if very close to any recent target (beyond the hard MIN_SEPARATION gate)
            for (int i = 0; i < _recent.Length; i++)
            {
                var p = _recent[i];
                if (p == Vector3.zero) continue;
                float dd = Vector2.Distance(p, candidate);
                if (dd < MIN_SEPARATION * 1.5f)
                {
                    score -= Mathf.Clamp01((MIN_SEPARATION * 1.5f - dd) / (MIN_SEPARATION * 1.5f)) * 0.25f;
                }
            }

            return score;
        }

        private float GetEffectiveRadius()
        {
            return BASE_SEARCH_RADIUS + Mathf.Clamp(_radiusBoost, 0f, RADIUS_MAX_BOOST);
        }

        private void BumpRadiusBoost()
        {
            _radiusBoost = Mathf.Min(_radiusBoost + RADIUS_STEP, RADIUS_MAX_BOOST);
        }

        private void DecayRadiusBoost()
        {
            if (_radiusBoost <= 0f) return;
            _radiusBoost = Mathf.Max(0f, _radiusBoost - RADIUS_COOLDOWN);
        }

        private bool ShouldMark(EnemyAICore core)
        {
            int stride = Mathf.Max(1, core.searchMarkStrideFrames);
            return ((Time.frameCount + core.enemyID) % stride) == 0;
        }

        private bool TooCloseToRecent(Vector3 pos)
        {
            for (int i = 0; i < _recent.Length; i++)
            {
                if (_recent[i] != Vector3.zero && Vector2.Distance(_recent[i], pos) < MIN_SEPARATION)
                    return true;
            }
            return false;
        }

        private void RememberTarget(Vector3 target)
        {
            _recent[_recentIndex] = target;
            _recentIndex = (_recentIndex + 1) % _recent.Length;
        }

        private bool IsBlocked(EnemyAICore core, Vector2 from, Vector2 to)
        {
            int mask = core.searchOccluderMask.value != 0
                ? core.searchOccluderMask.value
                : (core.visionBlockMask.value != 0 ? core.visionBlockMask.value : Physics2D.DefaultRaycastLayers);

            return Physics2D.Linecast(from, to, mask).collider != null;
        }

        private void RememberArea(int areaId)
        {
            if (areaId < 0) return;
            if (_recentAreas.Count == RECENT_AREA_MEMORY)
                _recentAreas.Dequeue();
            _recentAreas.Enqueue(areaId);
        }

        private bool WasAreaRecentlyVisited(int areaId)
        {
            if (areaId < 0) return false;
            foreach (var a in _recentAreas)
                if (a == areaId) return true;
            return false;
        }

        private bool IsAreaOnCooldown(int areaId)
        {
            if (areaId < 0) return false;
            if (_areaCooldownUntil.TryGetValue(areaId, out float until))
                return Time.time < until;
            return false;
        }

        private bool AvoidArea(int areaId)
        {
            if (areaId < 0) return false;
            if (IsAreaOnCooldown(areaId)) return true;
            if (WasAreaRecentlyVisited(areaId)) return true;
            return false;
        }

        private static float Clamp01(float v) => (v < 0f) ? 0f : (v > 1f ? 1f : v);

        private bool IsGoodDistance(Vector3 from, Vector3 to)
        {
            float dist = Vector2.Distance(from, to);
            return dist >= _effectiveMinHop;
        }

        private bool TrySatisfyMinHop(EnemyAICore core, ref Vector3 target)
        {
            if (IsGoodDistance(core.transform.position, target))
                return true;

            // Try a few frontier picks as alternates (cheap)
            for (int i = 0; i < 10; i++)
            {
                if (core.TryGetNextSearchPoint(core.transform.position, out var alt))
                {
                    if (IsGoodDistance(core.transform.position, alt))
                    {
                        target = alt;
                        return true;
                    }
                }
            }

            // Project outward along desired direction
            Vector2 dir = ((Vector2)target - (Vector2)core.transform.position);
            if (dir.sqrMagnitude < 1e-6f)
            {
                float ang = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            }
            dir.Normalize();

            float projDist = Mathf.Max(_effectiveMinHop * 2.0f, 2.25f);
            Vector3 projected = core.transform.position + (Vector3)(dir * projDist);

            int projArea = AreaIdAt(projected);
            if (!AvoidArea(projArea))
            {
                target = projected;
                return true;
            }

            // As a last resort, step inward within current area
            var inward = NudgeInsideArea(core.transform.position);
            if (IsGoodDistance(core.transform.position, inward))
            {
                target = inward;
                return true;
            }

            target = projected;
            return true;
        }

        private static AreaChunker2D FindChunker(EnemyAICore core)
        {
            if (core != null)
            {
                return core.GetComponent<AreaChunker2D>()
                       ?? core.GetComponentInParent<AreaChunker2D>()
                       ?? Object.FindObjectOfType<AreaChunker2D>(true);
            }
            return Object.FindObjectOfType<AreaChunker2D>(true);
        }

        private void SetInitialTarget(EnemyAICore core)
        {
            Vector3 target = GetScoredLocalTargetInArea(core, AreaIdStrictOrSoft(core));
            if (target == Vector3.zero)
                target = GetScoredLocalTarget(core);

            core.SetFixedTarget(target);
            _areaEnterTime = Time.time;
            _localPicksThisArea = 1;

            _lastCoverage = 0f;
            _lastCoverageT = Time.time;
            _bestCoverageThisArea = 0f;
        }
    }
}
