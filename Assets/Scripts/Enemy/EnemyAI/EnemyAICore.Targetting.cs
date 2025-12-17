// -----------------------------
// File: EnemyAICore.Targeting.cs
// Purpose: Stable target commitments; no short-hop veto.
// Notes:
//   • We keep debounce+hold to prevent target flapping.
//   • SetFixedTarget never rejects near-feet goals — pathing layer now enforces
//     multi-node follow and repath locks, so movement looks human.
// -----------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore
    {
        public Vector3 CurrentFixedTarget => currentTarget;

        [Header("Target Commit Controls")]
        [SerializeField] private float minTargetCommitDelta = 0.50f;
        [SerializeField] private float minTargetHoldSeconds = 0.75f;

        private float _targetHoldUntil = -1f;
        private Vector3 _lastCommittedTarget;

        void InitializeTarget()
        {
            switch (targetMode)
            {
                case TargetMode.MoveRight:
                    currentTarget = transform.position + Vector3.right * rightwardDistance;
                    break;

                case TargetMode.FollowTarget:
                    if (targetTransform != null)
                        currentTarget = targetTransform.position;
                    else if (targetGameObject != null)
                    {
                        targetTransform = targetGameObject.transform;
                        currentTarget = targetTransform.position;
                    }
                    else
                    {
                        targetMode = TargetMode.MoveRight;
                        currentTarget = transform.position + Vector3.right * rightwardDistance;
                    }
                    break;

                case TargetMode.FixedPosition:
                    currentTarget = targetPosition;
                    break;

                case TargetMode.FindByTag:
                    GameObject foundTarget = GameObject.FindGameObjectWithTag(targetTag);
                    if (foundTarget != null)
                    {
                        targetTransform = foundTarget.transform;
                        currentTarget = targetTransform.position;
                        targetMode = TargetMode.FollowTarget;
                    }
                    else
                    {
                        targetMode = TargetMode.MoveRight;
                        currentTarget = transform.position + Vector3.right * rightwardDistance;
                    }
                    break;

                case TargetMode.Manual:
                    if (currentTarget == Vector3.zero)
                        currentTarget = transform.position + Vector3.right * rightwardDistance;
                    break;
            }
        }

        void UpdateTarget()
        {
            if (CurrentStateId != EnemyState.Chase)
            {
                if (targetMode == TargetMode.FixedPosition || targetMode == TargetMode.Manual)
                    return;
                return;
            }

            if (retargetIfLost && (targetMode == TargetMode.FollowTarget || targetMode == TargetMode.FindByTag))
            {
                if (targetTransform == null)
                {
                    retargetSearchTimer += Time.deltaTime;
                    if (retargetSearchTimer >= retargetSearchInterval)
                    {
                        retargetSearchTimer = 0f;
                        var reacquire = GameObject.FindGameObjectWithTag(targetTag);
                        if (reacquire != null)
                        {
                            targetTransform = reacquire.transform;
                            if (showDebugLogs) Debug.Log($"[AI-{enemyID}] Reacquired target by tag '{targetTag}'.");
                        }
                    }
                }
            }

            switch (targetMode)
            {
                case TargetMode.FollowTarget:
                    if (targetTransform != null && updateTargetDynamically)
                        currentTarget = targetTransform.position;
                    break;

                case TargetMode.FixedPosition:
                    currentTarget = targetPosition;
                    break;

                case TargetMode.FindByTag:
                    if (targetTransform != null)
                        currentTarget = targetTransform.position;
                    break;
            }
        }

        // ---------- Public Target API ----------
        public void SetTarget(Vector3 position)
        {
            currentTarget = position;
            targetMode = TargetMode.Manual;
            lastKnownTargetPos = currentTarget;
            RequestPath();
        }

        public void SetTarget(Transform target)
        {
            targetTransform = target;
            targetMode = TargetMode.FollowTarget;
            if (target != null)
            {
                currentTarget = target.position;
                lastKnownTargetPos = currentTarget;
            }
            RequestPath();
        }

        public void SetFixedTarget(Vector3 position)
        {
            // No short-hop rejection: let pathing handle multi-node follow.
            bool changedEnough = (position - _lastCommittedTarget).sqrMagnitude >= (minTargetCommitDelta * minTargetCommitDelta);
            bool holdExpired = Time.time >= _targetHoldUntil;

            if (!changedEnough && !holdExpired)
                return;

            targetPosition = position;
            currentTarget = position;
            targetMode = TargetMode.FixedPosition;
            lastKnownTargetPos = currentTarget;

            _lastCommittedTarget = position;
            _targetHoldUntil = Time.time + minTargetHoldSeconds;

            RequestPath();
        }

        void FindAlternativeTarget()
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 offset = Quaternion.Euler(0, 0, angle) * Vector3.right * stuckRecoveryRadius;
                Vector3 newTarget = currentTarget + offset;
                Node node = grid.NodeFromWorldPoint(newTarget);
                if (node != null && node.walkable)
                {
                    if (showDebugLogs) Debug.Log($"<color=green>[AI]</color> {gameObject.name} found alternative target");
                    SetTarget(node.worldPosition);
                    return;
                }
            }
            Vector3 randomTarget = transform.position + (Vector3)Random.insideUnitCircle.normalized * stuckRecoveryRadius * 2f;
            SetTarget(randomTarget);
        }
    }
}
