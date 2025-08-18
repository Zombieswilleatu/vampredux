// -----------------------------
// File: EnemyAICore.Sizing.cs (partial)
// Purpose: Auto-compute per-enemy clearance radius from its 2D colliders,
//          with a manual override for special cases.
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Clearance / Size")]
        [Tooltip("If ON, compute agent radius from enabled 2D colliders. If OFF, use Manual Agent Radius.")]
        [SerializeField] private bool autoClearance = true;

        [Tooltip("Used when Auto Clearance is OFF. Roughly equals 'radius' for a circle body.")]
        [SerializeField] private float manualAgentRadius = 0.3f;

        [Tooltip("Multiplier applied after auto/manual radius is chosen. Useful for tuning squeeze/avoidance without changing colliders.")]
        [SerializeField] private float clearanceScale = 1.0f;

        // NOTE: Another partial declares: private float agentRadius = -1f;

        // Removed OnValidate() — we now centralize it in SearchTuning.cs to avoid CS0111.

        internal void CacheAgentRadius(bool forceRecompute = false)
        {
            if (!forceRecompute && agentRadius > 0f) return;

            if (!autoClearance)
            {
                agentRadius = Mathf.Max(0.01f, manualAgentRadius) * Mathf.Max(0.01f, clearanceScale);
                return;
            }

            // AUTO: compute from all enabled 2D colliders, using bounds (scale-aware).
            float maxR = 0.0f;
            var cols = GetComponents<Collider2D>();
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || !c.enabled || c.isTrigger) continue;

                var b = c.bounds; // includes transform scale
                float r = Mathf.Max(b.extents.x, b.extents.y); // circumscribed circle radius
                if (r > maxR) maxR = r;
            }

            // Fallback if no colliders found/enabled
            if (maxR <= 0.0001f)
            {
                var cc = GetComponent<CircleCollider2D>();
                if (cc != null)
                {
                    float scale = Mathf.Max(Mathf.Abs(transform.localScale.x), Mathf.Abs(transform.localScale.y));
                    maxR = Mathf.Abs(cc.radius) * scale;
                }
            }

            if (maxR <= 0.0001f) maxR = 0.3f; // last resort

            agentRadius = Mathf.Max(0.01f, maxR) * Mathf.Max(0.01f, clearanceScale);
        }

#if UNITY_EDITOR
        [ContextMenu("Recompute Agent Radius Now")]
        private void Editor_RecomputeAgentRadius()
        {
            CacheAgentRadius(true);
        }
#endif
    }
}
