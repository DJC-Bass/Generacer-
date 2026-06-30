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

    [Header("Rewards")]
    [Tooltip("Credits granted when the player completes the track (reaches the End Portal). " +
             "Tweak for balancing.")]
    public int trackCompletionCredits = 200;
    [Tooltip("Extra credits granted on top of the completion reward if the player reaches " +
             "the End Portal before any AI car (drone/challenger) finishes — i.e. first place. " +
             "Tweak for balancing.")]
    public int firstPlaceBonusCredits = 200;

    [Header("Endings")]
    [Tooltip("Number of SDs the player must collect (one per first-place finish) to WIN the game.")]
    public int sdItemsToWin = 3;
    [Tooltip("Drone wins that trigger the game-over Drone ending. A 'drone win' is any round — once " +
             "the portal has spawned — that does NOT end in a player first-place finish: a drone " +
             "beat the player to the End Portal, or the player died / aborted / skipped / timed out.")]
    public int droneWinsToGameOver = 2;

    [Header("Scene Names")]
    public string hubSceneName = "HubWorld";
    public string trackSceneName = "TrackScene";

    // Singleton
    public static GameLoopManager Instance { get; private set; }

    // Public state
    public Phase CurrentPhase { get; private set; } = Phase.HubCountdown;
    public float TimeRemainingInPhase { get; private set; }
    public float RoundTimeRemaining { get; private set; }

    /// <summary>Which game-loop round we're on, counting up from 1. Incremented each time
    /// the portal spawns (one per loop cycle, whether or not the player enters the track).
    /// Drives how many obstacle spawners are active that round.</summary>
    public int RoundNumber { get; private set; }

    /// <summary>True once any AI car (drone/challenger) has reached the track finish
    /// this round. Reset when the player enters the track. Read by the End Portal to
    /// decide whether the player earned the first-place bonus.</summary>
    public bool AnyRacerFinishedAhead { get; private set; }

    // Events � scene controllers subscribe to these to react to state changes
    public event Action OnPortalShouldSpawn;
    public event Action OnPortalShouldDespawn;
    public event Action OnRoundTimeoutInTrack;

    /// <summary>Rounds the drones have won so far (see <see cref="droneWinsToGameOver"/>).</summary>
    public int DroneWins { get; private set; }
    /// <summary>True once either ending has triggered — the normal round loop then halts.</summary>
    public bool GameEnded { get; private set; }
    /// <summary>True once the game-over Drone ending has begun; the hub reads this on load to start it.</summary>
    public bool DroneEndingActive { get; private set; }
    /// <summary>True once the player-victory ending has begun; the hub reads this on load to start it
    /// (keeps the portal from spawning and flashes the "BOTS DEFEATED" banner).</summary>
    public bool PlayerWinActive { get; private set; }

    public event Action OnDroneEnding;   // fired once when the drones hit the game-over threshold
    public event Action OnPlayerWin;     // fired once when the player collects enough SDs

    // Per-round scoring flags, reset each time the portal spawns.
    private bool playerFirstPlaceThisRound;
    private bool roundOutcomeCounted;

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

    void OnDestroy()
    {
        // Clear the static so the next GameLoopManager (e.g. after quitting to the menu and
        // starting again) becomes the live singleton instead of self-destroying as a duplicate.
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (GameEnded) return;   // an ending has taken over — stop the normal round loop

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
        Debug.Log($"[GameLoop] Hub countdown started � {TimeRemainingInPhase:F1}s until portal spawns");
    }

    void EnterPortalActivePhase()
    {
        CurrentPhase = Phase.HubPortalActive;
        RoundTimeRemaining = roundDuration;
        RoundNumber++;   // each portal spawn begins a new game-loop round
        playerFirstPlaceThisRound = false;   // fresh round — outcome not yet decided
        roundOutcomeCounted = false;
        Debug.Log($"[GameLoop] Round {RoundNumber} — portal active, {RoundTimeRemaining:F0}s round timer started");
        OnPortalShouldSpawn?.Invoke();
    }

    void HandleRoundExpiry()
    {
        if (CurrentPhase == Phase.HubPortalActive)
        {
            Debug.Log("[GameLoop] Round expired in hub � despawning portal");
            OnPortalShouldDespawn?.Invoke();
            EndRoundAndRestart();
        }
        else if (CurrentPhase == Phase.InTrack)
        {
            Debug.Log("[GameLoop] Round expired in track � sending player back to hub");
            OnRoundTimeoutInTrack?.Invoke();

            // CRITICAL: Advance the phase so this branch doesn't fire again next frame.
            // The scene reload triggered by the listener will take a few frames to
            // complete, during which Update() is still running on this persistent manager.
            EndRoundAndRestart();
        }
    }

    void EndRoundAndRestart()
    {
        EvaluateRoundOutcome();      // score the round for the player or the drones (may end the game)

        CurrentPhase = Phase.RoundEnded;
        if (GameEnded) return;       // an ending began — don't queue another round
        Invoke(nameof(StartHubCountdown), postRoundDelay);
    }

    /// <summary>
    /// Scores the round that's ending. A player FIRST-place finish is a player round win (and clinches
    /// the game once they hold <see cref="sdItemsToWin"/> SDs). Anything else — a drone beat the player
    /// to the End Portal, or the player died / aborted / skipped / timed out — is a drone win, which
    /// triggers the game-over Drone ending at <see cref="droneWinsToGameOver"/>. Runs once per round.
    /// </summary>
    void EvaluateRoundOutcome()
    {
        if (GameEnded || roundOutcomeCounted) return;
        roundOutcomeCounted = true;

        if (playerFirstPlaceThisRound)
        {
            if (CountPlayerSDs() >= sdItemsToWin)
            {
                GameEnded = true;
                PlayerWinActive = true;
                Debug.Log("[GameLoop] Player collected enough SDs — PLAYER WINS.");
                OnPlayerWin?.Invoke();
            }
        }
        else
        {
            DroneWins++;
            Debug.Log($"[GameLoop] Drone win {DroneWins}/{droneWinsToGameOver}.");
            if (DroneWins >= droneWinsToGameOver)
            {
                GameEnded = true;
                DroneEndingActive = true;
                Debug.Log("[GameLoop] Drones reached the win threshold — DRONE ENDING begins.");
                OnDroneEnding?.Invoke();
            }
        }
    }

    /// <summary>Counts the distinct SD items the player currently holds (items whose name ends in " SD").</summary>
    static int CountPlayerSDs()
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) return 0;

        int count = 0;
        foreach (var name in inv.Order)
            if (!string.IsNullOrEmpty(name) && name.EndsWith(" SD") && inv.GetCount(name) > 0)
                count++;
        return count;
    }

    // -------------------------------------------------------
    //  Public API � called by scene-side controllers
    // -------------------------------------------------------

    /// <summary>Player has entered the hub portal � they're now in the track.</summary>
    public void NotifyEnteredTrack()
    {
        if (CurrentPhase == Phase.HubPortalActive)
        {
            CurrentPhase = Phase.InTrack;
            AnyRacerFinishedAhead = false;   // fresh race — nobody has finished yet
            Debug.Log($"[GameLoop] Player entered track � {RoundTimeRemaining:F0}s remaining");
        }
    }

    /// <summary>Called by the End Portal when the player completes the track in FIRST place (no AI
    /// finished ahead). Marks this round as a player win, so it does NOT count toward the drones.</summary>
    public void NotifyPlayerFirstPlace()
    {
        playerFirstPlaceThisRound = true;
    }

    /// <summary>Called by an AI car (drone/challenger) when it reaches the end of its
    /// path. Marks that the player can no longer claim first place this round.</summary>
    public void NotifyRacerFinished()
    {
        if (!AnyRacerFinishedAhead)
            Debug.Log("[GameLoop] An AI car reached the finish first — first-place bonus lost");
        AnyRacerFinishedAhead = true;
    }

    /// <summary>Player returned to hub via end-portal or kill-floor.</summary>
    public void NotifyReturnedToHub()
    {
        if (CurrentPhase == Phase.InTrack)
        {
            Debug.Log("[GameLoop] Player returned to hub � round complete");
            EndRoundAndRestart();
        }
    }

    /// <summary>Triggers a fresh seed for the next track scene generation.</summary>
    public int GetNextTrackSeed()
    {
        return UnityEngine.Random.Range(1, 999999);
    }

    /// <summary>Ends the current run and destroys this manager so the NEXT Bootstrap load creates a
    /// fresh GameLoopManager (round/phase/timer reset to the start). Call when quitting to the main
    /// menu — otherwise this DontDestroyOnLoad singleton carries its old state into the next game,
    /// stranding the player in the hub with no portal until the stale round finishes.</summary>
    public static void EndRun()
    {
        if (Instance != null) Destroy(Instance.gameObject);
    }
}