// -----------------------------
// File: States/ChaseState.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI.States
{
    public class ChaseState : IEnemyState
    {
        public string DebugName => "Chase";

        public void OnEnter(EnemyAICore core)
        {
            if (core.showDebugLogs) Debug.Log("[AI] Enter Chase");

            // Try to (re)acquire target if missing
            if (core.targetTransform == null && !string.IsNullOrEmpty(core.targetTag))
            {
                var go = GameObject.FindGameObjectWithTag(core.targetTag);
                if (go != null) core.targetTransform = go.transform;
            }

            if (core.targetTransform != null)
            {
                // Start following the transform and force an immediate path
                core.SetTarget(core.targetTransform);   // switches to FollowTarget mode internally
                core.ForceRepathNow();
                // Store a last known in case we lose sight
                core.LastKnownTargetPos = core.targetTransform.position;
            }
            else
            {
                // No target found -> go investigate last known if we have one, otherwise idle
                core.SetFixedTarget(core.LastKnownTargetPos);
                core.SwitchState(EnemyState.Investigate);
            }
        }

        public void OnExit(EnemyAICore core) { }

        public void Tick(EnemyAICore core, float dt)
        {
            // If we still have a target, keep updating last known
            if (core.targetTransform != null)
            {
                core.LastKnownTargetPos = core.targetTransform.position;

                // Let the normal request loop handle repaths; only nudge if we’ve drifted far
                // (Optional) Force a repath if target jumped a long distance:
                // if (Vector2.Distance(core.LastKnownTargetPos, core.transform.position) > 6f)
                //     core.ForceRepathNow();

                return;
            }

            // Lost target -> head to last known then Investigate
            core.SetFixedTarget(core.LastKnownTargetPos);
            core.SwitchState(EnemyState.Investigate);
        }
    }
}
