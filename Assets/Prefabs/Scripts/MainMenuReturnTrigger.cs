using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Place this on a collider in the hub: when the player car touches it, the current run is torn
/// down and the player is sent back to the Main Menu. Works whether the collider is a solid box
/// (<see cref="OnCollisionEnter"/>) or a trigger volume (<see cref="OnTriggerEnter"/>), so it
/// doesn't matter how the Box Collider's "Is Trigger" flag is set.
/// </summary>
[RequireComponent(typeof(Collider))]
public class MainMenuReturnTrigger : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Tag that identifies the player car. Anything else touching this is ignored.")]
    public string playerTag = "Player";

    [Header("Scene Loading")]
    [Tooltip("Scene loaded when the player touches this collider.")]
    public string mainMenuSceneName = "MainMenu";

    private bool triggered;

    // Handle both collider styles so the zone works as a trigger OR a solid box.
    void OnTriggerEnter(Collider other) => TryReturn(other.transform);
    void OnCollisionEnter(Collision collision) => TryReturn(collision.transform);

    void TryReturn(Transform hit)
    {
        if (triggered) return;

        // Walk up the hierarchy so a wheel / sub-collider touching first still counts as the player.
        Transform t = hit;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                triggered = true;
                ReturnToMainMenu();
                return;
            }
            t = t.parent;
        }
    }

    /// <summary>Tear down the run (so the NEXT game starts fresh, like the QUIT button), reset the
    /// inventory, and load the Main Menu.</summary>
    void ReturnToMainMenu()
    {
        // Multiplayer: leave the session first (host leave deletes it for everyone), then the world
        // teardown does the run/inventory reset and loads the Main Menu itself.
        if (MultiplayerWorld.IsMultiplayerGame)
        {
            if (NetworkSessionManager.Instance != null)
                _ = NetworkSessionManager.Instance.LeaveSessionAsync();
            MultiplayerWorld.Instance.TeardownToMenu("LEFT VIA HUB EXIT");
            return;
        }

        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();

        if (!string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            SceneManager.LoadScene(mainMenuSceneName);
        else
            Debug.LogWarning($"[MainMenuReturnTrigger] Main menu scene '{mainMenuSceneName}' isn't in Build Settings.");
    }
}
