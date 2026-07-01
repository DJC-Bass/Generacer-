using System.Collections.Generic;
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

    [Header("First-Place Item Reward")]
    [Tooltip("Equippable 'SD' items granted one-at-a-time for finishing first. Each first-place " +
             "win grants one the player doesn't already own (no duplicates) until they hold all.")]
    public string[] firstPlaceItemPool = { "Fire SD", "Lightning SD", "Wind SD" };

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

        var manager = GameLoopManager.Instance;

        // Notify the manager BEFORE loading the scene so the round properly ends. This is where a
        // 3-SD finish is scored as the win (setting PlayerWinActive, and SpecialEndingEarned too if
        // the player never used an SD ability all run).
        if (manager != null)
            manager.NotifyReturnedToHub();

        // Flawless win — collected every SD WITHOUT ever using an SD ability — routes to the special
        // "Generacers" ending scene instead of back to the hub for the standard victory banner. Falls
        // through to the hub if that scene isn't in Build Settings, so a misconfig can't strand the player.
        if (manager != null && manager.SpecialEndingEarned
            && !string.IsNullOrEmpty(manager.specialEndingSceneName)
            && Application.CanStreamedLevelBeLoaded(manager.specialEndingSceneName))
        {
            SceneManager.LoadScene(manager.specialEndingSceneName);
            return;
        }

        string sceneName = !string.IsNullOrEmpty(hubSceneNameOverride)
                         ? hubSceneNameOverride
                         : manager != null
                             ? manager.hubSceneName
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

        // Tell the loop this round was a player win (so it isn't scored for the drones, and can
        // clinch the game at 3 SDs). Anything other than first place is left to count as a drone win.
        if (firstPlace && manager != null) manager.NotifyPlayerFirstPlace();

        int reward = completion + (firstPlace ? firstPlaceBonus : 0);
        inventory.AddCredits(reward);

        Debug.Log(firstPlace
            ? $"[ReturnPortal] First place! Awarded {reward} credits " +
              $"({completion} completion + {firstPlaceBonus} first-place bonus)"
            : $"[ReturnPortal] Track complete. Awarded {reward} credits (completion only)");

        // First place also grants one random equippable SD the player doesn't own yet.
        if (firstPlace) AwardFirstPlaceItem(inventory);
    }

    /// <summary>
    /// On a first-place finish, grants one random item from the pool that the player
    /// doesn't already own (no duplicates). Successive first-place wins fill out the set
    /// until the player owns them all, after which this is a no-op. These SDs are wiped by
    /// the kill-floor / timeout failure reset, so failing forces the player to re-earn them.
    /// </summary>
    void AwardFirstPlaceItem(PlayerInventory inventory)
    {
        if (firstPlaceItemPool == null || firstPlaceItemPool.Length == 0) return;

        // Collect the pool items the player doesn't have yet.
        var missing = new List<string>();
        foreach (var item in firstPlaceItemPool)
            if (!string.IsNullOrEmpty(item) && inventory.GetCount(item) == 0)
                missing.Add(item);

        if (missing.Count == 0) return;   // already owns the full set

        string award = missing[Random.Range(0, missing.Count)];
        inventory.Add(award, 1);
        Debug.Log($"[ReturnPortal] First place — awarded item: {award}");
    }
}