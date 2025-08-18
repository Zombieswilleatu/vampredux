// -----------------------------
// File: EnemyAI/Perception/EnemyAICore.DetectionHandlers.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    // NOTE: Do NOT redefine DetectionMode or the preset fields here.
    // Keep your original EnemyAICore.Detection.cs as-is.

    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Perception Masks")]
        public LayerMask visionBlockMask;   // used by SearchState LOS
        public LayerMask visionTargetsMask; // optional (sensor uses its own targetsMask)
        public LayerMask trapMask;          // optional (sensor uses its own trapMask)

        [Header("Perception Debug")]
        public bool verboseDetectionLogs = false;

        /// <summary>Called by PerceptionSensor2D.</summary>
        public void OnSensorDetected(DetectionHit hit)
        {
            if (verboseDetectionLogs)
                LogAI($"Detect [{hit.type}] {(hit.target ? hit.target.name : "<noise>")} @ {hit.position} d={hit.distance:0.0} hear={hit.viaHearing}");

            switch (hit.type)
            {
                case DetectableType.Player: HandlePlayerSpotted(hit); break;
                case DetectableType.Enemy: HandleEnemySpotted(hit); break;
                case DetectableType.Trap: HandleTrapSpotted(hit); break;
                case DetectableType.Noise:
                default: HandleNoiseHeard(hit); break;
            }
        }

        private void HandlePlayerSpotted(DetectionHit hit)
        {
            targetTransform = hit.target;
            LastKnownTargetPos = hit.position;
            BroadcastContact(hit.position, 1f);
            SwitchState(EnemyState.Chase);
        }

        private void HandleEnemySpotted(DetectionHit hit)
        {
            if (verboseDetectionLogs) LogAI($"Saw enemy-like entity: {hit.target?.name}");
        }

        private void HandleTrapSpotted(DetectionHit hit)
        {
            MarkDanger(hit.position, 1.25f, 30f);
            if (verboseDetectionLogs) LogAI($"Trap spotted @ {hit.position}");
        }

        private void HandleNoiseHeard(DetectionHit hit)
        {
            LastKnownTargetPos = hit.position;
            ResetSearchPlan(hit.position);
            SwitchState(EnemyState.Search);
        }

        // Hooks (safe no-ops unless you wire them)
        private void BroadcastContact(Vector2 pos, float confidence) { /* integrate with comms if desired */ }
        private void MarkDanger(Vector2 pos, float radius, float decaySeconds) { /* integrate with danger map */ }
    }
}
