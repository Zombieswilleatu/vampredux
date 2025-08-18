// -----------------------------
// File: EnemyAICore.Log.cs (partial)
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Debug")]
        public bool verboseSearchLogs = false; // extra per-candidate logs

        internal void LogAI(string msg)
        {
            if (showDebugLogs)
                Debug.Log($"[AI-{enemyID}] {msg}");
        }
    }
}
