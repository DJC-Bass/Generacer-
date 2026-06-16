using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Listens for the round-timeout event and forces the player back to hub.
/// Behaves the same as falling through the kill floor, since both should
/// trigger a return-to-hub with the round ending.
/// </summary>
public class TrackSceneController : MonoBehaviour
{
    void Start()
    {
        if (GameLoopManager.Instance == null) return;

        GameLoopManager.Instance.OnRoundTimeoutInTrack += ForceReturnToHub;

        // Notify the manager that the player has officially entered the track
        GameLoopManager.Instance.NotifyEnteredTrack();
    }

    void OnDestroy()
    {
        if (GameLoopManager.Instance != null)
            GameLoopManager.Instance.OnRoundTimeoutInTrack -= ForceReturnToHub;
    }

    void ForceReturnToHub()
    {
        // Ran out of time without reaching the End Portal — same failure penalty as
        // the kill floor: wipe inventory back to the starting defaults.
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.ResetToStarting();

        // Same effect as kill floor � reload hub scene
        SceneManager.LoadScene(GameLoopManager.Instance.hubSceneName);
    }
}