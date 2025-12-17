// -----------------------------
// File: EnemyAICore.SearchTuning.cs
// Purpose: ONE place for all search knobs used by SearchState + SearchMemory.
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        // ===================== Skill & Mapping =====================
        [Header("Search Skill")]
        [Range(1, 10)] public int searchSkill = 6;

        [Tooltip("When ON, values below are derived from Search Skill via ApplySearchTuningFromSkill().")]
        public bool searchUseSkillMapping = true;

        // ===================== Marking & Sweep =====================
        [Header("Search Marking (runtime)")]
        [Tooltip("Master switch for writing search coverage while moving / scanning.")]
        public bool enableSearchMarking = true;

        [Min(0.05f)]
        [Tooltip("Interval (seconds) between continuous MarkSearchedCone ticks while moving.")]
        public float searchMarkInterval = 0.35f; // tuned default (trail frequency)

        [Min(1)]
        [Tooltip("Split the search cone into this many wedges; one wedge is marked per tick (legacy).")]
        public int searchMarkSegments = 6;

        [Header("Sweep Shape")]
        [Tooltip("Base radius (meters) of a single sweep (before radius boost).")]
        public float searchLookSweepRadius = 0.9f; // tuned default (0.6–1.2 sweet spot)

        [Range(1f, 180f)]
        [Tooltip("Half-angle (degrees) of the sweep (before angle boost).")]
        public float searchLookHalfAngle = 30f; // tuned default (20–35)

        [Min(1)]
        [Tooltip("Hard cap for how many grid cells a single sweep may mark (used by both dense + trail variants).")]
        public int searchMaxMarksPerSweep = 80; // tuned default (60–90)

        [Header("Ray Budget (for occlusion-aware sweeping)")]
        [Min(1)]
        [Tooltip("Rays cast per sweep when using the budgeted raycasting variant.")]
        public int searchRaysPerSweep = 5; // tuned default (3–5)

        [Min(100)]
        [Tooltip("Global cap across ALL enemies per frame (used by TryConsumeBudget fallback or a manager).")]
        public int searchGlobalRayBudgetPerFrame = 1500;

        // --- New: Governor for trail work distribution ---
        [Header("Marking Governor")]
        [Min(1)] public int searchMarkStrideFrames = 2;   // mark every Nth frame per agent
        [Min(0f)] public float searchTrailMinMove = 0.08f; // require a bit of movement to mark

        // ===================== Frontier / Local Plan =====================
        [Header("Search Frontier (local plan)")]
        [Min(1)]
        [Tooltip("How many candidates to seed per frontier expansion.")]
        public int searchBatchSize = 30;

        [Min(0.1f)]
        [Tooltip("Max preferred hop length when picking the next point (meters).")]
        public float searchStepMaxDist = 4.0f;

        [Min(0f)]
        [Tooltip("Reject frontier points closer than this to current position (meters).")]
        public float searchMinHopDist = 0.6f;

        [Min(0.1f)]
        [Tooltip("How far the frontier radius expands when it runs out of work (meters).")]
        public float searchExpandRadius = 6.0f;

        // ===================== Rhythm & Dwell =====================
        [Header("Search Rhythm & Dwell")]
        [Min(0.1f)] public float searchPickInterval = 1.25f;

        [Min(0f)] public float searchLookHoldMin = 0.12f;
        [Min(0f)] public float searchLookHoldMax = 0.35f;
        [Min(0f)] public float searchLookHoldPerMeter = 0.08f;

        // ===================== Local Sampling =====================
        [Header("Local Sampling (same-side poke)")]
        [Min(0f)] public float searchLocalSampleRMin = 0.9f;
        [Min(0f)] public float searchLocalSampleRMax = 2.25f;
        [Min(1)] public int searchLocalSampleCount = 16;

        // ===================== Cone Multipliers =====================
        [Header("Cone Boosts")]
        [Min(1f)] public float searchRadiusBoost = 1.10f; // tuned default (1.05–1.15)
        [Min(1f)] public float searchAngleBoost = 1.15f;  // tuned default (1.05–1.20)

        // ===================== Coverage & LOS =====================
        [Header("Area Coverage Targets")]
        [Range(0.0f, 1.0f)] public float searchCoverageTarget = 0.96f;

        [Tooltip("Slack above target that counts as 'done' and allows leaving immediately.")]
        [Range(0f, 0.2f)] public float searchCoverageDoneSlack = 0.03f;

        [Min(0)] public int searchEmptyChecksBeforeLeave = 3;

        [Tooltip("How many same-side 'local pokes' allowed before leaving.")]
        [Min(0)] public int searchMaxLocalPokesBeforeLeave = 2;

        [Tooltip("Counts as no progress if coverage improves less than this per pick.")]
        [Range(0f, 0.1f)] public float searchCoverageStagnationEpsilon = 0.02f;

        [Tooltip("Consecutive stagnant picks before leaving.")]
        [Min(0)] public int searchStagnantPicksBeforeLeave = 2;

        [Header("LOS Heuristics (SearchState)")]
        [Min(0f)] public float searchAcrossWallMaxDist = 6.0f;
        [Min(0f)] public float searchLosNearBlockDist = 2.0f;
        [Range(0f, 1f)] public float searchBlockHitNear = 0.80f;

        [Header("Sweep LOS (SearchMemory cone)")]
        [Tooltip("If true, dense sweep marks linecast to each candidate to respect occluders.")]
        public bool searchUseLOS = true;

        [Tooltip("LayerMask for occluders used by sweep LOS checks.")]
        public LayerMask searchOccluderMask;

        [Min(0f)]
        [Tooltip("Inset (meters) from the origin to start LOS linecasts to avoid self-hits.")]
        public float searchLOSStartInset = 0.10f;

        [Header("Portals")]
        [Tooltip("If true we may pick portal cells when leaving an area. While finishing an area we still avoid portals.")]
        public bool searchAllowPortalTargets = true;

        // ===================== Skill → Tuning mapping =====================
        public void ApplySearchTuningFromSkill()
        {
            if (!searchUseSkillMapping) return;

            float t = Mathf.Clamp01((searchSkill - 1) / 9f);

            // --- Coverage & persistence ---
            searchCoverageTarget = Mathf.Lerp(0.82f, 0.99f, t);
            searchCoverageDoneSlack = Mathf.Lerp(0.06f, 0.02f, t);
            searchEmptyChecksBeforeLeave = Mathf.RoundToInt(Mathf.Lerp(1f, 5f, t));
            searchMaxLocalPokesBeforeLeave = Mathf.RoundToInt(Mathf.Lerp(1f, 3f, t));
            searchCoverageStagnationEpsilon = Mathf.Lerp(0.03f, 0.01f, t);
            searchStagnantPicksBeforeLeave = Mathf.RoundToInt(Mathf.Lerp(1f, 3f, t));

            // --- Rhythm / dwell ---
            searchPickInterval = Mathf.Lerp(1.60f, 0.70f, t);
            searchLookHoldMin = Mathf.Lerp(0.08f, 0.18f, t);
            searchLookHoldMax = Mathf.Lerp(0.25f, 0.55f, t);
            searchLookHoldPerMeter = Mathf.Lerp(0.06f, 0.12f, t);

            // --- Local sampling ---
            searchLocalSampleCount = Mathf.RoundToInt(Mathf.Lerp(12f, 28f, t));
            searchLocalSampleRMin = Mathf.Lerp(0.75f, 1.25f, t);
            searchLocalSampleRMax = Mathf.Lerp(2.00f, 3.50f, t);

            // --- Frontier / plan scale ---
            searchStepMaxDist = Mathf.Lerp(3.0f, 6.0f, t);
            searchMinHopDist = Mathf.Lerp(0.5f, 1.0f, t);
            searchExpandRadius = Mathf.Lerp(4.0f, 9.0f, t);
            searchBatchSize = Mathf.RoundToInt(Mathf.Lerp(20f, 40f, t));

            // --- LOS heuristics ---
            searchAcrossWallMaxDist = Mathf.Lerp(5.0f, 8.5f, t);
            searchLosNearBlockDist = Mathf.Lerp(1.6f, 2.5f, t);
            searchBlockHitNear = Mathf.Lerp(0.65f, 0.90f, t);

            // --- Cone base shape ---
            searchLookSweepRadius = Mathf.Lerp(0.6f, 3.5f, t);
            searchLookHalfAngle = Mathf.Lerp(25f, 80f, t);

            // --- Cone boosts ---
            searchRadiusBoost = Mathf.Lerp(1.05f, 1.50f, t);
            searchAngleBoost = Mathf.Lerp(1.05f, 1.60f, t);

            // --- Marking clamp & budgets ---
            searchMaxMarksPerSweep = Mathf.RoundToInt(Mathf.Lerp(140f, 600f, t));
            searchRaysPerSweep = Mathf.RoundToInt(Mathf.Lerp(5f, 18f, t));
            searchGlobalRayBudgetPerFrame = Mathf.RoundToInt(Mathf.Lerp(1200f, 6000f, t));

            // (Intentionally not mapping searchMarkStrideFrames / searchTrailMinMove;
            // keep those as designer performance knobs.)
        }

