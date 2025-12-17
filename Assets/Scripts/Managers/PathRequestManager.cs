using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using EnemyAI;

public class PathRequestManager : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    // Throughput (independent of FPS)
    // ─────────────────────────────────────────────────────────────
    [Header("Path Requests • Throughput")]
    [Tooltip("Target number of path requests to process per second (overall).")]
    public int targetRequestsPerSecond = 60;

    [Tooltip("Max requests processed in a single frame to avoid spikes.")]
    public int maxBurstPerFrame = 2;

    [Header("Path Requests • Time Budget")]
    [Tooltip("Max milliseconds spent pathfinding per frame.")]
    public float maxPathfindingTimePerFrame = 1.0f;

    [Header("Queues / Debug")]
    [Tooltip("Warn when total pending (mailboxes + anon) exceeds this size.")]
    public int warnQueueSize = 200;

    [Tooltip("Log a summary every N frames (0 = off).")]
    public int logEveryNFrames = 60;

    [Header("Processing Mode")]
    [Tooltip("Run the processing in a coroutine (per frame) instead of Update().")]
    public bool useAsyncProcessing = true;

    [Header("Emergency Performance")]
    [Tooltip("Skip pathfinding when physics overlap calls exceed this per frame.")]
    public int maxPhysicsCallsPerFrame = 5000;

    [Tooltip("Spread pathfinding across multiple frames when under load.")]
    public bool useFrameDistribution = true;

    [Obsolete("Use targetRequestsPerSecond + maxBurstPerFrame instead")]
    public int maxRequestsPerFrame = 1;

    // ─────────────────────────────────────────────────────────────
    // Search Memory (marking) GLOBAL budget (per frame)
    // ─────────────────────────────────────────────────────────────
    [Header("Search Memory • Global Budget")]
    [Tooltip("Total search-mark operations allowed across ALL enemies each frame.")]
    public int searchMarksPerFrame = 3000;

    [Tooltip("Max burst allowed to a single call (prevents one enemy from spiking a frame).")]
    public int searchMarksBurst = 600;

    private static int _marksLeft;
    private static int _markBudgetFrame = -1;

    public static bool TryConsumeSearchMarks(int want, out int granted)
    {
        // lazy frame reset (safe even if Instance is null during domain reload)
        if (_markBudgetFrame != Time.frameCount)
        {
            int defaultBudget = 2000;
            var inst = Instance;
            _marksLeft = Mathf.Max(0, inst ? inst.searchMarksPerFrame : defaultBudget);
            _markBudgetFrame = Time.frameCount;
        }

        granted = Mathf.Min(want, _marksLeft);
        if (Instance) granted = Mathf.Min(granted, Instance.searchMarksBurst);
        if (granted <= 0) return false;

        _marksLeft -= granted;
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // Internal path queues
    // ─────────────────────────────────────────────────────────────
    private readonly Queue<PathRequest> anonQueue = new Queue<PathRequest>();
    private readonly Dictionary<int, PathRequest> pendingByRequester = new Dictionary<int, PathRequest>();
    private readonly Queue<int> requesterOrder = new Queue<int>();

    private Pathfinding pathfinding;
    private float tokenBucket = 0f; // tokens available this frame
    private int pathsProcessedThisWindow = 0;

    // Physics-call tracking (rough back-pressure)
    private static int physicsCallsThisFrame = 0;
    private static int lastPhysicsFrame = -1;

    // Work spreading
    private int frameDistributionOffset = 0;
    private static int globalFrameDistributor = 0;

    public static PathRequestManager Instance { get; private set; }

    // ─────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────
    void Awake()
    {
        Instance = this;
        pathfinding = FindObjectOfType<Pathfinding>();
        if (pathfinding == null)
            Debug.LogError("No Pathfinding component found in scene!");

        // give the bucket an initial small burst
        tokenBucket = Mathf.Min(maxBurstPerFrame, Mathf.Max(1, targetRequestsPerSecond) * 0.25f);

        // Assign frame distribution offset (0..3)
        frameDistributionOffset = (globalFrameDistributor++) % 4;
    }

    void OnEnable()
    {
        Instance = this;
        if (useAsyncProcessing)
            StartCoroutine(ProcessPathRequestsAsync());
    }

    void OnDisable()
    {
        StopAllCoroutines();
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        // Reset physics call counter each frame
        if (Time.frameCount != lastPhysicsFrame)
        {
            lastPhysicsFrame = Time.frameCount;
            physicsCallsThisFrame = 0;
        }

        if (!useAsyncProcessing)
            ProcessBudgeted();

        if (logEveryNFrames > 0 && Time.frameCount % logEveryNFrames == 0 &&
            (pathsProcessedThisWindow > 0 || TotalPending() > 0))
        {
            Debug.Log($"<color=blue>[PathRequestManager]</color> " +
                      $"Processed {pathsProcessedThisWindow} paths in last {logEveryNFrames} frames | " +
                      $"pending={TotalPending()} (mailboxes={pendingByRequester.Count}, anon={anonQueue.Count})");
            pathsProcessedThisWindow = 0;
        }
    }

    IEnumerator ProcessPathRequestsAsync()
    {
        while (true)
        {
            ProcessBudgeted();
            yield return null; // once per frame
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────
    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback)
    {
        RequestPath(start, end, clearanceRadius, callback, requesterId: -1);
    }

    // Overload: pass your enemyID (or instanceID) here to enable coalescing
    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback, int requesterId)
    {
        var req = new PathRequest(start, end, clearanceRadius, callback, requesterId);

        if (requesterId >= 0)
        {
            if (pendingByRequester.ContainsKey(requesterId))
            {
                // coalesce to latest (drop earlier one)
                pendingByRequester[requesterId] = req;
            }
            else
            {
                pendingByRequester.Add(requesterId, req);
                requesterOrder.Enqueue(requesterId);
            }
        }
        else
        {
            anonQueue.Enqueue(req);
        }

        int pending = TotalPending();
        if (pending > warnQueueSize)
            Debug.LogWarning($"<color=orange>[PathRequestManager]</color> Queue size: {pending}");
    }

    public int GetQueueLength() => TotalPending();

    public void ClearQueue()
    {
        anonQueue.Clear();
        pendingByRequester.Clear();
        requesterOrder.Clear();
    }

    // ─────────────────────────────────────────────────────────────
    // Core processing
    // ─────────────────────────────────────────────────────────────
    private void ProcessBudgeted()
    {
        // Optionally only do work 1/4 of frames per manager instance to spread load
        if (useFrameDistribution)
        {
            bool isMyFrame = (Time.frameCount + frameDistributionOffset) % 4 == 0;
            if (!isMyFrame) return;
        }

        // Back off if physics overlaps/raycast budget looks scary this frame
        if (physicsCallsThisFrame > maxPhysicsCallsPerFrame)
            return;

        // Token bucket regen (frame-rate independent)
        float regen = Mathf.Max(1, targetRequestsPerSecond) * Time.unscaledDeltaTime;
        tokenBucket = Mathf.Min(tokenBucket + regen, Mathf.Max(maxBurstPerFrame, 1));

        if (TotalPending() == 0 || tokenBucket < 1f)
            return;

        float frameStartMs = Time.realtimeSinceStartup * 1000f;
        int processedThisFrame = 0;
        int physicsCallsAtStart = physicsCallsThisFrame;

        while (tokenBucket >= 1f)
        {
            float elapsedMs = (Time.realtimeSinceStartup * 1000f) - frameStartMs;
            if (elapsedMs > maxPathfindingTimePerFrame) break;
            if (processedThisFrame >= maxBurstPerFrame) break;
            if (TotalPending() == 0) break;

            // additional safety to not dominate a frame if other systems are hot
            if (physicsCallsThisFrame - physicsCallsAtStart > 1000) break;

            bool didWork = false;

            // 1) Per-requester mailbox (round-robin ids)
            if (pendingByRequester.Count > 0)
            {
                int id = requesterOrder.Dequeue();
                if (pendingByRequester.TryGetValue(id, out var req))
                {
                    pendingByRequester.Remove(id);
                    ExecutePath(req);
                    tokenBucket -= 1f;
                    processedThisFrame++;
                    pathsProcessedThisWindow++;
                    didWork = true;
                }
            }
            // 2) Anonymous FIFO
            else if (anonQueue.Count > 0)
            {
                var req = anonQueue.Dequeue();
                ExecutePath(req);
                tokenBucket -= 1f;
                processedThisFrame++;
                pathsProcessedThisWindow++;
                didWork = true;
            }

            if (!didWork) break;
        }
    }

    private void ExecutePath(in PathRequest req)
    {
        if (pathfinding == null) return;

        // Tiny prewarm for long hops (reduces first hierarchical spike)
        TryPrewarmIfLong(req);

        int callsBefore = physicsCallsThisFrame;
        pathfinding.FindPath(req.start, req.end, req.clearanceRadius, req.callback);
        physicsCallsThisFrame = callsBefore + EstimatePhysicsCalls();
    }

    // Safety-net prewarm so first hierarchical query doesn't spike as much
    private void TryPrewarmIfLong(in PathRequest req)
    {
        if (pathfinding == null) return;
        float thresh = pathfinding != null ? pathfinding.hierarchicalThreshold : 12f;
        if ((req.end - req.start).sqrMagnitude >= (thresh * thresh * 0.5f)) // slightly under threshold
        {
            // UPDATED: use budgeted version to avoid CS0619 + frame spikes
            pathfinding.PrewarmCoarseBudgeted(req.start, req.clearanceRadius, 2);
            pathfinding.PrewarmCoarseBudgeted(req.end, req.clearanceRadius, 2);
        }
    }

    // Rough estimate of physics calls per pathfinding operation
    private int EstimatePhysicsCalls() => 200;

    // Allow physics-heavy systems to report in (so we can back off)
    public static void ReportPhysicsCall()
    {
        if (Time.frameCount != lastPhysicsFrame)
        {
            lastPhysicsFrame = Time.frameCount;
            physicsCallsThisFrame = 0;
        }
        physicsCallsThisFrame++;
    }

    private int TotalPending() => pendingByRequester.Count + anonQueue.Count;

    // ─────────────────────────────────────────────────────────────
    // Types
    // ─────────────────────────────────────────────────────────────
    private struct PathRequest
    {
        public readonly Vector3 start;
        public readonly Vector3 end;
        public readonly float clearanceRadius;
        public readonly Action<List<Node>> callback;
        public readonly int requesterId;

        public PathRequest(Vector3 s, Vector3 e, float clearance, Action<List<Node>> cb, int requester)
        {
            start = s;
            end = e;
            clearanceRadius = clearance;
            callback = cb;
            requesterId = requester;
        }
    }
}
