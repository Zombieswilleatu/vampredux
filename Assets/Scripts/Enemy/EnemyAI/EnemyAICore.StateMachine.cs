// -----------------------------
// File: EnemyAICore.StateMachine.cs (partial)
// -----------------------------
using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public enum GamePhase { Day, Night }

    public enum EnemyState
    {
        Idle,
        Patrol,
        Investigate,
        Chase,
        Search
    }

    public interface IEnemyState
    {
        void OnEnter(EnemyAICore core);
        void OnExit(EnemyAICore core);
        void Tick(EnemyAICore core, float dt);
        string DebugName { get; }
    }

    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Game/Phase")]
        [SerializeField] public GamePhase phase = GamePhase.Day;

        [Header("State Machine")]
        [SerializeField] private EnemyState initialState = EnemyState.Patrol;

        [Header("Patrol Settings")]
        [SerializeField] internal List<Transform> patrolPoints = new List<Transform>();
        [SerializeField] internal float waypointTolerance = 0.25f;

        [Header("Investigate Settings")]
        [SerializeField] internal float investigateHoldSeconds = 1.5f;

        public Vector3 investigatePoint;

        private IEnemyState _state;
        private EnemyState _stateId;
        public EnemyState CurrentStateId => _stateId;

        private float _stateLogTimer = 0f; // optional heartbeat logger

        private readonly Dictionary<EnemyState, IEnemyState> _states =
            new Dictionary<EnemyState, IEnemyState>();

        private void InitStateTable()
        {
            _states.Clear();
            _states[EnemyState.Idle] = new States.IdleState();
            _states[EnemyState.Patrol] = new States.PatrolState();
            _states[EnemyState.Investigate] = new States.InvestigateState();
            _states[EnemyState.Chase] = new States.ChaseState();
            _states[EnemyState.Search] = new States.SearchState();
        }

        private void StartStateMachine()
        {
            InitStateTable();
            _stateId = initialState;
            _state = _states[_stateId];
            _state.OnEnter(this);

            if (showDebugLogs) Debug.Log($"[AI-{enemyID}] Start in state: {_stateId}");
        }

        private void TickState(float dt)
        {
            if (_state != null) _state.Tick(this, dt);

            // optional: infrequent heartbeat to avoid console spam
            if (showDebugLogs)
            {
                _stateLogTimer -= dt;
                if (_stateLogTimer <= 0f)
                {
                    _stateLogTimer = 2.0f;
                    Debug.Log($"[AI-{enemyID}] State: {_stateId}");
                }
            }
        }

        public void SwitchState(EnemyState next)
        {
            if (_state != null) _state.OnExit(this);
            var prev = _stateId;
            _stateId = next;
            _state = _states[next];
            _state.OnEnter(this);

            if (showDebugLogs) Debug.Log($"[AI-{enemyID}] {prev} -> {next}");
        }

        public void ForceState(EnemyState s) => SwitchState(s);

        // --- helpers used by states ---
        public void ForceRepathNow()
        {
            nextPathRequestTime = Time.time;
            RequestPath();
        }

        public bool Reached(Vector3 pos, float tol)
        {
            return Vector2.Distance(transform.position, pos) <= Mathf.Max(0.01f, tol);
        }
    }
}