#if UNITY_EDITOR
        // SINGLE unified OnValidate for EnemyAICore
        private void OnValidate()
        {
            if (searchUseSkillMapping)
                ApplySearchTuningFromSkill();

            // clamps
            searchLookSweepRadius  = Mathf.Max(0.1f, searchLookSweepRadius);
            searchLookHalfAngle    = Mathf.Clamp(searchLookHalfAngle, 1f, 180f);
            searchMaxMarksPerSweep = Mathf.Max(1,   searchMaxMarksPerSweep);
            searchRaysPerSweep     = Mathf.Max(1,   searchRaysPerSweep);
            searchGlobalRayBudgetPerFrame = Mathf.Max(100, searchGlobalRayBudgetPerFrame);

            searchBatchSize   = Mathf.Max(1,    searchBatchSize);
            searchStepMaxDist = Mathf.Max(0.1f, searchStepMaxDist);
            searchMinHopDist  = Mathf.Max(0f,   searchMinHopDist);
            searchExpandRadius= Mathf.Max(0.1f, searchExpandRadius);

            // governor clamps
            searchMarkStrideFrames = Mathf.Max(1, searchMarkStrideFrames);
            searchTrailMinMove     = Mathf.Max(0f, searchTrailMinMove);

            searchLOSStartInset    = Mathf.Max(0f,  searchLOSStartInset);

            searchCoverageDoneSlack         = Mathf.Clamp(searchCoverageDoneSlack, 0f, 0.2f);
            searchEmptyChecksBeforeLeave    = Mathf.Max(0, searchEmptyChecksBeforeLeave);
            searchMaxLocalPokesBeforeLeave  = Mathf.Max(0, searchMaxLocalPokesBeforeLeave);
            searchCoverageStagnationEpsilon = Mathf.Clamp(searchCoverageStagnationEpsilon, 0f, 0.1f);
            searchStagnantPicksBeforeLeave  = Mathf.Max(0, searchStagnantPicksBeforeLeave);
        }
#endif
    }
}
