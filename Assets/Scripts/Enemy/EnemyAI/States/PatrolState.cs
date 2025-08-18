// -----------------------------
// File: States/PatrolState.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI.States
{
    public class PatrolState : IEnemyState
    {
        public string DebugName => "Patrol";

        private int _index = 0;

        public void OnEnter(EnemyAICore core)
        {
            if (core.showDebugLogs) Debug.Log("[AI] Enter Patrol");

            if (core.patrolPoints == null || core.patrolPoints.Count == 0)
            {
                // No patrol points: fall back to Idle
                core.SwitchState(EnemyState.Idle);
                return;
            }

            // Move to current patrol point
            _index = Mathf.Clamp(_index, 0, core.patrolPoints.Count - 1);
            core.SetFixedTarget(core.patrolPoints[_index].position);
            core.ForceRepathNow();
        }

        public void OnExit(EnemyAICore core) { }

        public void Tick(EnemyAICore core, float dt)
        {
            if (core.patrolPoints == null || core.patrolPoints.Count == 0)
            {
                core.SwitchState(EnemyState.Idle);
                return;
            }

            Vector3 wp = core.patrolPoints[_index].position;

            // Advance when reached
            if (core.Reached(wp, core.waypointTolerance))
            {
                _index = (_index + 1) % core.patrolPoints.Count;
                core.SetFixedTarget(core.patrolPoints[_index].position);
                core.ForceRepathNow();
            }

            // (Hook for detection would go here; if sees target -> core.SwitchState(EnemyState.Chase);)
        }
    }
}
