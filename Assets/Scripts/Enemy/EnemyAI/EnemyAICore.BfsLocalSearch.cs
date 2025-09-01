// -------------------------------------------
// File: EnemyAI/EnemyAICore.BfsLocalSearch.cs
// Fast local-unsearched-point BFS (no HashSet/Queue)
// NOTE: You must wire IsCellUnsearchedLocal or IsWorldUnsearched
// to your coverage system for best behavior.
// -------------------------------------------
using UnityEngine;

namespace EnemyAI
{
    public partial class EnemyAICore
    {
        // ==== BFS scratch (allocation-free) ====
        private int[] _visitStamp;          // length = gridWidth * gridHeight
        private int _visitStampCur;       // incremented per search
        private int _visitW, _visitH;

        private int[] _qx, _qy;             // ring buffer for BFS
        private int _qMask, _qHead, _qTail;

        private Node[] _bfsNbuf = new Node[8]; // neighbor scratch for BFS
        private AreaChunker2D _areaChunker;    // set lazily

        // Hook these to your coverage logic:
        //  - IsCellUnsearchedLocal(gx, gy) for grid-indexed map (fastest)
        //  - IsWorldUnsearched(world) if you only have world-space checks
        public System.Func<int, int, bool> IsCellUnsearchedLocal;
        public System.Func<Vector2, bool> IsWorldUnsearched;

        private void EnsureBfsBuffers()
        {
            if (_areaChunker == null)
                _areaChunker = GetComponent<AreaChunker2D>() ?? GetComponentInParent<AreaChunker2D>();

            int w = grid.gridSizeXPublic;
            int h = grid.gridSizeYPublic;

            if (_visitStamp == null || _visitW != w || _visitH != h)
            {
                _visitStamp = new int[w * h];
                _visitW = w; _visitH = h;
            }

            // Queue capacity: power-of-two for fast wrap; sized for local searches
            int need = Mathf.NextPowerOfTwo(Mathf.Max(256, (w * h) / 8));
            if (_qx == null || _qx.Length != need)
            {
                _qx = new int[need];
                _qy = new int[need];
                _qMask = need - 1;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int ToIndex(int x, int y, int w) => y * w + x;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void QClear() { _qHead = _qTail = 0; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void QEnq(int x, int y)
        {
            _qx[_qTail & _qMask] = x;
            _qy[_qTail & _qMask] = y;
            _qTail++;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void QDeq(out int x, out int y)
        {
            x = _qx[_qHead & _qMask];
            y = _qy[_qHead & _qMask];
            _qHead++;
        }

        private bool QAny => _qHead != _qTail;

        // Cheap local predicate (grid-space first, then optional world fallback)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool IsCellUnsearchedLocalFast(int gx, int gy, Vector2 world)
        {
            if (IsCellUnsearchedLocal != null) return IsCellUnsearchedLocal(gx, gy);
            if (IsWorldUnsearched != null) return IsWorldUnsearched(world);
            // No predicate wired: assume "already searched" so we don't wander forever.
            return false;
        }

        /// <summary>
        /// Finds an unsearched point inside the strict area, starting from startWorld.
        /// Allocation-free BFS with visited stamp + ring buffer.
        /// </summary>
        public bool TryGetUnsearchedPointInAreaLocal(int areaId, Vector3 startWorld, out Vector3 pick)
        {
            pick = startWorld;
            if (grid == null || areaId < 0) return false;

            if (_areaChunker == null)
                _areaChunker = GetComponent<AreaChunker2D>() ?? GetComponentInParent<AreaChunker2D>();
            if (_areaChunker == null) return false;

            EnsureBfsBuffers();

            Node start = grid.NodeFromWorldPoint(startWorld, 0f);
            if (start == null) return false;

            // bump the visit stamp (wrap safely)
            _visitStampCur = (_visitStampCur == int.MaxValue) ? 1 : _visitStampCur + 1;

            QClear();
            int w = _visitW, h = _visitH;

            int sx = start.gridX, sy = start.gridY;
            if ((uint)sx >= w || (uint)sy >= h) return false;

            _visitStamp[ToIndex(sx, sy, w)] = _visitStampCur;
            QEnq(sx, sy);

            // Safety budget so one call can't monopolize a frame
            const int EXPAND_BUDGET = 4096;
            int expands = 0;

            while (QAny && expands < EXPAND_BUDGET)
            {
                QDeq(out int x, out int y);
                expands++;

                Node cur = grid.GetNodeByGridCoords(x, y, 0f);
                if (cur == null) continue;

                Vector2 pw = cur.worldPosition;

                // stay inside strict area and off portals for local picks
                if (_areaChunker.GetAreaIdStrict(pw) == areaId && !_areaChunker.IsPortal(pw))
                {
                    if (IsCellUnsearchedLocalFast(x, y, pw))
                    {
                        pick = cur.worldPosition;
                        return true;
                    }
                }

                // expand neighbors (respect your grid topology/clearance)
                int ncount = grid.GetNeighboursNonAlloc(cur, _bfsNbuf, 0f);
                for (int i = 0; i < ncount; i++)
                {
                    Node nb = _bfsNbuf[i];
                    if (nb == null || !nb.walkable) continue;

                    int nx = nb.gridX, ny = nb.gridY;
                    if ((uint)nx >= w || (uint)ny >= h) continue;

                    int nidx = ToIndex(nx, ny, w);
                    if (_visitStamp[nidx] == _visitStampCur) continue;

                    // quick area gate before enqueuing
                    if (_areaChunker.GetAreaIdStrict(nb.worldPosition) != areaId) continue;

                    _visitStamp[nidx] = _visitStampCur;
                    QEnq(nx, ny);
                }
            }

            return false;
        }
    }
}
