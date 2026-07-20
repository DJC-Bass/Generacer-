using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Acts as a fail boundary. When the player passes through this collider,
/// the hub scene is loaded. Attach to a flat plane positioned below the
/// playable area.
/// </summary>
[RequireComponent(typeof(Collider))]
public class KillFloor : MonoBehaviour
{
    [Header("Scene Loading")]
    [Tooltip("Build index of the hub scene to load when the player falls through. " +
             "Set this to whatever build index your Hub world uses (typically 0).")]
    public int hubSceneIndex = 0;

    [Tooltip("Optional: scene name to load instead of build index. If both are set, " +
             "name takes priority. Leave empty to use build index.")]
    public string hubSceneName = "";

    [Header("Detection")]
    [Tooltip("Tag that identifies the player. Anything else passing through is ignored.")]
    public string playerTag = "Player";

    private bool triggered;

    void Reset()
    {
        // Auto-configure the collider as a trigger when added in the editor
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        // Drone? Despawn just that drone � independent of the player guard so a
        // falling drone never blocks the player's own kill-floor trigger.
        DroneCar drone = other.GetComponentInParent<DroneCar>();
        if (drone != null)
        {
            // Pay the player their bounty if THEY knocked this car in here. No-op
            // for cars that fell on their own. Guarded against double-paying.
            drone.AwardKnockoffBounty();
            Destroy(drone.gameObject);
            return;
        }

        if (triggered) return;

        // Walk up the hierarchy looking for the Player tag
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
        // Failed the run (fell off the track) — wipe inventory back to the defaults.
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.ResetToStarting();

        if (GameLoopManager.Instance != null)
            GameLoopManager.Instance.NotifyReturnedToHub();   // no-op while remote-driven

        // Multiplayer: dying is a per-player TELEPORT back to the hub — the round keeps running for
        // everyone still racing. (The trigger re-arms so this client can fail again next round.)
        if (MultiplayerWorld.IsMultiplayerGame)
        {
            MultiplayerWorld.Instance.ReturnToHubLocally();
            triggered = false;
            return;
        }

        if (!string.IsNullOrEmpty(hubSceneName))
            SceneManager.LoadScene(hubSceneName);
        else
            SceneManager.LoadScene(hubSceneIndex);
    }
}