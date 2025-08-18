// -----------------------------
// File: States/IdleState.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI.States
{
    public sealed class IdleState : IEnemyState
    {
        public string DebugName => "Idle";

        public void OnEnter(EnemyAICore core)
        {
            // Neutral target at current position so pathfinder has a valid goal.
            core.targetMode = EnemyAICore.TargetMode.Manual;
            core.SetFixedTarget(core.transform.position);

            // Ask for a path (will early-out safely until Grid/Pathfinding are ready).
            core.ForceRepathNow();
        }

        public void Tick(EnemyAICore core, float dt)
        {
            // Later: look for stimuli (sound/vision) and SwitchState when found.
        }

        public void OnExit(EnemyAICore core) { }
    }
}
