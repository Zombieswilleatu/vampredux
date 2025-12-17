// ---------------------------------------------
// File: EnemyAI/States/Search/EnemyAICore.BfsOverloadExtensions.cs
// Purpose: 4-arg overload shim used by SearchState (compat).
// ---------------------------------------------
using UnityEngine;

namespace EnemyAI
{
    public static class EnemyAICoreBfsOverloadExtensions
    {
        // Delegates to the instance 4-arg implementation; passes legacy budget through.
        public static bool TryGetUnsearchedPointInAreaLocal(
            this EnemyAICore core,
            int areaId,
            Vector3 startWorld,
            out Vector3 pick,
            int maxExpansions /* legacy parameter */)
        {
            return core.TryGetUnsearchedPointInAreaLocal(areaId, startWorld, out pick, maxExpansions);
        }
    }
}
