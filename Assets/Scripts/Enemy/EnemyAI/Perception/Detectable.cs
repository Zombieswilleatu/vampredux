// -----------------------------
// File: EnemyAI/Perception/Detectable.cs
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public enum DetectableType { Unknown, Player, Enemy, Trap, Noise }

    /// <summary>Attach to anything the AI should notice.</summary>
    public sealed class Detectable : MonoBehaviour
    {
        public DetectableType type = DetectableType.Unknown;
    }
}
