using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using EnemyAI;

public class PathRequestManager : MonoBehaviour
{
    [Header("Throughput (independent of FPS)")]
    public int targetRequestsPerSecond = 60;
    public int maxBurstPerFrame = 2;

    [Header("Time Budget")]
    public float maxPathfindingTimePerFrame = 1.0f;

    [Header("Queue / Debug")]
    public int warnQueueSize = 200;
    public int logEveryNFrames = 60;

    [Header("Processing Mode")]
    public bool useAsyncProcessing = true;

    [Header("Emergency Performance")]
    public int maxPhysicsCallsPerFrame = 5000;

    [Header("Fair Scheduling")]
    [Tooltip("Distribute work across this many per-frame slots. Higher = smoother.")]
    public int distributionSlots = 6;
    public bool prioritizeMailboxes = true;

    // ---- Internal state ------------------------------------------------------

    private readonly Dictionary<int, PathRequest> pendingByRequester = new Dictionary<int, PathRequest>();
    private readonly Dictionary<int, int> pendingSlotByRequester = new Dictionary<int, int>();
    private Queue<int>[] mailboxOrderBySlot;    // requester ids per slot
    private Queue<PathRequest>[] anonBySlot;    // anon requests per slot

    private Pathfinding pathfinding;
    private float tokenBucket = 0f;
    private int pathsProcessedThisWindow = 0;

    private static int physicsCallsThisFrame = 0;
    private static int lastPhysicsFrame = -1;

    private bool _initialized = false;

