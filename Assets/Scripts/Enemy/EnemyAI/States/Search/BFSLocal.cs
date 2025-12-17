// -------------------------------------------
// File: EnemyAI/Search/BfsLocal.cs
// Purpose: Allocation-free local BFS to find an unsearched point in a strict area.
// Notes:
//  - Uses a ring buffer + visit stamps (no GC).
//  - Depends only on Grid + AreaChunker2D.
//  - Intended to be called by SearchMemory (not directly by states).
// -------------------------------------------
using UnityEngine;

namespace EnemyAI
{
    public sealed class BfsLocal
    {
        private readonly Grid _grid;
        private readonly AreaChunker2D _chunker;

        // visit stamps sized to grid; incremented per query
        private int[] _visit;  // length = W*H
        private int _tick;
        private int _w, _h;

        // ring buffer queue (power-of-two sized)
        private int[] _qx, _qy;
        private int _mask, _head, _tail;

        // scratch
        private readonly Node[] _nbuf = new Node[8];

        public BfsLocal(Grid grid, AreaChunker2D chunker)
        {
            _grid = grid;
            _chunker = chunker;
            EnsureBuffers();
        }

        private void EnsureBuffers()
        {
            if (_grid == null) return;

            int w = _grid.gridSizeXPublic;
            int h = _grid.gridSizeYPublic;
            if (w <= 0 || h <= 0) return;

            if (_visit == null || _w != w || _h != h)
            {
                _visit = new int[w * h];
                _w = w; _h = h;

                int cap = Mathf.NextPowerOfTwo(Mathf.Max(256, (w * h) / 8));
                _qx = new int[cap];
                _qy = new int[cap];
                _mask = cap - 1;
            }
        }

        public bool TryGetUnsearchedInArea(
            int areaId,
            Vector3 startWorld,
            System.Func<int, int, bool> isUnsearchedCell, // (gx,gy) -> bool
            out Vector3 world,
            int maxExpansions = 2048)
        {
            world = startWorld;
            if (_grid == null || _chunker == null || areaId < 0) return false;

            EnsureBuffers();

            var start = _grid.NodeFromWorldPoint(startWorld);
            if (start == null || !start.walkable) return false;
            if (!_grid.IsWalkableCached(start, 0f)) return false;

            _tick = (_tick == int.MaxValue) ? 1 : _tick + 1;

            _head = _tail = 0;
            Enq(start.gridX, start.gridY);
            MarkVisited(start.gridX, start.gridY);

            int expands = 0;

            while (_head != _tail && expands < maxExpansions)
            {
                Deq(out int x, out int y);
                expands++;

                var n = _grid.GetNodeByGridCoords(x, y);
                if (n == null || !n.walkable) continue;
                if (!_grid.IsWalkableCached(n, 0f)) continue;

                Vector2 wp = n.worldPosition;

                // stay inside strict area & avoid portals for a *local* pick
                if (_chunker.GetAreaIdStrict(wp) == areaId && !_chunker.IsPortal(wp))
                {
                    if (isUnsearchedCell != null && isUnsearchedCell(x, y))
                    {
                        world = n.worldPosition;
                        return true;
                    }
                }

                // expand neighbors (clearance 0 → fastest topology)
                int count = _grid.GetNeighboursNonAlloc(n, _nbuf, 0f);
                for (int i = 0; i < count; i++)
                {
                    var nb = _nbuf[i];
                    if (nb == null || !nb.walkable) continue;

                    int gx = nb.gridX, gy = nb.gridY;
                    if ((uint)gx >= _w || (uint)gy >= _h) continue;
                    if (Visited(gx, gy)) continue;

                    // quick area gate before enqueue
                    if (_chunker.GetAreaIdStrict(nb.worldPosition) != areaId) continue;

                    MarkVisited(gx, gy);
                    Enq(gx, gy);
                }
            }

            return false;
        }

        // ── queue helpers ─────────────────────────────────────────────
        private void Enq(int x, int y)
        {
            _qx[_tail & _mask] = x;
            _qy[_tail & _mask] = y;
            _tail++;
        }

        private void Deq(out int x, out int y)
        {
            x = _qx[_head & _mask];
            y = _qy[_head & _mask];
            _head++;
        }

        // ── visit helpers ────────────────────────────────────────────
        private int ToIdx(int x, int y) => y * _w + x;
        private void MarkVisited(int x, int y) => _visit[ToIdx(x, y)] = _tick;
        private bool Visited(int x, int y) => _visit[ToIdx(x, y)] == _tick;
    }
}
