using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persistent singleton that owns the game loop state. Survives scene loads
/// via DontDestroyOnLoad, so timer and round phase are preserved when the
/// player travels between hub and track scenes.
/// </summary>
public class GameLoopManager : MonoBehaviour
{
    public enum Phase
    {
        HubCountdown,    // Portal not yet spawned, countdown ticking
        HubPortalActive, // Portal spawned, round timer ticking, player in hub
        InTrack,         // Player is in the track scene, round timer ticking
        RoundEnded       // Round timer expired or player completed/fell off; back to HubCountdown soon
    }

    [Header("Round Settings")]
    [Tooltip("Minimum countdown before portal spawns (seconds).")]
    public float minPortalCountdown = 10f;
    [Tooltip("Maximum countdown before portal spawns (seconds).")]
    public float maxPortalCountdown = 20f;
    [Tooltip("Total time the player has from portal-spawn to track completion.")]
    public float roundDuration = 300f;
    [Tooltip("Brief pause before the next round's countdown starts after a round ends.")]
    public float postRoundDelay = 2f;

    [Header("Scene Names")]
    public string hubSceneName = "HubWorld";
    public string trackSceneName = "TrackScene";

    // Singleton
    public static GameLoopManager Instance { get; private set; }

    // Public state
    public Phase CurrentPhase { get; private set; } = Phase.HubCountdown;
    public float TimeRemainingInPhase { get; private set; }
    public float RoundTimeRemaining { get; private set; }

    // Events — scene controllers subscribe to these to react to state changes
    public event Action OnPortalShouldSpawn;
    public event Action OnPortalShouldDespawn;
    public event Action OnRoundTimeoutInTrack;

    void Awake()
    {

        // On the GameLoopManager, in Awake after singleton setup:
        SceneManager.LoadScene(hubSceneName);

        // Standard singleton setup with persistence
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Start the loop in countdown phase
        StartHubCountdown();
    }

    void Update()
    {
        switch (CurrentPhase)
        {
            case Phase.HubCountdown:
                TickCountdown();
                break;

            case Phase.HubPortalActive:
            case Phase.InTrack:
                TickRoundTimer();
                break;
        }
    }

    void TickCountdown()
    {
        TimeRemainingInPhase -= Time.deltaTime;
        if (TimeRemainingInPhase <= 0f)
            EnterPortalActivePhase();
    }

    void TickRoundTimer()
    {
        RoundTimeRemaining -= Time.deltaTime;
        if (RoundTimeRemaining <= 0f)
            HandleRoundExpiry();
    }

    // -------------------------------------------------------
    //  Phase Transitions
    // -------------------------------------------------------

    void StartHubCountdown()
    {
        CurrentPhase = Phase.HubCountdown;
        TimeRemainingInPhase = UnityEngine.Random.Range(minPortalCountdown, maxPortalCountdown);
        Debug.Log($"[GameLoop] Hub countdown started — {TimeRemainingInPhase:F1}s until portal spawns");
    }

    void EnterPortalActivePhase()
    {
        CurrentPhase = Phase.HubPortalActive;
        RoundTimeRemaining = roundDuration;
        Debug.Log($"[GameLoop] Portal active — {RoundTimeRemaining:F0}s round timer started");
        OnPortalShouldSpawn?.Invoke();
    }

    void HandleRoundExpiry()
    {
        if (CurrentPhase == Phase.HubPortalActive)
        {
            Debug.Log("[GameLoop] Round expired in hub — despawning portal");
            OnPortalShouldDespawn?.Invoke();
            EndRoundAndRestart();
        }
        else if (CurrentPhase == Phase.InTrack)
        {
            Debug.Log("[GameLoop] Round expired in track — sending player back to hub");
            OnRoundTimeoutInTrack?.Invoke();

            // CRITICAL: Advance the phase so this branch doesn't fire again next frame.
            // The scene reload triggered by the listener will take a few frames to
            // complete, during which Update() is still running on this persistent manager.
            EndRoundAndRestart();
        }
    }

    void EndRoundAndRestart()
    {
        CurrentPhase = Phase.RoundEnded;
        Invoke(nameof(StartHubCountdown), postRoundDelay);
    }

    // -------------------------------------------------------
    //  Public API — called by scene-side controllers
    // -------------------------------------------------------

    /// <summary>Player has entered the hub portal — they're now in the track.</summary>
    public void NotifyEnteredTrack()
    {
        if (CurrentPhase == Phase.HubPortalActive)
        {
            CurrentPhase = Phase.InTrack;
            Debug.Log($"[GameLoop] Player entered track — {RoundTimeRemaining:F0}s remaining");
        }
    }

    /// <summary>Player returned to hub via end-portal or kill-floor.</summary>
    public void NotifyReturnedToHub()
    {
        if (CurrentPhase == Phase.InTrack)
        {
            Debug.Log("[GameLoop] Player returned to hub — round complete");
            EndRoundAndRestart();
        }
    }

    /// <summary>Triggers a fresh seed for the next track scene generation.</summary>
    public int GetNextTrackSeed()
    {
        return UnityEngine.Random.Range(1, 999999);
    }
}