// Stateless interface for AI states
using UnityEngine;

namespace EnemyAI
{
    public interface IAIState
    {
        EnemyAIStateId Id { get; }
        void OnEnter(EnemyAICore ai);
        void Tick(EnemyAICore ai, float dt);
        void OnExit(EnemyAICore ai);
    }

    public enum EnemyAIStateId
    {
        Idle,
        Patrol,
        Chase,
        Investigate
    }
}
