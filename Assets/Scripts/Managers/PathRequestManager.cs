using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using EnemyAI;

public class PathRequestManager : MonoBehaviour
{
    [Header("Throughput (independent of FPS)")]
    [Tooltip("Target number of path requests to process per second (overall).")]
    public int targetRequestsPerSecond = 60; // Reduced from 120

    [Tooltip("Max requests processed in a single frame to avoid spikes.")]
    public int maxBurstPerFrame = 2; // Reduced from 6

    [Header("Time Budget")]
    [Tooltip("Max milliseconds spent pathfinding per frame.")]
    public float maxPathfindingTimePerFrame = 1.0f; // Reduced from 2.5ms

    [Header("Queue / Debug")]
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

    // Legacy (kept for compatibility; no longer used)
    [Obsolete("Use targetRequestsPerSecond + maxBurstPerFrame instead")]
    public int maxRequestsPerFrame = 1;

    // ---- Internal state ------------------------------------------------------

    // Anonymous FIFO (requests without a requesterId)
    private readonly Queue<PathRequest> anonQueue = new Queue<PathRequest>();

    // Per-agent mailbox: latest request per requesterId + order to process once
    private readonly Dictionary<int, PathRequest> pendingByRequester = new Dictionary<int, PathRequest>();
    private readonly Queue<int> requesterOrder = new Queue<int>();

    private Pathfinding pathfinding;
    private float tokenBucket = 0f; // tokens available this frame
    private int pathsProcessedThisWindow = 0;
    
    // Performance tracking
    private static int physicsCallsThisFrame = 0;
    private static int lastPhysicsFrame = -1;
    private int frameDistributionOffset = 0;
    private static int globalFrameDistributor = 0;

    public static PathRequestManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
        pathfinding = FindObjectOfType<Pathfinding>();
        if (pathfinding == null)
            Debug.LogError("No Pathfinding component found in scene!");

        tokenBucket = Mathf.Min(maxBurstPerFrame, Mathf.Max(1, targetRequestsPerSecond) * 0.25f);
        
        // Assign frame distribution offset
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

        if (logEveryNFrames > 0 && Time.frameCount % logEveryNFrames == 0 && (pathsProcessedThisWindow > 0 || TotalPending() > 0))
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

    // ------------------- Public API -------------------

    // Original signature (kept for existing callers)
    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback)
    {
        RequestPath(start, end, clearanceRadius, callback, requesterId: -1);
    }

    // New overload: pass your enemyID here to enable coalescing
    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback, int requesterId)
    {
        var req = new PathRequest(start, end, clearanceRadius, callback, requesterId);

        if (requesterId >= 0)
        {
            // Mailbox: if already pending, overwrite; otherwise add and enqueue id once.
            if (pendingByRequester.ContainsKey(requesterId))
            {
                pendingByRequester[requesterId] = req; // coalesce to latest
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

    // ------------------- Core processing -------------------

    private void ProcessBudgeted()
    {
        // Frame distribution - only process on designated frames when enabled
        if (useFrameDistribution)
        {
            bool isMyFrame = (Time.frameCount + frameDistributionOffset) % 4 == 0;
            if (!isMyFrame) return;
        }
        
        // Emergency brake - if physics system is overloaded, skip pathfinding
        if (physicsCallsThisFrame > maxPhysicsCallsPerFrame)
        {
            return;
        }

        // Refill tokens by real time (independent of FPS)
        float regen = Mathf.Max(1, targetRequestsPerSecond) * Time.unscaledDeltaTime;
        tokenBucket = Mathf.Min(tokenBucket + regen, Mathf.Max(maxBurstPerFrame, 1));

        if (TotalPending() == 0 || tokenBucket < 1f)
            return;

        float frameStartMs = Time.realtimeSinceStartup * 1000f;
        int processedThisFrame = 0;
        int physicsCallsAtStart = physicsCallsThisFrame;

        // Much more conservative processing
        while (tokenBucket >= 1f)
        {
            float elapsedMs = (Time.realtimeSinceStartup * 1000f) - frameStartMs;
            if (elapsedMs > maxPathfindingTimePerFrame) break;
            if (processedThisFrame >= maxBurstPerFrame) break;
            if (TotalPending() == 0) break;
            
            // Emergency brake during processing
            if (physicsCallsThisFrame - physicsCallsAtStart > 1000) break;

            bool didWork = false;

            // 1) Per-requester mailbox first (prevents a single spammy agent from flooding)
            if (pendingByRequester.Count > 0)
            {
                int id = requesterOrder.Dequeue();
                if (pendingByRequester.TryGetValue(id, out var req))
                {
                    pendingByRequester.Remove(id); // remove before processing to avoid duplicates
                    
                    // Track physics calls before pathfinding
                    int callsBefore = physicsCallsThisFrame;
                    pathfinding.FindPath(req.start, req.end, req.clearanceRadius, req.callback);
                    physicsCallsThisFrame = callsBefore + EstimatePhysicsCalls(); // Rough estimate

                    tokenBucket -= 1f;
                    processedThisFrame++;
                    pathsProcessedThisWindow++;
                    didWork = true;
                }
                // If the id had no pending (rare race), just continue
            }
            // 2) Anonymous FIFO
            else if (anonQueue.Count > 0)
            {
                var req = anonQueue.Dequeue();
                
                // Track physics calls before pathfinding
                int callsBefore = physicsCallsThisFrame;
                pathfinding.FindPath(req.start, req.end, req.clearanceRadius, req.callback);
                physicsCallsThisFrame = callsBefore + EstimatePhysicsCalls(); // Rough estimate

                tokenBucket -= 1f;
                processedThisFrame++;
                pathsProcessedThisWindow++;
                didWork = true;
            }

            if (!didWork) break; // safety
        }
    }

    // Rough estimate of physics calls per pathfinding operation
    // This is a heuristic - you may need to tune based on your pathfinding implementation
    private int EstimatePhysicsCalls()
    {
        return 200; // Conservative estimate - adjust based on your actual pathfinding complexity
    }

    // Method for pathfinding system to report actual physics usage
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

    // ------------------- Types -------------------

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