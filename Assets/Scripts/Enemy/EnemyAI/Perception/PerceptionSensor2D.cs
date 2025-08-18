// -----------------------------
// File: EnemyAI/Perception/PerceptionSensor2D.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EnemyAICore))]
    public sealed class PerceptionSensor2D : MonoBehaviour
    {
        [Header("Mask Source")]
        [Tooltip("When ON, read masks from EnemyAICore. Turn OFF to use overrides below.")]
        public bool useCoreMasks = true;

        [Header("Mask Overrides (used only if useCoreMasks = false)")]
        public LayerMask obstacleMaskOverride; // walls/geometry
        public LayerMask targetsMaskOverride;  // player/enemy/etc.
        public LayerMask trapMaskOverride;     // traps, if separated

        [Header("Scan")]
        [Tooltip("Max colliders to check per scan.")]
        public int maxColliders = 32;

        private EnemyAICore _core;
        private Rigidbody2D _rb;
        private Collider2D[] _hits;
        private float _cooldownTimer;

        // Effective masks (resolved each Update in case presets change)
        private LayerMask ObstacleMask => useCoreMasks ? _core.visionBlockMask : obstacleMaskOverride;
        private LayerMask TargetsMask => useCoreMasks ? _core.visionTargetsMask : targetsMaskOverride;
        private LayerMask TrapMask => useCoreMasks ? _core.trapMask : trapMaskOverride;

        private void Awake()
        {
            _core = GetComponent<EnemyAICore>();
            _rb = GetComponent<Rigidbody2D>();
            _hits = new Collider2D[Mathf.Max(8, maxColliders)];
        }

        private void Update()
        {
            _cooldownTimer -= Time.deltaTime;
            if (_cooldownTimer > 0f) return;
            _cooldownTimer = Mathf.Max(0.02f, _core.detectionCooldown);

            Vector2 origin = transform.position;
            Vector2 heading = GetHeading();

            VisionScan(origin, heading, _core.visionRange, _core.visionFOVDeg, TargetsMask);
            VisionScan(origin, heading, _core.visionRange, 360f, TrapMask);
        }

        public void HearNoise(Vector2 worldPos, float loudness = 1f)
        {
            float hearRadius = _core.hearingRadius * Mathf.Max(0.1f, loudness);
            if (Vector2.Distance(worldPos, (Vector2)transform.position) <= hearRadius)
            {
                var hit = new DetectionHit
                {
                    type = DetectableType.Noise,
                    target = null,
                    position = worldPos,
                    distance = Vector2.Distance(worldPos, (Vector2)transform.position),
                    viaHearing = true
                };
                _core.OnSensorDetected(hit);
            }
        }

        private Vector2 GetHeading()
        {
            if (_rb != null && _rb.velocity.sqrMagnitude > 0.0025f)
                return _rb.velocity.normalized;

            Vector2 to = (Vector2)(_core.CurrentFixedTarget - transform.position);
            if (to.sqrMagnitude > 1e-6f) return to.normalized;

            return Vector2.right;
        }

        private void VisionScan(Vector2 origin, Vector2 heading, float range, float fovDeg, LayerMask queryMask)
        {
            if (range <= 0.01f || queryMask.value == 0) return;

            int count = Physics2D.OverlapCircleNonAlloc(origin, range, _hits, queryMask);
            if (count <= 0) return;

            float halfFov = Mathf.Clamp(fovDeg * 0.5f, 0f, 180f);

            for (int i = 0; i < count; i++)
            {
                var col = _hits[i];
                if (!col) continue;
                Transform t = col.transform;
                if (t == transform) continue;

                Vector2 pos = t.position;
                Vector2 dir = pos - origin;
                float dist = dir.magnitude;
                if (dist <= 0.001f) continue;

                if (halfFov < 179.9f)
                {
                    if (Vector2.Angle(heading, dir) > halfFov) continue;
                }

                if (Physics2D.Linecast(origin, pos, ObstacleMask).collider != null) continue;

                DetectableType type = DetectableType.Unknown;
                var det = t.GetComponentInParent<Detectable>();
                if (det) type = det.type;
                else
                {
                    if (t.CompareTag("Player")) type = DetectableType.Player;
                    else if (t.CompareTag("Enemy")) type = DetectableType.Enemy;
                    else if (t.CompareTag("Trap")) type = DetectableType.Trap;
                }

                var hit = new DetectionHit
                {
                    type = type,
                    target = t,
                    position = pos,
                    distance = dist,
                    viaHearing = false
                };
                _core.OnSensorDetected(hit);
            }
        }
    }

    public struct DetectionHit
    {
        public DetectableType type;
        public Transform target;
        public Vector2 position;
        public float distance;
        public bool viaHearing;
    }
}
