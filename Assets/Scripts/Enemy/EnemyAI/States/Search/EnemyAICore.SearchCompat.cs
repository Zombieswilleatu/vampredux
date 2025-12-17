// ---------------------------------------------
// File: EnemyAI/States/Search/EnemyAICore.SearchCompat.cs  
// Purpose: Minimal compatibility shim for legacy comms code
// ---------------------------------------------
using System.Collections.Generic;
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore
    {
        // Legacy field for comms - unused but referenced
        public readonly List<(int x, int y)> _searchedOrder = new List<(int x, int y)>(0);
    }
}