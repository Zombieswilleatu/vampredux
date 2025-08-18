using UnityEngine;
using System.Collections;

public class GameManager : MonoBehaviour
{
    public float preloadDelay = 5f;
    public GameObject gameplayRoot; // Optional: parent for gameplay elements

    private bool gameStarted = false;
    private float deltaTime = 0f;

    void Start()
    {
        if (gameplayRoot != null)
            gameplayRoot.SetActive(false);

        StartCoroutine(PreloadAndStartGame());
    }

    IEnumerator PreloadAndStartGame()
    {
        yield return new WaitForSeconds(preloadDelay);

        if (gameplayRoot != null)
            gameplayRoot.SetActive(true);

        gameStarted = true;
    }

    void Update()
    {
        if (!gameStarted) return;

        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        float fps = 1f / deltaTime;

        // Log warning to console
        if (fps < 10)
        {
            Debug.LogWarning($"<color=red>[FPS: {fps:F1}] 🚨 CRITICAL</color>");
        }
        else if (fps < 20)
        {
            Debug.LogWarning($"<color=orange>[FPS: {fps:F1}] 🛑 LOW</color>");
        }
        else if (fps < 30)
        {
            Debug.Log($"<color=yellow>[FPS: {fps:F1}] ⚠️ Warning</color>");
        }
        else if (Time.frameCount % 120 == 0) // only log every few seconds at healthy FPS
        {
            Debug.Log($"<color=green>[FPS: {fps:F1}] ✅ OK</color>");
        }
    }
}
