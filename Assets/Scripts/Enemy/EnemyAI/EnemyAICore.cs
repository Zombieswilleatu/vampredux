// -----------------------------
// File: EnemyAICore.Core.cs
// PATCHED: Added diagnostic logging for SearchMemory initialization
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

        [Header("Pathfinding Prewarm")]
        [Tooltip("Prewarm the coarse caches around start/target when the AI spawns.")]
        public bool prewarmOnSpawn = true;
        [Tooltip("Periodically prewarm when our target is far away (before asking for a long path).")]
        public bool prewarmOnRetarget = true;
        [Tooltip("Minimum distance to target before we attempt a prewarm.")]
        public float prewarmMinDistance = 10f;
        [Tooltip("Coarse-cell radius for prewarm touches (tiny costs).")]
        [Range(1, 6)] public int prewarmRadiusCells = 2;
        [Tooltip("Cooldown between opportunistic prewarms (seconds).")]
        public float prewarmCooldown = 0.5f;
        [Tooltip("Used only if we can't infer a radius from colliders/other partials.")]
        public float fallbackClearanceRadius = 0.35f;

        private float _nextPrewarmTime = 0f;
        private Vector3 _lastPrewarmForDest = new Vector3(9999, 9999, 9999);
        private float _cachedClearance = -1f;

        private static int _budgetFrame = -1;
        private static int _budgetLeft = 0;
        internal static readonly RaycastHit2D[] _rayBuf = new RaycastHit2D[1];

        private static System.Func<int> _instanceBudgetHook;
        public static void SetSearchGlobalBudgetProvider(System.Func<int> provider) => _instanceBudgetHook = provider;

        public static bool TryConsumeBudget(int want, out int granted)
        {
            granted = 0;
            if (PathRequestManager.TryConsumeSearchMarks(want, out granted))
                return granted > 0;

            if (_budgetFrame != Time.frameCount)
            {
                _budgetFrame = Time.frameCount;
                _budgetLeft = Mathf.Max(0, _instanceBudgetHook?.Invoke() ?? 2000);
            }
            granted = Mathf.Min(_budgetLeft, want);
            _budgetLeft -= granted;
            return granted > 0;
        }

        public static bool TryConsumeBudget(int want) => TryConsumeBudget(want, out _);

        [Header("Safety")]
        public float noPathWatchdogSeconds = 1.5f;

        [Header("Debug")]
        public bool showDebugInfo = false;
        public bool showDebugLogs = false;
        public float currentActualSpeed = 0f;
        public string currentStatus = "";
        public int enemyID = -1;
        public static int totalEnemiesInitialized = 0;
        public static int totalEnemiesActive = 0;

        private Pathfinding pathfinder;
        private Grid grid;
        private Rigidbody2D rb;

        private AreaChunker2D _chunker;
        private SearchMemory _searchMemory;
        internal SearchMemory SearchMem => _searchMemory;
        internal void InitSearchMemory(SearchMemory mem) => _searchMemory = mem;

        private List<Vector3> path;
        private int targetIndex = 0;
        private Vector3 currentTarget;
        private bool hasValidPath = false;
        private float actualPathUpdateInterval;
        private float nextPathRequestTime = 0f;

        private Vector2 currentVelocity;
        private Vector2 desiredVelocity;
        private bool isKnockedBack = false;

        private Vector3 stuckCheckPosition;
        private float stuckTimer = 0f;
        private int stuckRecoveryAttempts = 0;
        private bool isRecovering = false;
        private Vector2 recoveryDirection;
        private float recoveryTimer = 0f;

        private int recoveryBounces = 0;
        private float totalRecoveryClock = 0f;

        private Vector3 lastPosition;
        private Vector3 lastFramePosition;
        private float timeSinceLastMove;
        private float noPathTimer = 0f;
        private bool requestLoopRunning = false;

        private float retargetSearchTimer = 0f;
        [SerializeField] private Vector3 lastKnownTargetPos;
        private float lastRepathTime = 0f;

        public Vector3 LastKnownTargetPos
        {
            get => lastKnownTargetPos;
            set => lastKnownTargetPos = value;
        }

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

            pathfinder = Object.FindObjectOfType<Pathfinding>(true);
            grid = Object.FindObjectOfType<Grid>(true);
            if (pathfinder == null || grid == null)
            { Debug.LogError($"<color=red>[AI-{enemyID}]</color> Missing Pathfinding or Grid in scene! Pathfinder: {pathfinder}, Grid: {grid}"); enabled = false; return; }

            // ═══════════════════════════════════════════════════════════════════
            // DIAGNOSTIC LOGGING: SearchMemory Initialization
            // ═══════════════════════════════════════════════════════════════════

            Debug.Log($"<color=cyan>[AI-{enemyID}] ━━━ SearchMemory Init START ━━━</color>");

            _chunker = GetComponent<AreaChunker2D>()
                   ?? GetComponentInParent<AreaChunker2D>()
                   ?? Object.FindObjectOfType<AreaChunker2D>(true);

            Debug.Log($"[AI-{enemyID}] BEFORE init: _chunker={(_chunker != null ? "✓ OK" : "✗ NULL")} grid={(grid != null ? "✓ OK" : "✗ NULL")}");

            if (_chunker == null)
            {
                Debug.LogError($"<color=red>[AI-{enemyID}]</color> No AreaChunker2D found; Search features will be disabled.");
            }
            else
            {
                // CRITICAL: Force AreaChunker to build BEFORE creating SearchMemory
                Debug.Log($"[AI-{enemyID}] Ensuring AreaChunker is built...");
                _chunker.EnsureBuilt();
                Debug.Log($"[AI-{enemyID}] AreaChunker ready: {_chunker.AreaCount} areas, {_chunker.WalkableCount} walkable cells");

                try
                {
                    Debug.Log($"[AI-{enemyID}] Creating SearchMemory...");
                    _searchMemory = new SearchMemory(this, grid, _chunker);
                    Debug.Log($"<color=green>[AI-{enemyID}] ✓ SearchMemory created successfully</color>");

                    InitSearchMemory(_searchMemory);
                    Debug.Log($"<color=green>[AI-{enemyID}] ✓ SearchMemory initialized successfully</color>");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"<color=red>[AI-{enemyID}] ✗ SearchMemory init FAILED: {ex.Message}</color>\n{ex.StackTrace}");
                    _searchMemory = null;
                }
            }

            Debug.Log($"[AI-{enemyID}] AFTER init: _searchMemory={(_searchMemory != null ? "✓ OK" : "✗ NULL")}");
            Debug.Log($"<color=cyan>[AI-{enemyID}] ━━━ SearchMemory Init END ━━━</color>");

            // ═══════════════════════════════════════════════════════════════════

            if (recoveryBlockers.value == 0) recoveryBlockers = LayerMask.GetMask("Wall", "Obstacle");

            InitializeTarget();
            lastPosition = transform.position; stuckCheckPosition = transform.position; lastFramePosition = transform.position;
            lastKnownTargetPos = currentTarget; lastRepathTime = Time.time;

            totalEnemiesInitialized++; totalEnemiesActive++;

            if (prewarmOnSpawn)
            {
                float clr = GetClearanceRadius();
                pathfinder.PrewarmCoarseBudgeted(transform.position, clr, prewarmRadiusCells);
                pathfinder.PrewarmCoarseBudgeted(currentTarget, clr, prewarmRadiusCells);
                _lastPrewarmForDest = currentTarget;
                _nextPrewarmTime = Time.time + prewarmCooldown;
            }

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

            if (prewarmOnRetarget) TryPrewarmForCurrentTarget();

            if (enableStuckDetection && !isRecovering) CheckIfStuck();

            if (!isRecovering)
            {
                CalculateDesiredVelocity();
                IdleMicroTick(Time.deltaTime);
            }

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

        public void ApplyKnockback(Vector2 force)
        {
            if (!canBeKnockedBack) return;
            Vector2 actualForce = force * (1f - knockbackResistance);
            rb.velocity += actualForce; StartCoroutine(KnockbackRecovery());
        }
        IEnumerator KnockbackRecovery() { isKnockedBack = true; yield return new WaitForSeconds(knockbackRecoveryTime); isKnockedBack = false; }

        private float GetClearanceRadius()
        {
            if (_cachedClearance > 0f) return _cachedClearance;

            float r = fallbackClearanceRadius;

            if (TryGetComponent<CircleCollider2D>(out var cc))
                r = Mathf.Max(r, cc.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y));
            else if (TryGetComponent<CapsuleCollider2D>(out var cap))
                r = Mathf.Max(r, Mathf.Max(cap.size.x, cap.size.y) * 0.5f * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y));
            else if (TryGetComponent<BoxCollider2D>(out var bc))
                r = Mathf.Max(r, Mathf.Max(bc.size.x, bc.size.y) * 0.5f * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y));

            _cachedClearance = r;
            return r;
        }

        private void TryPrewarmForCurrentTarget()
        {
            if (pathfinder == null) return;

            float now = Time.time;
            if (now < _nextPrewarmTime) return;

            Vector3 dest = currentTarget;
            float dist = Vector3.Distance(transform.position, dest);
            if (dist < prewarmMinDistance) return;

            if (Vector3.SqrMagnitude(dest - _lastPrewarmForDest) < 1.0f) return;

            float clr = GetClearanceRadius();
            pathfinder.PrewarmCoarseBudgeted(transform.position, clr, prewarmRadiusCells);
            pathfinder.PrewarmCoarseBudgeted(dest, clr, prewarmRadiusCells);

            _lastPrewarmForDest = dest;
            _nextPrewarmTime = now + prewarmCooldown;
        }
    }
}