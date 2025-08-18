// -----------------------------
// File: States/InvestigateState.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI.States
{
    public class InvestigateState : IEnemyState
    {
        public string DebugName => "Investigate";

        private float _hold;

        public void OnEnter(EnemyAICore core)
        {
            if (core.showDebugLogs) Debug.Log("[AI] Enter Investigate");
            _hold = core.investigateHoldSeconds;

            // head to last known / investigate point
            if (core.targetTransform != null)
            {
                core.investigatePoint = core.targetTransform.position;
            }
            else
            {
                core.investigatePoint = core.LastKnownTargetPos;
            }

            core.SetFixedTarget(core.investigatePoint);
            core.ForceRepathNow();
        }

        public void OnExit(EnemyAICore core) { }

        public void Tick(EnemyAICore core, float dt)
        {
            // If we reached the spot, wait a beat then go to Search
            if (core.Reached(core.investigatePoint, core.waypointTolerance))
            {
                _hold -= dt;
                if (_hold <= 0f)
                {
                    core.SwitchState(EnemyState.Search);
                }
            }

            // If target is reacquired, chase
            if (core.targetTransform != null)
            {
                core.SwitchState(EnemyState.Chase);
            }
        }
    }
}
