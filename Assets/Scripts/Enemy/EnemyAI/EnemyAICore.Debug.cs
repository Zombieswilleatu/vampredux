// -----------------------------
// File: EnemyAICore.Debug.cs (fixed)
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        // Centralized status string updater used from Update()
        void UpdateDebugStatus()
        {
            // Optional: warn if someone tweaked speed at runtime
            if (Mathf.Abs(moveSpeed - originalMoveSpeed) > 0.1f)
            {
                Debug.LogWarning($"<color=red>[AI-{enemyID}]</color> Speed changed from {originalMoveSpeed} to {moveSpeed}!");
            }

            if (isKnockedBack)
            {
                currentStatus = "KNOCKBACK";
                return;
            }

            if (isRecovering)
            {
                currentStatus = "RECOVERING";
                return;
            }

            if (stuckTimer > 0f)
            {
                currentStatus = $"STUCK ({stuckTimer:F1}s)";
                return;
            }

            if (path == null || path.Count == 0)
            {
                currentStatus = "NO PATH";
                return;
            }

            if (targetIndex >= path.Count)
            {
                currentStatus = "PATH END";
                return;
            }

            currentStatus = $"FOLLOWING (node {targetIndex}/{path.Count})";
        }
    }
}
