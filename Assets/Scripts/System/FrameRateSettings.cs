// ================================
// File: FrameRateSettingsUI.cs
// ================================
using UnityEngine;
using UnityEngine.UI;

public class FrameRateSettingsUI : MonoBehaviour
{
    [SerializeField] private Dropdown dropdown; // assign in inspector (UGUI)

    private void Start()
    {
        if (dropdown != null)
        {
            dropdown.ClearOptions();
            dropdown.AddOptions(new System.Collections.Generic.List<string>
            {
                "Unlimited", "VSync", "60", "90", "120"
            });

            var current = FrameRateManager.Instance != null
                ? FrameRateManager.Instance.GetMode()
                : FrameRateManager.CapMode.Unlimited;

            dropdown.value = (int)current;
            dropdown.onValueChanged.AddListener(OnDropdownChanged);
        }
    }

    public void OnDropdownChanged(int idx)
    {
        if (FrameRateManager.Instance == null) return;
        FrameRateManager.Instance.SetMode((FrameRateManager.CapMode)idx);
    }

    // Optional: use this from a simple Button to cycle without a dropdown
    public void CycleNext()
    {
        if (FrameRateManager.Instance == null) return;
        var m = FrameRateManager.Instance.GetMode();
        int next = ((int)m + 1) % 5;
        FrameRateManager.Instance.SetMode((FrameRateManager.CapMode)next);
        if (dropdown) dropdown.value = next;
    }
}