    public static PathRequestManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
        EnsureInit();
        // start with a small token reserve
        tokenBucket = Mathf.Min(maxBurstPerFrame, Mathf.Max(1, targetRequestsPerSecond) * 0.25f);
    }

    void OnEnable()
    {
        Instance = this;
        EnsureInit();
        if (useAsyncProcessing)
            StartCoroutine(ProcessPathRequestsAsync());
    }

    void OnDisable()
    {
        StopAllCoroutines();
    }

    void Update()
    {
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
                      $"pending={TotalPending()} (mailboxes={pendingByRequester.Count}, anon={TotalAnonCount()})");
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

    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback)
    {
        RequestPath(start, end, clearanceRadius, callback, requesterId: -1);
    }

    public void RequestPath(Vector3 start, Vector3 end, float clearanceRadius, Action<List<Node>> callback, int requesterId)
    {
        EnsureInit();

        var req = new PathRequest(start, end, clearanceRadius, callback, requesterId);
        int slots = Mathf.Max(1, distributionSlots);

        if (requesterId >= 0)
        {
            if (pendingByRequester.ContainsKey(requesterId))
            {
                pendingByRequester[requesterId] = req; // coalesce
            }
            else
            {
                pendingByRequester.Add(requesterId, req);
                int slot = Math.Abs(requesterId) % slots;
                pendingSlotByRequester[requesterId] = slot;
                mailboxOrderBySlot[slot].Enqueue(requesterId);
            }
        }
        else
        {
            int slot = HashToSlot(start, end, slots);
            anonBySlot[slot].Enqueue(req);
        }

        int pending = TotalPending();
        if (pending > warnQueueSize)
            Debug.LogWarning($"<color=orange>[PathRequestManager]</color> Queue size: {pending}");
    }

    public int GetQueueLength() => TotalPending();

    public void ClearQueue()
    {
        if (anonBySlot != null) foreach (var q in anonBySlot) q.Clear();
        if (mailboxOrderBySlot != null) foreach (var q in mailboxOrderBySlot) q.Clear();
        pendingByRequester.Clear();
        pendingSlotByRequester.Clear();
    }

    // ------------------- Core processing -------------------

    private void ProcessBudgeted()
    {
        if (!EnsureInit()) return;

        if (physicsCallsThisFrame > maxPhysicsCallsPerFrame)
            return;

        float regen = Mathf.Max(1, targetRequestsPerSecond) * Time.unscaledDeltaTime;
        tokenBucket = Mathf.Min(tokenBucket + regen, Mathf.Max(maxBurstPerFrame, 1));

        if (TotalPending() == 0 || tokenBucket < 1f)
            return;

        float frameStartMs = Time.realtimeSinceStartup * 1000f;
        int processedThisFrame = 0;

        int slots = Mathf.Max(1, distributionSlots);
        int slotThisFrame = Time.frameCount % slots;

        bool BudgetOK()
        {
            float elapsedMs = (Time.realtimeSinceStartup * 1000f) - frameStartMs;
            if (elapsedMs > maxPathfindingTimePerFrame) return false;
            if (processedThisFrame >= maxBurstPerFrame) return false;
            if (tokenBucket < 1f) return false;
            return true;
        }

        if (prioritizeMailboxes)
        {
            ProcessMailboxSlot(slotThisFrame, ref processedThisFrame, BudgetOK);
            ProcessAnonSlot(slotThisFrame, ref processedThisFrame, BudgetOK);
        }
        else
        {
            ProcessAnonSlot(slotThisFrame, ref processedThisFrame, BudgetOK);
            ProcessMailboxSlot(slotThisFrame, ref processedThisFrame, BudgetOK);
        }
    }

    private void ProcessMailboxSlot(int slot, ref int processedThisFrame, Func<bool> budgetOK)
    {
        var q = mailboxOrderBySlot[slot];
        while (q.Count > 0 && budgetOK())
        {
            int requesterId = q.Dequeue();

            if (!pendingByRequester.TryGetValue(requesterId, out var req))
                continue;

            pendingByRequester.Remove(requesterId);
            pendingSlotByRequester.Remove(requesterId);

            TryPrewarmIfLong(req);

            int callsBefore = physicsCallsThisFrame;
            pathfinding.FindPath(req.start, req.end, req.clearanceRadius, req.callback);
            physicsCallsThisFrame = callsBefore + EstimatePhysicsCalls();

            tokenBucket -= 1f;
            processedThisFrame++;
            pathsProcessedThisWindow++;
        }
    }

    private void ProcessAnonSlot(int slot, ref int processedThisFrame, Func<bool> budgetOK)
    {
        var q = anonBySlot[slot];
        while (q.Count > 0 && budgetOK())
        {
            var req = q.Dequeue();

            TryPrewarmIfLong(req);

            int callsBefore = physicsCallsThisFrame;
            pathfinding.FindPath(req.start, req.end, req.clearanceRadius, req.callback);
            physicsCallsThisFrame = callsBefore + EstimatePhysicsCalls();

            tokenBucket -= 1f;
            processedThisFrame++;
            pathsProcessedThisWindow++;
        }
    }

    // safety-net prewarm (cheap) so first hierarchical query doesn't spike
    private void TryPrewarmIfLong(in PathRequest req)
    {
        if (pathfinding == null) return;
        float thresh = pathfinding != null ? pathfinding.hierarchicalThreshold : 12f;
        if ((req.end - req.start).sqrMagnitude >= (thresh * thresh * 0.5f))
        {
            pathfinding.PrewarmCoarse(req.start, req.clearanceRadius, 2);
            pathfinding.PrewarmCoarse(req.end, req.clearanceRadius, 2);
        }
    }

    private int EstimatePhysicsCalls() => 200;

    public static void ReportPhysicsCall()
    {
        if (Time.frameCount != lastPhysicsFrame)
        {
            lastPhysicsFrame = Time.frameCount;
            physicsCallsThisFrame = 0;
        }
        physicsCallsThisFrame++;
    }

    private int TotalPending()
    {
        EnsureInit();
        int total = pendingByRequester.Count;
        if (anonBySlot != null)
        {
            for (int i = 0; i < anonBySlot.Length; i++) total += anonBySlot[i].Count;
        }
        return total;
    }

    private int TotalAnonCount()
    {
        if (anonBySlot == null) return 0;
        int total = 0;
        for (int i = 0; i < anonBySlot.Length; i++) total += anonBySlot[i].Count;
        return total;
    }

    private int HashToSlot(Vector3 a, Vector3 b, int slots)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(a.x * 10f);
            h = h * 31 + Mathf.RoundToInt(a.y * 10f);
            h = h * 31 + Mathf.RoundToInt(b.x * 10f);
            h = h * 31 + Mathf.RoundToInt(b.y * 10f);
            h &= 0x7fffffff;
            return slots <= 1 ? 0 : (h % slots);
        }
    }

    // --- init helper ----------------------------------------------------------

    private bool EnsureInit()
    {
        if (_initialized && mailboxOrderBySlot != null && anonBySlot != null && pathfinding != null)
            return true;

        if (pathfinding == null)
            pathfinding = FindObjectOfType<Pathfinding>();

        int slots = Mathf.Max(1, distributionSlots);
        if (mailboxOrderBySlot == null || mailboxOrderBySlot.Length != slots)
        {
            mailboxOrderBySlot = new Queue<int>[slots];
            for (int i = 0; i < slots; i++) mailboxOrderBySlot[i] = new Queue<int>();
        }
        if (anonBySlot == null || anonBySlot.Length != slots)
        {
            anonBySlot = new Queue<PathRequest>[slots];
            for (int i = 0; i < slots; i++) anonBySlot[i] = new Queue<PathRequest>();
        }

        _initialized = (pathfinding != null);
        return _initialized;
    }

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
