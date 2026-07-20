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
    [Tooltip("Special \"flawless win\" ending scene — loaded straight from the track instead of the " +
             "hub victory when the player collects all SDs WITHOUT ever using an SD ability this run.")]
    public string specialEndingSceneName = "GeneracersEnding";

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

    /// <summary>True if the player has activated ANY SD ability at least once this run. Latched by
    /// <see cref="NotifySDAbilityUsed"/> the first time an ability turns on, never cleared within a
    /// run (it survives a failure SD-wipe), and gone only when the manager is torn down for a new run.
    /// Using even one SD forfeits the flawless "Generacers" ending.</summary>
    public bool UsedAnySDThisRun { get; private set; }

    /// <summary>True when the player has earned the special flawless ending: they just won
    /// (<see cref="PlayerWinActive"/>) AND never used an SD ability all run. Read at the End Portal to
    /// route the player to <see cref="specialEndingSceneName"/> instead of the hub victory banner.</summary>
    public bool SpecialEndingEarned => PlayerWinActive && !UsedAnySDThisRun;

    public event Action OnDroneEnding;   // fired once when the drones hit the game-over threshold
    public event Action OnPlayerWin;     // fired once when the player collects enough SDs

    // Per-round scoring flags, reset each time the portal spawns.
    private bool playerFirstPlaceThisRound;
    private bool roundOutcomeCounted;

    // ---- Multiplayer puppet mode (Phase 2) ----
    /// <summary>When true this manager is REMOTE-DRIVEN: the multiplayer server owns the round loop,
    /// so local phase transitions/scoring are suppressed and <see cref="RemoteBeginRound"/> /
    /// <see cref="RemoteEndRound"/> (called by MultiplayerWorld from network messages) drive the
    /// events. Set BEFORE the manager is created; cleared by MultiplayerWorld's teardown.</summary>
    public static bool RemoteDriven;
    /// <summary>The server-rolled seed <see cref="GetNextTrackSeed"/> returns while remote-driven,
    /// so TrackGenerator's existing seed path needs no changes.</summary>
    public static int RemoteTrackSeed;

    void Awake()
    {
        // Standard singleton setup with persistence
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Bootstrap redirect: this manager lives in the Bootstrap scene, whose only job is to
        // create it and hand off to the hub. Safely behind the singleton guard, so a duplicate
        // manager in some other scene can never bounce the game back to the hub.
        SceneManager.LoadScene(hubSceneName);

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

        // Remote-driven (multiplayer): the server owns all transitions. Tick the round timer down
        // purely for display; the real round end arrives as a network message.
        if (RemoteDriven)
        {
            if (CurrentPhase == Phase.HubPortalActive || CurrentPhase == Phase.InTrack)
                RoundTimeRemaining = Mathf.Max(0f, RoundTimeRemaining - Time.deltaTime);
            return;
        }

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

    // -------------------------------------------------------
    //  Multiplayer puppet API — called by MultiplayerWorld when the server's round messages land.
    //  These fire the SAME events the local loop would, so every consumer (HubSceneController's
    //  portal spawn/despawn, RoundObstacleSelector's RoundNumber read, TrackGenerator's seed pull)
    //  works unmodified in multiplayer.
    // -------------------------------------------------------

    /// <summary>Server said the round is PRELOADING: the track loads/generates NOW (during the hub
    /// countdown, the least-crucial moment) but stays frozen — round state is set so the TrackScene's
    /// Start-time consumers (RoundObstacleSelector's RoundNumber read, spawner elapsed maths) see the
    /// new round, while the phase stays HubCountdown so nothing ticks and no portal spawns yet.</summary>
    public void RemotePrepareRound(int roundNumber, float roundDuration)
    {
        if (!RemoteDriven) return;
        RoundNumber = roundNumber;
        CurrentPhase = Phase.HubCountdown;          // frozen: the puppet timer only ticks in HubPortalActive
        RoundTimeRemaining = roundDuration;         // elapsed reads as 0 until the round goes live
        playerFirstPlaceThisRound = false;
        roundOutcomeCounted = false;
        AnyRacerFinishedAhead = false;
        Debug.Log($"[GameLoop] (remote) Round {roundNumber} preloading — track generating, timers held");
    }

    /// <summary>Server said the round is LIVE: spawn the portal everywhere and start the timer.</summary>
    public void RemoteBeginRound(int roundNumber, float roundDuration)
    {
        if (!RemoteDriven) return;
        RoundNumber = roundNumber;
        CurrentPhase = Phase.HubPortalActive;
        RoundTimeRemaining = roundDuration;
        Debug.Log($"[GameLoop] (remote) Round {roundNumber} — portal active, {roundDuration:F0}s round timer");
        OnPortalShouldSpawn?.Invoke();
    }

    /// <summary>Server said the round ended: despawn the portal everywhere. Round scoring is the
    /// server's job (MultiplayerScoring) — never scored locally in multiplayer.</summary>
    public void RemoteEndRound()
    {
        if (!RemoteDriven) return;
        Debug.Log("[GameLoop] (remote) Round ended — despawning portal");
        OnPortalShouldDespawn?.Invoke();
        CurrentPhase = Phase.HubCountdown;
        TimeRemainingInPhase = 0f;
        RoundTimeRemaining = 0f;
    }

    /// <summary>Server replicated the SHARED drone-wins tally (HUDs read <see cref="DroneWins"/>).</summary>
    public void RemoteSetDroneWins(int wins)
    {
        if (!RemoteDriven) return;
        DroneWins = wins;
    }

    /// <summary>Server declared the drone ending — game over for EVERYONE, both teams. Fires the same
    /// event the local loop would, so HubSceneController's swarm presentation plays unmodified.</summary>
    public void RemoteTriggerDroneEnding()
    {
        if (!RemoteDriven || GameEnded) return;
        GameEnded = true;
        DroneEndingActive = true;
        Debug.Log("[GameLoop] (remote) DRONE ENDING — the drones beat both teams.");
        OnDroneEnding?.Invoke();
    }

    /// <summary>Server declared a team victory. Fires the player-win event so HubSceneController's
    /// banner presentation plays; MultiplayerWorld sets the banner text per team beforehand.</summary>
    public void RemoteTriggerTeamVictory()
    {
        if (!RemoteDriven || GameEnded) return;
        GameEnded = true;
        PlayerWinActive = true;
        Debug.Log("[GameLoop] (remote) TEAM VICTORY.");
        OnPlayerWin?.Invoke();
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
        if (RemoteDriven) return;   // multiplayer: per-player presence is MultiplayerWorld's business
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

    /// <summary>Called by <see cref="SDAbilityController"/> the first time an SD ability is activated.
    /// Latches <see cref="UsedAnySDThisRun"/> for the rest of the run, forfeiting the flawless ending.</summary>
    public void NotifySDAbilityUsed()
    {
        UsedAnySDThisRun = true;
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
        if (RemoteDriven) return;   // multiplayer: the server ends rounds, not a single player's return
        if (CurrentPhase == Phase.InTrack)
        {
            Debug.Log("[GameLoop] Player returned to hub � round complete");
            EndRoundAndRestart();
        }
    }

    /// <summary>Triggers a fresh seed for the next track scene generation. In multiplayer this
    /// returns the SERVER-rolled round seed instead, so every client generates the same track.</summary>
    public int GetNextTrackSeed()
    {
        if (RemoteDriven) return RemoteTrackSeed;
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