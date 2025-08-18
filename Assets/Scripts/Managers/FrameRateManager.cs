// ================================
// File: FrameRateManager.cs
// ================================
using UnityEngine;

public class FrameRateManager : MonoBehaviour
{
    public enum CapMode { Unlimited = 0, VSync = 1, Cap60 = 2, Cap90 = 3, Cap120 = 4 }

    public static FrameRateManager Instance { get; private set; }

    [Header("Defaults")]
    [SerializeField] private CapMode defaultMode = CapMode.Unlimited;
    [SerializeField] private int fudgeBelowHz = 2; // e.g. 118 for 120 Hz

    private const string PrefKey = "fr_cap_mode";
    private CapMode _mode;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _mode = (CapMode)PlayerPrefs.GetInt(PrefKey, (int)defaultMode);
        Apply(_mode);
    }

    public CapMode GetMode() => _mode;

    public void SetMode(CapMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        PlayerPrefs.SetInt(PrefKey, (int)_mode);
        PlayerPrefs.Save();
        Apply(_mode);
    }

    private void Apply(CapMode mode)
    {
        switch (mode)
        {
            case CapMode.Unlimited:
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1; // uncapped
                break;

            case CapMode.VSync:
                QualitySettings.vSyncCount = 1;   // v-sync governs; ignores targetFrameRate
                Application.targetFrameRate = -1;
                break;

            case CapMode.Cap60:
                CapTo(60); break;
            case CapMode.Cap90:
                CapTo(90); break;
            case CapMode.Cap120:
                CapTo(120); break;
        }

        Debug.Log($"[FrameRate] Mode={mode} vSync={QualitySettings.vSyncCount} target={Application.targetFrameRate}");
    }

    private void CapTo(int hz)
    {
        QualitySettings.vSyncCount = 0;                            // v-sync off; we control it
        Application.targetFrameRate = Mathf.Max(30, hz - fudgeBelowHz);
    }
}
