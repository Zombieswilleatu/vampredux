// ---------------------------------------------
// File: EnemyAI/States/Search/EnemyAICore.SearchCompatOverloads.cs
// Purpose: Instance-method overloads to satisfy legacy Comms.cs calls
// (tuple keys, and a 3-arg IngestSearchedKeys with (keys, budget, outWorlds))
// ---------------------------------------------
using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore : MonoBehaviour
    {
        // Newer callsites pass tuple keys; provide tuple overloads so the
        // compiler picks these instead of older List<int> signatures elsewhere.

        public void CopySearchedKeysSampleRecency(List<(int x, int y)> dst, int budget)
        {
            // No-op shim (we aren't actually sharing search yet)
            if (dst != null) dst.Clear();
        }

        public void CopySearchedKeysSample(List<(int x, int y)> dst, int budget)
        {
            // No-op shim
            if (dst != null) dst.Clear();
        }

        public bool TryWorldForKey((int x, int y) key, out Vector3 world)
        {
            world = default;
            if (grid == null) return false;
            var n = grid.GetNodeByGridCoords(key.x, key.y);
            if (n == null) return false;
            world = n.worldPosition;
            return true;
        }

        // Some Comms paths call: IngestSearchedKeys(keysTuple, budget, outWorlds)
        // Provide that exact parameter order to avoid binding to other overloads.
        public void IngestSearchedKeys(List<(int x, int y)> keys, int budget, List<Vector3> outWorlds)
        {
            if (outWorlds != null) outWorlds.Clear();
            if (grid == null || keys == null || outWorlds == null) return;

            int count = Mathf.Clamp(budget, 0, keys.Count);
            for (int i = 0; i < count; i++)
            {
                var (x, y) = keys[i];
                var n = grid.GetNodeByGridCoords(x, y);
                if (n != null) outWorlds.Add(n.worldPosition);
            }
        }
    }
}
