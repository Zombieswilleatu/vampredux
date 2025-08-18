// -----------------------------
// File: EnemyAICore.Core.cs
// -----------------------------
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace EnemyAI
{
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Movement")]
        public float moveSpeed = 5f;
        public float acceleration = 8f;
        private float originalMoveSpeed;

        [Header("Combat")]
        public bool canBeKnockedBack = true;
        public float knockbackResistance = 0f;
        public float knockbackRecoveryTime = 0.3f;

        [Header("Pathfinding")]
        public float pathUpdateInterval = 0.5f;
        public float nodeReachDistance = 0.1f;
        [Range(0f, 1f)] public float pathUpdateRandomization = 0.3f;
        public int minNodesRemainingForNewPath = 5;
        public bool enableDynamicThrottling = true;

        [Header("Targeting")]
        public TargetMode targetMode = TargetMode.MoveRight;
        public Transform targetTransform;
        public Vector3 targetPosition;
        public float rightwardDistance = 25f;
        public GameObject targetGameObject;
        public string targetTag = "Player";
        public bool updateTargetDynamically = true;

        public enum TargetMode { MoveRight, FollowTarget, FixedPosition, FindByTag, Manual }

        [Header("Retargeting")]
        public bool retargetIfLost = true;
        public float retargetSearchInterval = 1.0f;
        public float targetMoveRepathThreshold = 0.25f;
        public float maxRepathIntervalWhileFollowing = 0.75f;

        [Header("Avoidance")]
        public float avoidanceRadius = 0.5f;
        public float avoidanceStrength = 0.3f;
        public bool prioritizeAvoidance = true;

        [Header("Path Smoothing")]
        public bool usePathSmoothing = true;
        public int smoothingIterations = 2;

        [Header("Stuck Detection")]
        public bool enableStuckDetection = true;
        public float stuckDetectionTime = 2f;
        public float minProgressDistance = 0.5f;
        public float stuckRecoveryRadius = 3f;
        public int maxStuckRecoveryAttempts = 3;
        public float recoveryDuration = 0.5f;
        public float recoveryForce = 5f;

        [Header("Recovery Movement")]
        public float recoverySpeed = 4f;
        public int recoveryMaxBounces = 3;
        public LayerMask recoveryBlockers;
        public float maxTotalRecoveryTime = 3.0f;

        [Header("Performance")]
        public bool staggerPathRequests = true;
        public bool skipPathIfStationary = true;
        public float stationaryThreshold = 0.1f;

        // --------------------------------------------------------------------
        // Search ray budget machinery (kept here). Do NOT re-declare public knobs;
        // they now live in EnemyAICore.SearchTuning.cs (partial).
        // --------------------------------------------------------------------
        private static int _budgetFrame = -1;
        private static int _budgetLeft = 0;
        private static readonly RaycastHit2D[] _rayBuf = new RaycastHit2D[1];

        private static bool TryConsumeBudget(int cost)
        {
            if (_budgetFrame != Time.frameCount)
            {
                _budgetFrame = Time.frameCount;
                _budgetLeft = Mathf.Max(0, _instanceBudgetHook?.Invoke() ?? 0);
                if (_budgetLeft == 0) _budgetLeft = 2000; // fallback if no provider
            }
            if (_budgetLeft < cost) return false;
            _budgetLeft -= cost;
            return true;
        }

        // optional: allow dynamic tuning from code (can return searchGlobalRayBudgetPerFrame)
        private static System.Func<int> _instanceBudgetHook;
        public static void SetSearchGlobalBudgetProvider(System.Func<int> provider) => _instanceBudgetHook = provider;

        // Uses fields defined in EnemyAICore.SearchTuning.cs (partial):
        // - enableSearchMarking
        // - searchRaysPerSweep
        // - visionBlockMask (declared elsewhere in your AI)
        public void MarkSearchedCone(Vector2 origin, Vector2 heading, float radius, float halfAngle)
        {
            if (!enableSearchMarking) return;

            int rays = Mathf.Max(1, searchRaysPerSweep);
            if (!TryConsumeBudget(rays)) return; // global cap reached

            Vector2 fwd = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector2.right;
            float halfRad = (halfAngle > Mathf.PI) ? halfAngle * Mathf.Deg2Rad : halfAngle;

            for (int i = 0; i < rays; i++)
            {
                float t = (rays == 1) ? 0f : (i / (float)(rays - 1)) * 2f - 1f; // [-1..1]
                float ang = t * halfRad;
                float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                Vector2 dir = new Vector2(ca * fwd.x - sa * fwd.y, sa * fwd.x + ca * fwd.y);

                int hitCount = Physics2D.RaycastNonAlloc(origin, dir, _rayBuf, radius, visionBlockMask);
                float dist = hitCount > 0 ? _rayBuf[0].distance : radius;

                // hook your coverage write here (origin -> origin + dir * dist)
                // e.g., Coverage.WriteRay(origin, origin + dir * dist);
            }
        }

        [Header("Safety")]
        public float noPathWatchdogSeconds = 1.5f;

        // NOTE: Search Marking public fields (enableSearchMarking, searchMarkInterval, searchMarkSegments)
        // and Search Perf public fields (searchRaysPerSweep, searchGlobalRayBudgetPerFrame)
        // have been MOVED to EnemyAICore.SearchTuning.cs. Do not declare them here.

        [Header("Debug")]
        public bool showDebugInfo = false;
        public bool showDebugLogs = false;
        public float currentActualSpeed = 0f;
        public string currentStatus = "";
        public int enemyID = -1;
        public static int totalEnemiesInitialized = 0;
        public static int totalEnemiesActive = 0;

        // Engine & graph refs
        private Pathfinding pathfinder;
        private Grid grid;
        private Rigidbody2D rb;

        // Path state
        private List<Vector3> path;
        private int targetIndex = 0;
        private Vector3 currentTarget;
        private bool hasValidPath = false;
        private float actualPathUpdateInterval;
        private float nextPathRequestTime = 0f;
        private int lastQueueSize = 0;

        // Movement state
        private Vector2 currentVelocity;
        private Vector2 desiredVelocity;
        private bool isKnockedBack = false;

        // Stuck detection
        private Vector3 stuckCheckPosition;
        private float stuckTimer = 0f;
        private int stuckRecoveryAttempts = 0;
        private bool isRecovering = false;
        private Vector2 recoveryDirection;
        private float recoveryTimer = 0f;

        // Recovery bookkeeping
        private int recoveryBounces = 0;
        private float totalRecoveryClock = 0f;

        // Loop control & books
        private Vector3 lastPosition;
        private Vector3 lastFramePosition;
        private float timeSinceLastMove;
        private float noPathTimer = 0f;
        private bool requestLoopRunning = false;

        // Retargeting bookkeeping
        private float retargetSearchTimer = 0f;
        [SerializeField] private Vector3 lastKnownTargetPos;
        private float lastRepathTime = 0f;

        // expose for states
        public Vector3 LastKnownTargetPos
        {
            get => lastKnownTargetPos;
            set => lastKnownTargetPos = value;
        }

        // ---------- LIFECYCLE ----------
        void Awake()
        {
            if (!gameObject.activeInHierarchy)
                Debug.LogWarning($"<color=orange>[AI]</color> {gameObject.name} is not active in hierarchy!");
            if (!enabled)
                Debug.LogWarning($"<color=orange>[AI]</color> SimpleEnemyAI component is disabled on {gameObject.name}!");
        }

        void OnEnable()
        {
            if (Application.isPlaying && !requestLoopRunning)
            {
                if (staggerPathRequests)
                {
                    actualPathUpdateInterval = pathUpdateInterval * Random.Range(1f - pathUpdateRandomization, 1f + pathUpdateRandomization);
                    float maxInitialDelay = pathUpdateInterval;
                    if (GameObject.FindObjectsOfType<EnemyAICore>().Length > 30) maxInitialDelay = pathUpdateInterval * 2f;
                    float initialDelay = Random.Range(0f, maxInitialDelay);
                    StartCoroutine(RequestPathRepeatedly(initialDelay));
                }
                else
                {
                    actualPathUpdateInterval = pathUpdateInterval;
                    StartCoroutine(RequestPathRepeatedly(0f));
                }
                requestLoopRunning = true;
            }
        }

        void OnDisable()
        {
            StopAllCoroutines();
            requestLoopRunning = false;
        }

        void Start()
        {
            enemyID = Random.Range(1000, 9999);
            originalMoveSpeed = moveSpeed;

            rb = GetComponent<Rigidbody2D>();
            if (rb == null) { Debug.LogError($"<color=red>[AI-{enemyID}]</color> No Rigidbody2D found on {gameObject.name}!"); enabled = false; return; }
            rb.gravityScale = 0f; rb.freezeRotation = true; rb.interpolation = RigidbodyInterpolation2D.Interpolate; rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous; rb.drag = 1f; rb.mass = 1f;

            // includeInactive: true (safer when objects are disabled at start)
            pathfinder = Object.FindObjectOfType<Pathfinding>(true);
            grid = Object.FindObjectOfType<Grid>(true);
            if (pathfinder == null || grid == null)
            { Debug.LogError($"<color=red>[AI-{enemyID}]</color> Missing Pathfinding or Grid in scene! Pathfinder: {pathfinder}, Grid: {grid}"); enabled = false; return; }

            if (recoveryBlockers.value == 0) recoveryBlockers = LayerMask.GetMask("Wall", "Obstacle");

            InitializeTarget();
            lastPosition = transform.position; stuckCheckPosition = transform.position; lastFramePosition = transform.position;
            lastKnownTargetPos = currentTarget; lastRepathTime = Time.time;

            totalEnemiesInitialized++; totalEnemiesActive++;

            if (!requestLoopRunning)
            {
                if (staggerPathRequests)
                {
                    actualPathUpdateInterval = pathUpdateInterval * Random.Range(1f - pathUpdateRandomization, 1f + pathUpdateRandomization);
                    float maxInitialDelay = pathUpdateInterval; if (GameObject.FindObjectsOfType<EnemyAICore>().Length > 30) maxInitialDelay = pathUpdateInterval * 2f;
                    float initialDelay = Random.Range(0f, maxInitialDelay);
                    StartCoroutine(RequestPathRepeatedly(initialDelay));
                }
                else { actualPathUpdateInterval = pathUpdateInterval; StartCoroutine(RequestPathRepeatedly(0f)); }
                requestLoopRunning = true;
            }

            // start state machine AFTER init
            StartStateMachine();
        }

        void OnDestroy() { totalEnemiesActive--; }

        void Update()
        {
            if (showDebugInfo)
            {
                float distanceMoved = Vector3.Distance(transform.position, lastFramePosition);
                currentActualSpeed = distanceMoved / Time.deltaTime;
                lastFramePosition = transform.position; UpdateDebugStatus();
            }

            if (path == null || path.Count == 0)
            {
                noPathTimer += Time.deltaTime;
                if (noPathTimer >= noPathWatchdogSeconds)
                { if (showDebugLogs) Debug.Log($"[AI-{enemyID}] Watchdog forcing RequestPath()"); noPathTimer = 0f; nextPathRequestTime = Time.time; RequestPath(); }
            }
            else noPathTimer = 0f;

            if (isKnockedBack) return;

            UpdateTarget();
            if (enableStuckDetection && !isRecovering) CheckIfStuck();

            if (!isRecovering)
            {
                CalculateDesiredVelocity();
                // Gentle idle micro-movement lives in EnemyAICore.IdleMicro.cs
                IdleMicroTick(Time.deltaTime);
            }

            // tick state machine
            TickState(Time.deltaTime);
        }

        void FixedUpdate()
        {
            if (isKnockedBack) return;
            if (isRecovering) { RecoveryStep(); return; }

            currentVelocity = Vector2.Lerp(currentVelocity, desiredVelocity, Time.fixedDeltaTime * acceleration);
            rb.velocity = currentVelocity;
            if (rb.velocity.magnitude > moveSpeed * 1.5f)
                rb.velocity = rb.velocity.normalized * (moveSpeed * 1.5f);
        }

        // ---------- COMBAT ----------
        public void ApplyKnockback(Vector2 force)
        {
            if (!canBeKnockedBack) return;
            Vector2 actualForce = force * (1f - knockbackResistance);
            rb.velocity += actualForce; StartCoroutine(KnockbackRecovery());
        }
        IEnumerator KnockbackRecovery() { isKnockedBack = true; yield return new WaitForSeconds(knockbackRecoveryTime); isKnockedBack = false; }
    }
}
