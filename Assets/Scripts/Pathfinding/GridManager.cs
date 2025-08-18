using UnityEngine;
using System.Collections.Generic;

public class GridManager : MonoBehaviour
{
    [Header("Grid Settings")]
    public Vector2 gridWorldSize = new Vector2(30, 30);
    public float nodeRadius = 0.25f;
    public LayerMask unwalkableMask;

    private Node[,] grid;
    private float nodeDiameter;
    private int gridSizeX, gridSizeY;
    private Vector2 worldBottomLeft;

    public bool IsGridReady { get; private set; } = false;
    public float NodeDiameter => nodeDiameter;
    public int GridSizeX => gridSizeX;
    public int GridSizeY => gridSizeY;

    void Awake()
    {
        InitializeGrid();
    }

    void InitializeGrid()
    {
        nodeDiameter = nodeRadius * 2f;
        gridSizeX = Mathf.RoundToInt(gridWorldSize.x / nodeDiameter);
        gridSizeY = Mathf.RoundToInt(gridWorldSize.y / nodeDiameter);
        grid = new Node[gridSizeX, gridSizeY];

        worldBottomLeft = (Vector2)transform.position
                        - Vector2.right * gridWorldSize.x / 2f
                        - Vector2.up * gridWorldSize.y / 2f;

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int y = 0; y < gridSizeY; y++)
            {
                Vector2 worldPoint = worldBottomLeft
                                   + Vector2.right * (x * nodeDiameter + nodeRadius)
                                   + Vector2.up * (y * nodeDiameter + nodeRadius);

                bool walkable = !Physics2D.OverlapBox(worldPoint, Vector2.one * (nodeDiameter * 0.9f), 0, unwalkableMask);

                grid[x, y] = new Node(walkable, worldPoint, x, y);
            }
        }

        CalculateClearance();
        IsGridReady = true;
        Debug.Log($"<color=green>Grid initialized: {gridSizeX}x{gridSizeY}</color>");
    }

    void CalculateClearance()
    {
        for (int x = gridSizeX - 1; x >= 0; x--)
        {
            for (int y = gridSizeY - 1; y >= 0; y--)
            {
                Node node = grid[x, y];

                if (!node.walkable)
                {
                    node.clearance = 0;
                }
                else
                {
                    float right = (x + 1 < gridSizeX) ? grid[x + 1, y].clearance : 0;
                    float up = (y + 1 < gridSizeY) ? grid[x, y + 1].clearance : 0;
                    float diag = (x + 1 < gridSizeX && y + 1 < gridSizeY) ? grid[x + 1, y + 1].clearance : 0;

                    node.clearance = 1 + Mathf.Min(right, up, diag);
                }
            }
        }
    }

    public Node NodeFromWorldPoint(Vector2 worldPosition)
    {
        Vector2 percent = (worldPosition - worldBottomLeft) / gridWorldSize;
        percent.x = Mathf.Clamp01(percent.x);
        percent.y = Mathf.Clamp01(percent.y);

        int x = Mathf.RoundToInt((gridSizeX - 1) * percent.x);
        int y = Mathf.RoundToInt((gridSizeY - 1) * percent.y);

        return grid[x, y];
    }

    public Node GetNodeAt(int x, int y)
    {
        if (x >= 0 && x < gridSizeX && y >= 0 && y < gridSizeY)
            return grid[x, y];
        return null;
    }

    public List<Node> GetNeighbors(Node node)
    {
        List<Node> neighbors = new List<Node>();

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int checkX = node.gridX + dx;
                int checkY = node.gridY + dy;

                if (checkX >= 0 && checkX < gridSizeX && checkY >= 0 && checkY < gridSizeY)
                {
                    neighbors.Add(grid[checkX, checkY]);
                }
            }
        }

        return neighbors;
    }

    [System.Serializable]
    public class Node
    {
        public bool walkable;
        public Vector2 worldPos;
        public int gridX, gridY;
        public float clearance;

        public Node(bool walkable, Vector2 worldPos, int gridX, int gridY)
        {
            this.walkable = walkable;
            this.worldPos = worldPos;
            this.gridX = gridX;
            this.gridY = gridY;
            this.clearance = 0;
        }
    }
}
