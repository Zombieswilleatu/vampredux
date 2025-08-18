// -----------------------------
// File: EnemyAICore.SearchTuning.cs
// Purpose: ONE place for all search knobs (UI + skill mapping) used by
//          SearchState + SearchMemory. Also hosts the single OnValidate().
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
        public float searchMarkInterval = 0.50f;

        [Min(1)]
        [Tooltip("Split the search cone into this many wedges; one wedge is marked per tick.")]
        public int searchMarkSegments = 6;

        [Header("Sweep Shape")]
        [Tooltip("Base radius (meters) of a single sweep (before radius boost).")]
        public float searchLookSweepRadius = 0.6f;

        [Range(1f, 180f)]
        [Tooltip("Half-angle (degrees) of the sweep (before angle boost).")]
        public float searchLookHalfAngle = 25f;

        [Min(1)]
        [Tooltip("Hard cap for how many grid cells a single sweep may mark.")]
        public int searchMaxMarksPerSweep = 160; // used by SearchMemory sweeps

        [Header("Ray Budget (for occlusion-aware sweeping)")]
        [Min(1)]
        [Tooltip("Rays cast per sweep when using the budgeted raycasting variant.")]
        public int searchRaysPerSweep = 7;

        [Min(100)]
        [Tooltip("Global cap across ALL enemies per frame (used by TryConsumeBudget).")]
        public int searchGlobalRayBudgetPerFrame = 1500;

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
        [Min(1f)] public float searchRadiusBoost = 1.15f;
        [Min(1f)] public float searchAngleBoost = 1.20f;

        // ===================== Coverage & LOS =====================
        [Header("Area Coverage Targets")]
        [Range(0.0f, 1.0f)] public float searchCoverageTarget = 0.96f;
        [Min(0)] public int searchEmptyChecksBeforeLeave = 3;

        [Header("LOS Heuristics (SearchState)")]
        [Min(0f)] public float searchAcrossWallMaxDist = 6.0f;
        [Min(0f)] public float searchLosNearBlockDist = 2.0f;
        [Range(0f, 1f)] public float searchBlockHitNear = 0.80f;

        [Header("Sweep LOS (SearchMemory cone)")]
        [Tooltip("If true, sweep marks will linecast to each candidate to respect occluders.")]
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
        /// <summary>Recompute tunables from the 1–10 skill knob.</summary>
        public void ApplySearchTuningFromSkill()
        {
            if (!searchUseSkillMapping) return;

            // t: 0 at skill=1, 1 at skill=10
            float t = Mathf.Clamp01((searchSkill - 1) / 9f);

            // --- Coverage & persistence ---
            searchCoverageTarget = Mathf.Lerp(0.82f, 0.99f, t);
            searchEmptyChecksBeforeLeave = Mathf.RoundToInt(Mathf.Lerp(1f, 5f, t));

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

            // --- Cone base shape (stronger at high skill) ---
            searchLookSweepRadius = Mathf.Lerp(0.6f, 3.5f, t);
            searchLookHalfAngle = Mathf.Lerp(25f, 80f, t);

            // --- Cone boosts ---
            searchRadiusBoost = Mathf.Lerp(1.05f, 1.50f, t);
            searchAngleBoost = Mathf.Lerp(1.05f, 1.60f, t);

            // --- Marking clamp & budgets ---
            searchMaxMarksPerSweep = Mathf.RoundToInt(Mathf.Lerp(140f, 600f, t));
            searchRaysPerSweep = Mathf.RoundToInt(Mathf.Lerp(5f, 18f, t));
            searchGlobalRayBudgetPerFrame = Mathf.RoundToInt(Mathf.Lerp(1200f, 6000f, t));
        }

#if UNITY_EDITOR
        // SINGLE unified OnValidate for EnemyAICore
        private void OnValidate()
        {
            if (searchUseSkillMapping)
                ApplySearchTuningFromSkill();

            // keep auto-sizing responsive in editor (method lives in Sizing partial)
            try { CacheAgentRadius(true); } catch { /* fine if other partial not compiled yet */ }

            // clamps
            searchLookSweepRadius  = Mathf.Max(0.1f, searchLookSweepRadius);
            searchLookHalfAngle    = Mathf.Clamp(searchLookHalfAngle, 1f, 180f);
            searchMaxMarksPerSweep = Mathf.Max(1,   searchMaxMarksPerSweep);
            searchRaysPerSweep     = Mathf.Max(1,   searchRaysPerSweep);
            searchGlobalRayBudgetPerFrame = Mathf.Max(100, searchGlobalRayBudgetPerFrame);

            searchBatchSize        = Mathf.Max(1,   searchBatchSize);
            searchStepMaxDist      = Mathf.Max(0.1f,searchStepMaxDist);
            searchMinHopDist       = Mathf.Max(0f,  searchMinHopDist);
            searchExpandRadius     = Mathf.Max(0.1f,searchExpandRadius);

            searchLOSStartInset    = Mathf.Max(0f,  searchLOSStartInset);
        }
#endif
    }
}
