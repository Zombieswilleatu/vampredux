using UnityEngine;

[ExecuteInEditMode]
public class SnapToGrid : MonoBehaviour
{
    [Header("Grid Settings")]
    public float gridSize = 1f;
    public bool snapOnStart = true;
    public bool snapInEditor = true;

    [Header("Size Snapping")]
    public bool snapSize = false;
    [Tooltip("Snap visual size to whole-number increments (e.g., 1, 2, 3...)")]
    public float sizeIncrement = 1f;

    [Header("Manual Controls")]
    public bool snapSizeNow = false;

    [Header("Debug")]
    public bool showDebugInfo = false;
    public bool logSizeChanges = false;

    private Vector3 lastScale;
    private bool isSnapping = false;

    void Start()
    {
        lastScale = transform.localScale;
        if (snapOnStart)
        {
            Snap();
            if (snapSize) SnapSizeToIncrement();
        }
    }

    void Update()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && !isSnapping)
        {
            if (snapSizeNow)
            {
                snapSizeNow = false;
                SnapSizeToIncrement();
            }

            if (snapInEditor)
            {
                Snap();

                if (snapSize)
                {
                    Vector3 currentScale = transform.localScale;
                    if (Vector3.Distance(currentScale, lastScale) > 0.1f)
                    {
                        SnapSizeToIncrement();
                        lastScale = currentScale;
                    }
                }
            }
        }
#endif
    }

    public void Snap()
    {
        if (isSnapping) return;

        float step = gridSize;
        float offset = step / 2f;

        Vector3 pos = transform.position;
        float snappedX = Mathf.Round((pos.x - offset) / step) * step + offset;
        float snappedY = Mathf.Round((pos.y - offset) / step) * step + offset;
        Vector3 newPos = new Vector3(snappedX, snappedY, pos.z);

        if (Vector3.Distance(newPos, pos) > 0.001f)
        {
            if (showDebugInfo)
                Debug.Log($"{gameObject.name}: Position snapped from {pos} to {newPos}");
            transform.position = newPos;
        }
    }


    public void SnapSizeToIncrement()
    {
        if (isSnapping) return;
        isSnapping = true;

        try
        {
            Vector2 visualSize = GetVisualSize();

            if (visualSize == Vector2.zero)
            {
                Debug.LogWarning($"{gameObject.name}: Could not determine visual size");
                return;
            }

            Vector2 snappedSize = new Vector2(
                SnapValueToIncrement(visualSize.x),
                SnapValueToIncrement(visualSize.y)
            );

            if (showDebugInfo && logSizeChanges)
            {
                Debug.Log($"{gameObject.name}: Visual size {visualSize} -> Snapped size {snappedSize}");
            }

            Vector3 currentScale = transform.localScale;
            Vector2 scaleMultiplier = new Vector2(
                snappedSize.x / visualSize.x,
                snappedSize.y / visualSize.y
            );

            Vector3 newScale = new Vector3(
                currentScale.x * scaleMultiplier.x,
                currentScale.y * scaleMultiplier.y,
                currentScale.z
            );

            transform.localScale = newScale;
            lastScale = newScale;

            if (showDebugInfo && logSizeChanges)
                Debug.Log($"{gameObject.name}: Scale set to {newScale} to achieve size {snappedSize}");
        }
        finally
        {
            isSnapping = false;
        }
    }

    private Vector2 GetVisualSize()
    {
        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null)
            return sr.bounds.size;

        Collider2D col = GetComponent<Collider2D>();
        if (col != null)
            return col.bounds.size;

        return Vector2.zero;
    }

    private float SnapValueToIncrement(float value)
    {
        return Mathf.Round(value / sizeIncrement) * sizeIncrement;
    }

    [ContextMenu("Snap Size NOW")]
    public void ManualSnapSize()
    {
        SnapSizeToIncrement();
    }

    [ContextMenu("Snap Position NOW")]
    public void ManualSnapPosition()
    {
        Snap();
    }

    [ContextMenu("EMERGENCY: Reset Scale")]
    public void EmergencyResetScale()
    {
        transform.localScale = Vector3.one;
        lastScale = Vector3.one;
        Debug.Log($"{gameObject.name}: Scale reset to (1,1,1)");
    }

    void OnDrawGizmosSelected()
    {
        Vector2 visualSize = GetVisualSize();
        if (visualSize != Vector2.zero)
        {
            Gizmos.color = snapSize ? new Color(0f, 1f, 0f, 0.5f) : new Color(1f, 1f, 0f, 0.5f);
            Gizmos.DrawWireCube(transform.position, new Vector3(visualSize.x, visualSize.y, 0.1f));

            if (snapSize)
            {
                Vector2 snappedSize = new Vector2(
                    SnapValueToIncrement(visualSize.x),
                    SnapValueToIncrement(visualSize.y)
                );

                if (Vector2.Distance(visualSize, snappedSize) > 0.01f)
                {
                    Gizmos.color = new Color(1f, 0f, 1f, 0.7f);
                    Gizmos.DrawWireCube(transform.position, new Vector3(snappedSize.x, snappedSize.y, 0.1f));
                }
            }
        }

        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(transform.position, 0.05f);
    }
}
