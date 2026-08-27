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
    /// inventory, and leave — to the LOBBY in multiplayer, to the main menu solo.
    ///
    /// ⚠️ When the HOST does this, everyone else is pulled to the lobby too (see TeardownToLobby).
    /// The host runs the whole simulation, so a world they have left is a frozen one nobody can act in
    /// or escape from.</summary>
    void ReturnToMainMenu()
    {
        // Multiplayer: this pad returns to the LOBBY ROOM, not the main menu, and stays in the session
        // (2026-08-27). It is the in-world way to say "I am done with this run", not "I am done with
        // these people" — leaving the session outright is what the lobby's own BACK button is for, and
        // for a host it would delete the room from under everybody.
        if (MultiplayerWorld.IsMultiplayerGame)
        {
            // Nothing here is hub-specific: this component gates on the player TAG only, so the same
            // behaviour follows the pad wherever it is placed, TrackScene included.
            MultiplayerWorld.Instance.TeardownToLobby("LEFT VIA THE EXIT PAD");
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
