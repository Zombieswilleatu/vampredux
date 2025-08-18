// -----------------------------
// File: EnemyAICore.Gizmos.cs
// -----------------------------
using UnityEngine;

#if UNITY_EDITOR
namespace EnemyAI
{
    public partial class EnemyAICore
    {
        void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;
            Color targetColor = Color.cyan; if (isRecovering) targetColor = Color.red; else if (stuckTimer > 0) targetColor = Color.yellow;
            Gizmos.color = targetColor; Gizmos.DrawWireSphere(currentTarget, 0.2f);
            if (path != null && path.Count > 0)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.5f);
                for (int i = 0; i < path.Count - 1; i++) Gizmos.DrawLine(path[i], path[i + 1]);
                if (targetIndex < path.Count) { Gizmos.color = Color.red; Gizmos.DrawWireSphere(path[targetIndex], 0.15f); }
            }
            Gizmos.color = new Color(1, 0, 0, 0.1f); Gizmos.DrawWireSphere(transform.position, isRecovering ? avoidanceRadius * 2f : avoidanceRadius);
            if (isRecovering) { Gizmos.color = Color.red; Gizmos.DrawRay(transform.position, (Vector3)recoveryDirection * 2f); }
        }
    }
}
#endif