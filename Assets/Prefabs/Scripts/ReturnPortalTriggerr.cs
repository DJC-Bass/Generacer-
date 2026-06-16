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

        // Walk up the hierarchy to find the Player tag � handles cases where
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
        // Reward the player for completing the track BEFORE the manager ends the round.
        AwardCompletionCredits();

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

    /// <summary>
    /// Grants the player their credits for reaching the End Portal: the flat
    /// completion reward, plus a first-place bonus if no AI car finished the track
    /// first this round. Both amounts are tunable on the GameLoopManager.
    /// </summary>
    void AwardCompletionCredits()
    {
        var inventory = PlayerInventory.Instance;
        if (inventory == null) return;   // nothing to credit if the inventory isn't up

        var manager = GameLoopManager.Instance;

        // Fall back to sensible defaults if the manager is somehow missing.
        int completion = manager != null ? manager.trackCompletionCredits : 200;
        int firstPlaceBonus = manager != null ? manager.firstPlaceBonusCredits : 200;

        // First place = no AI car has finished ahead of the player this round.
        bool firstPlace = manager == null || !manager.AnyRacerFinishedAhead;

        int reward = completion + (firstPlace ? firstPlaceBonus : 0);
        inventory.AddCredits(reward);

        Debug.Log(firstPlace
            ? $"[ReturnPortal] First place! Awarded {reward} credits " +
              $"({completion} completion + {firstPlaceBonus} first-place bonus)"
            : $"[ReturnPortal] Track complete. Awarded {reward} credits (completion only)");
    }
}