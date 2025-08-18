// Partial: communications windowing + sharing logic (now sends keys + world positions)
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        [Header("Comms: Skill & Roles")]
        [SerializeField, Range(1, 10)] private int commsSkill = 5;
        [SerializeField] private bool isLeader = false;
        [SerializeField, Range(0, 3)] private int leaderSkillBoost = 2;
        [SerializeField] private bool shareNewestFirst = true;

        [Header("Comms: Debug")]
        [SerializeField] private bool debugComms = true;

        [Header("Comms: Tunables (skill-mapped)")]
        [SerializeField, Range(0f, 1f)] private float shareBiasMin = 0.00f;
        [SerializeField, Range(0f, 1f)] private float shareBiasMax = 0.98f;
        [SerializeField, Range(0f, 1f)] private float obeyBiasMin = 0.00f;
        [SerializeField, Range(0f, 1f)] private float obeyBiasMax = 0.98f;

        [SerializeField] private Vector2Int recipientsMinMax = new Vector2Int(1, 16);
        [SerializeField] private Vector2Int shareKeysCapMinMax = new Vector2Int(0, 128);
        [SerializeField] private Vector2Int ingestCapMinMax = new Vector2Int(0, 128);
        [SerializeField] private Vector2 probeRadiusMinMax = new Vector2(0f, 16f);
        [SerializeField] private Vector2 baseWindowMinMax = new Vector2(0.20f, 4.00f);
        [SerializeField] private Vector2 phaseMinMax = new Vector2(0.17f, 3.00f);
        [SerializeField] private Vector2 jitterMinMax = new Vector2(0.05f, 0.35f);

        float skill01, shareBiasEff, obeyBiasEff;
        int effRecipients, effShareKeysCap, effIngestCap;
        float effProbeRadius, effBase, effPhase, effJitter;

        float nextWindowAt = 0f;
        bool commsInit = false;

        // scratch (avoid GC)
        readonly List<(int x, int y)> _shareScratchKeys = new(256);
        readonly List<Vector3> _shareScratchWorld = new(256);

        // peer cache
        static List<EnemyAICore> s_peerCache = new();
        static float s_peerCacheAt = -999f;
        const float PEER_CACHE_TTL = 0.50f;

        static IEnumerable<EnemyAICore> GetAllPeersCached()
        {
            if (Time.time - s_peerCacheAt > PEER_CACHE_TTL)
            {
                s_peerCache = FindObjectsOfType<EnemyAICore>().ToList();
                s_peerCacheAt = Time.time;
            }
            return s_peerCache;
        }

        int ShortId => Mathf.Abs(GetInstanceID()) % 10000;

        public void CommsTick(float dt)
        {
            EnsureCommsInit();
            if (Time.time < nextWindowAt) return;

            float roll = Random.value;
            if (roll > shareBiasEff)
            {
                float next = SampleNextWindow();
                nextWindowAt = Time.time + next;
                if (debugComms) CommsDbg($"window open -> skip (roll {roll:0.00} > shareBias {shareBiasEff:0.00}), next ~{next:0.00}s");
                return;
            }

            var candidates = ProbeNearby(effProbeRadius);
            int keysAvailable = GetShareableKeyCount();
            if (debugComms)
                CommsDbg($"probe hits={candidates.Count} radius={effProbeRadius:0} keys={keysAvailable} effRecipients={effRecipients} effIngestCap={effIngestCap}");

            int sharedPeers = 0;

            if (candidates.Count > 0 && keysAvailable > 0)
            {
                var considered = candidates.OrderBy(_ => Random.value).Take(effRecipients).ToList();
                if (considered.Count > 0)
                {
                    var target = considered[Random.Range(0, considered.Count)];
                    float recipObey = GetRecipientObeyWithLeaderBoost(target);
                    float acceptRoll = Random.value;

                    if (acceptRoll <= recipObey)
                    {
                        int toSend = Mathf.Min(keysAvailable, target.effIngestCap);

                        // build key list
                        _shareScratchKeys.Clear();
                        if (shareNewestFirst) CopySearchedKeysSampleRecency(_shareScratchKeys, toSend);
                        else CopySearchedKeysSample(_shareScratchKeys, toSend);

                        // build world list aligned to keys
                        _shareScratchWorld.Clear();
                        for (int i = 0; i < _shareScratchKeys.Count; i++)
                        {
                            if (TryWorldForKey(_shareScratchKeys[i], out var wp))
                                _shareScratchWorld.Add(wp);
                            else
                                _shareScratchWorld.Add(Vector3.zero); // fallback (still ingested, just no cyan)
                        }

                        target.IngestSearchedKeys(_shareScratchKeys, toSend, _shareScratchWorld);

                        if (debugComms)
                            CommsDbg($"  -> sent {toSend} keys to {target.name}{LeaderNoteForLog()} (cap {target.effIngestCap})");

                        sharedPeers = 1;
                    }
                    else if (debugComms)
                    {
                        CommsDbg($"  -> {target.name} refused (roll {acceptRoll:0.00} > obey {recipObey:0.00}){LeaderNoteForLog()}");
                    }
                }
            }

            if (debugComms) CommsDbg($"done: shared to {sharedPeers} peer(s)");
            nextWindowAt = Time.time + SampleNextWindow();
        }

        void EnsureCommsInit()
        {
            if (commsInit) return;

            skill01 = Mathf.InverseLerp(1f, 10f, commsSkill);
            shareBiasEff = Mathf.Lerp(shareBiasMin, shareBiasMax, skill01);
            obeyBiasEff = Mathf.Lerp(obeyBiasMin, obeyBiasMax, skill01);
            effRecipients = Mathf.RoundToInt(Mathf.Lerp(recipientsMinMax.x, recipientsMinMax.y, skill01));
            effShareKeysCap = Mathf.RoundToInt(Mathf.Lerp(shareKeysCapMinMax.x, shareKeysCapMinMax.y, skill01));
            effIngestCap = Mathf.RoundToInt(Mathf.Lerp(ingestCapMinMax.x, ingestCapMinMax.y, skill01));
            effProbeRadius = Mathf.Lerp(probeRadiusMinMax.x, probeRadiusMinMax.y, skill01);
            effBase = Mathf.Lerp(baseWindowMinMax.x, baseWindowMinMax.y, skill01);
            effPhase = Mathf.Lerp(phaseMinMax.x, phaseMinMax.y, skill01);
            effJitter = Mathf.Lerp(jitterMinMax.x, jitterMinMax.y, skill01);

            nextWindowAt = Time.time + SampleNextWindow();
            commsInit = true;

            if (debugComms)
                CommsDbg(
                    $"init base={effBase:0.00}s phase={effPhase:0.00}s radiusEff={effProbeRadius:0} " +
                    $"keysEff={effShareKeysCap} ingestEff={effIngestCap} recipsEff={effRecipients} " +
                    $"shareBiasEff={shareBiasEff:0.00} obeyBiasEff={obeyBiasEff:0.00} jitterEff={effJitter:0.00} " +
                    $"skill={commsSkill}({skill01:0.00}){(isLeader ? " [LEADER]" : "")}"
                );
        }

        float SampleNextWindow()
        {
            float t = effBase + Random.Range(0f, effPhase);
            float j = 1f + Random.Range(-effJitter, effJitter);
            return Mathf.Max(0.05f, t * j);
        }

        List<EnemyAICore> ProbeNearby(float radius)
        {
            var pos = transform.position;
            return GetAllPeersCached()
                .Where(p => p != this && p.isActiveAndEnabled)
                .Where(p => (p.transform.position - pos).sqrMagnitude <= radius * radius)
                .ToList();
        }

        int GetShareableKeyCount()
        {
            int have = _searchedOrder.Count;
            return Mathf.Min(effShareKeysCap, have);
        }

        float GetRecipientObeyWithLeaderBoost(EnemyAICore recipient)
        {
            int boostedRaw = Mathf.Clamp(recipient.commsSkill + (isLeader ? leaderSkillBoost : 0), 1, 10);
            float recip01 = Mathf.InverseLerp(1f, 10f, boostedRaw);
            return Mathf.Lerp(obeyBiasMin, obeyBiasMax, recip01);
        }

        string LeaderNoteForLog() => isLeader ? $" [sender=LEADER +{leaderSkillBoost}]" : "";

        void CommsDbg(string msg)
        {
            if (!debugComms) return;
            Debug.Log($"[COMMS][AI-{ShortId}] {msg}", this);
        }
    }
}
