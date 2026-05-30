using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Trigger volume for the track scene's return portal. When the player
/// passes through, notifies the GameLoopManager that the round is complete
/// and loads the hub scene.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ReturnPortalTrigger : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Tag of the player car.")]
    public string playerTag = "Player";
    [Tooltip("Name of the hub scene to load. Leave empty to use the manager's value.")]
    public string hubSceneNameOverride = "";

    private bool triggered;

    void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (triggered) return;

        // Walk up the hierarchy to find the Player tag — handles cases where
        // a wheel collider or sub-object enters the trigger first
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                triggered = true;
                ReturnToHub();
                return;
            }
            t = t.parent;
        }
    }

    void ReturnToHub()
    {
        // Notify the manager BEFORE loading the scene so the round properly ends
        if (GameLoopManager.Instance != null)
            GameLoopManager.Instance.NotifyReturnedToHub();

        string sceneName = !string.IsNullOrEmpty(hubSceneNameOverride)
                         ? hubSceneNameOverride
                         : GameLoopManager.Instance != null
                             ? GameLoopManager.Instance.hubSceneName
                             : "HubWorld";

        SceneManager.LoadScene(sceneName);
    }
}