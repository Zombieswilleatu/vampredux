// EnemyAICore.Detection.cs (partial)
using UnityEngine;

namespace EnemyAI
{
    public enum DetectionMode { Patrol, Search, Alerted, Combat }

    public partial class EnemyAICore : MonoBehaviour
    {
        // Your existing perception fields (example names)
        [Header("Perception")]
        [SerializeField] internal float visionRange = 8f;
        [SerializeField] internal float visionFOVDeg = 110f;
        [SerializeField] internal float hearingRadius = 4f;
        [SerializeField] internal float detectionCooldown = 0.2f;

        private DetectionMode _mode = DetectionMode.Patrol;

        public void ApplyDetectionPreset(DetectionMode mode)
        {
            _mode = mode;
            switch (mode)
            {
                case DetectionMode.Patrol:
                    visionRange = 7f; visionFOVDeg = 100f; hearingRadius = 3f; detectionCooldown = 0.25f; break;
                case DetectionMode.Search:
                    visionRange = 9f; visionFOVDeg = 140f; hearingRadius = 5f; detectionCooldown = 0.15f; break;
                case DetectionMode.Alerted:
                    visionRange = 11f; visionFOVDeg = 160f; hearingRadius = 6f; detectionCooldown = 0.10f; break;
                case DetectionMode.Combat:
                    visionRange = 12f; visionFOVDeg = 180f; hearingRadius = 7f; detectionCooldown = 0.08f; break;
            }
            LogAI($"Detection preset -> {mode} (range={visionRange}, fov={visionFOVDeg}, hear={hearingRadius})");
        }
    }
}
