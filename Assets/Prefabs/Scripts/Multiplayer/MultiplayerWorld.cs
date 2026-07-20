using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Phase 2 of the multiplayer roadmap: the ADDITIVE shared world. In a multiplayer game the hub and
/// the track are never alternating scene loads — HubWorld stays loaded for the whole session and the
/// TrackScene is additively loaded each round at <see cref="TrackAreaOffset"/>, so players occupy the
/// two areas simultaneously and move between them by TELEPORT (portal in, end-portal/kill-floor/LRA/
/// round-end out). Single-player is untouched: everything here is inert until <see cref="Launch"/>.
///
/// The HOST is the single source of truth for rounds and randomness. It runs the round loop
/// (countdown → round start → duration/all-left → round end, using the local GameLoopManager's tuning
/// fields) and broadcasts via NGO custom named messages — deliberately prefab-free, so Phase 2 needs
/// zero editor setup. Each round it rolls ONE seed; every client's TrackScene load derives all its
/// randomness from that seed (track geometry via the puppet <see cref="GameLoopManager.GetNextTrackSeed"/>,
/// road hue / skybox hue / blackout roll / obstacle pick via <see cref="DeriveRandom"/>), so all
/// players race the same track under the same sky. (Phase 4 migrates game state onto NetworkVariables
/// once the Phase 3 player prefab exists; the message layer here is the interim transport.)
///
/// Every client keeps a real <see cref="GameLoopManager"/> alive but in REMOTE-DRIVEN (puppet) mode:
/// its phase transitions come only from these messages, which keeps every existing consumer
/// (HubSceneController portal spawn/despawn, RoundObstacleSelector, RoundDirectionalLightToggle,
/// TrackGenerator seeding) working unmodified.
///
/// Per-area presentation is LOCAL: the active scene follows the local player's area (so RenderSettings
/// skybox/fog and AudioManager's scene music follow), each area's directional lights are enabled only
/// for occupants of that area, and the TrackScene's own cameras/placed car/speedometer are stripped or
/// area-gated on load (the hub camera rig follows the car everywhere).
/// </summary>
public class MultiplayerWorld : MonoBehaviour
{
    public static MultiplayerWorld Instance { get; private set; }

    /// <summary>True while a multiplayer game (not just a lobby) is running. Gameplay scripts branch
    /// on this for teleport-instead-of-LoadScene behaviour.</summary>
    public static bool IsMultiplayerGame => Instance != null && Instance.begun;

    /// <summary>True while the LOCAL player is in the track area. The multiplayer replacement for
    /// "CurrentPhase == InTrack" checks (per-player presence isn't a global phase in a shared world) —
    /// the LRA abort gates on it, and the per-client obstacle spawners only run while it's true.</summary>
    public bool InTrackLocally => inTrackLocally;

    /// <summary>Where the track area lives relative to the hub. -35 km keeps the whole generated
    /// track (which spans up to ~28 km forward from its origin) clear of the hub while staying inside
    /// the float-precision envelope the single-player tracks already occupy (~30 km ⇒ mm-scale).</summary>
    public static readonly Vector3 TrackAreaOffset = new Vector3(0f, 0f, -35000f);

    /// <summary>The server-rolled seed for the current round (0 between rounds). Every per-round
    /// random decision on every client must derive from this (see <see cref="DeriveRandom"/>).</summary>
    public static int CurrentRoundSeed { get; private set; }

    /// <summary>A deterministic RNG for one named random decision (e.g. "skybox", "roadhue",
    /// "blackout", "obstacles"), derived from the round seed + the stream name (FNV-1a) so streams
    /// are independent but every client computes identical values.</summary>
    public static System.Random DeriveRandom(string stream)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in stream) { h ^= c; h *= 16777619u; }
            return new System.Random((int)(h ^ (uint)CurrentRoundSeed * 2654435761u));
        }
    }

    // ---- Named messages (server → clients unless noted) ----
    const string MsgReady = "GNRC_READY";            // client → server: hub loaded, ready for rounds
    const string MsgRoundStart = "GNRC_ROUND_START"; // {round, seed}
    const string MsgRoundEnd = "GNRC_ROUND_END";     // {reason: 0 timeout, 1 all racers left}
    const string MsgArea = "GNRC_AREA";              // client → server: {inTrack}

    const string MainMenuSceneName = "MainMenu";

    // ---- Local state ----
    private bool begun;
    private Scene hubScene;
    private Scene trackScene;
    private bool hubCaptured;
    private Vector3 hubSpawnPos;
    private Quaternion hubSpawnRot;
    private bool inTrackLocally;
    private bool roundActive;
    private int roundNumber;
    private bool teleporting;
    private GameObject trackSpeedometerRoot;   // TrackScene's own speed HUD — shown only while in the track area
    private readonly List<(Light light, bool wasEnabled)> hubLights = new List<(Light, bool)>();
    private readonly List<(Light light, bool wasEnabled)> trackLights = new List<(Light, bool)>();

    // ---- Server state ----
    private readonly HashSet<ulong> readyClients = new HashSet<ulong>();
    private readonly HashSet<ulong> enteredThisRound = new HashSet<ulong>();
    private readonly HashSet<ulong> inTrackNow = new HashSet<ulong>();

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    /// <summary>Starts the multiplayer game on this client (host and joiners alike). Called by
    /// <see cref="NetworkSessionManager"/> when the session's "started" flag lands. Idempotent.</summary>
    public static void Launch()
    {
        if (Instance == null)
        {
            var go = new GameObject("MultiplayerWorld");
            go.AddComponent<MultiplayerWorld>();   // sets Instance in Awake
        }
        Instance.BeginSession();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void BeginSession()
    {
        if (begun) return;
        begun = true;

        // Puppet mode BEFORE the manager exists so its Awake countdown never self-ticks.
        GameLoopManager.RemoteDriven = true;
        GameLoopManager.RemoteTrackSeed = 0;

        SceneManager.sceneLoaded += OnSceneLoaded;
        RegisterMessageHandlers();
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

        // Phase 3: remote players — roster + 30 Hz car-state stream + extrapolated puppets.
        gameObject.AddComponent<RemoteCarManager>();

        // Phase 4: server-authoritative round scoring, team SD aggregation and endings.
        gameObject.AddComponent<MultiplayerScoring>();

        // Creating the manager doubles as the transition out of the menu: its Awake loads the hub.
        if (GameLoopManager.Instance == null)
            new GameObject("GameLoopManager").AddComponent<GameLoopManager>();
        else
            SceneManager.LoadScene(GameLoopManager.Instance.hubSceneName);

        if (IsServer) StartCoroutine(ServerRoundLoop());
        Debug.Log("[MultiplayerWorld] Multiplayer game begun — loading the hub.");
    }

    /// <summary>Ends the multiplayer game on this client and returns to the Main Menu (used for the
    /// session dying under us AND for deliberate quits). Mirrors the single-player quit teardown.</summary>
    public void TeardownToMenu(string reason)
    {
        if (!begun) return;
        begun = false;

        StopAllCoroutines();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        UnregisterMessageHandlers();
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

        CurrentRoundSeed = 0;
        GameLoopManager.RemoteDriven = false;
        GameLoopManager.RemoteTrackSeed = 0;
        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();

        Debug.Log($"[MultiplayerWorld] Teardown to menu: {reason}");
        SceneManager.LoadScene(MainMenuSceneName);   // single-mode load also clears the additive track
        Destroy(gameObject);
    }

    // -------------------------------------------------------
    //  Scene lifecycle
    // -------------------------------------------------------

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!begun) return;
        var glm = GameLoopManager.Instance;
        string hubName = glm != null ? glm.hubSceneName : "HubWorld";
        string trackName = glm != null ? glm.trackSceneName : "TrackScene";

        if (scene.name == hubName && mode == LoadSceneMode.Single)
        {
            hubScene = scene;
            hubCaptured = false;
            inTrackLocally = false;
            StartCoroutine(CaptureHubScene(scene));
        }
        else if (scene.name == trackName && mode == LoadSceneMode.Additive)
        {
            trackScene = scene;
            PrepareTrackScene(scene);                 // runs before the scene's Start()s
            StartCoroutine(CaptureTrackScene(scene)); // runs after them
        }
    }

    /// <summary>Waits for PlayerCarSwapper to place the chosen car, then records the hub spawn pose
    /// (every return-teleport lands there) and the hub's directional lights, and reports ready.</summary>
    IEnumerator CaptureHubScene(Scene scene)
    {
        yield return null;
        yield return null;

        var car = PlayerRegistry.LocalCar;
        if (car != null)
        {
            hubSpawnPos = car.transform.position;
            hubSpawnRot = car.transform.rotation;
        }
        else
        {
            hubSpawnPos = Vector3.up * 2f;
            hubSpawnRot = Quaternion.identity;
            Debug.LogWarning("[MultiplayerWorld] No Player car found in the hub — return teleports use a fallback pose.");
        }

        hubLights.Clear();
        foreach (var light in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (light.type == LightType.Directional && light.gameObject.scene == scene)
                hubLights.Add((light, light.enabled));

        hubCaptured = true;
        ApplyAreaPresentation();

        if (IsServer) MarkReady(NetworkManager.Singleton.LocalClientId);
        else SendReadyToServer();
    }

    /// <summary>Immediately after the additive track load, BEFORE its scripts' Start(): shove the
    /// whole scene to the track area offset, and strip the pieces the shared world replaces — the
    /// scene's own cameras/listeners (the hub rig follows the car everywhere), its authored test car
    /// (the real car teleports in through the portal), and area-gate its speed HUD.</summary>
    void PrepareTrackScene(Scene scene)
    {
        trackSpeedometerRoot = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            root.transform.position += TrackAreaOffset;

            if (root.CompareTag("Player"))
            {
                root.SetActive(false);   // drop it from FindWithTag immediately
                Destroy(root);
                continue;
            }

            foreach (var cam in root.GetComponentsInChildren<Camera>(true))
                cam.gameObject.SetActive(false);
            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;

            if (trackSpeedometerRoot == null && root.GetComponentInChildren<Speedometer>(true) != null)
            {
                trackSpeedometerRoot = root;
                root.SetActive(inTrackLocally);
            }
        }
    }

    /// <summary>After the track scene's Start()s ran (generator built the track, the blackout toggle
    /// rolled): record its directional lights AS TOGGLED — restoring that recorded state is what keeps
    /// a blackout round dark when a player teleports in.</summary>
    IEnumerator CaptureTrackScene(Scene scene)
    {
        yield return null;
        yield return null;

        trackLights.Clear();
        foreach (var light in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (light.type == LightType.Directional && light.gameObject.scene == scene)
                trackLights.Add((light, light.enabled));

        ApplyAreaPresentation();
    }

    // -------------------------------------------------------
    //  Per-player area movement (the teleports)
    // -------------------------------------------------------

    /// <summary>True when the local player can take the hub portal into the track right now.</summary>
    public bool CanEnterTrack =>
        begun && roundActive && !inTrackLocally && !teleporting
        && trackScene.IsValid() && trackScene.isLoaded && TrackGenerator.Current != null;

    /// <summary>Teleports the local car onto the track start (the multiplayer version of the hub
    /// portal's scene load). Reuses the generator's spawn pose + spawn-boost tuning.</summary>
    public void EnterTrackLocally()
    {
        if (!CanEnterTrack) return;
        StartCoroutine(EnterTrackRoutine());
    }

    IEnumerator EnterTrackRoutine()
    {
        teleporting = true;
        var generator = TrackGenerator.Current;
        var car = PlayerRegistry.LocalCar;
        if (generator == null || car == null) { teleporting = false; yield break; }

        var rb = car.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        car.transform.SetPositionAndRotation(generator.CarSpawnPosition, generator.CarSpawnRotation);

        inTrackLocally = true;
        ApplyAreaPresentation();
        AudioManager.PlayPortalExit(car.transform);

        // Same two-step as the generator's own spawn: let the teleport register with physics before
        // the boost so initial-penetration resolution can't cancel the velocity.
        if (rb != null && generator.spawnVelocityMph > 0f)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            generator.ApplySpawnBoostTo(rb);
        }

        SendAreaToServer(true);
        teleporting = false;
    }

    /// <summary>Teleports the local car back to the hub spawn — the multiplayer version of every
    /// "return to hub" scene load (end portal, kill floor, LRA abort, round end).</summary>
    public void ReturnToHubLocally(bool notifyServer = true)
    {
        if (!begun || !inTrackLocally) return;

        var car = PlayerRegistry.LocalCar;
        if (car != null)
        {
            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            car.transform.SetPositionAndRotation(hubSpawnPos, hubSpawnRot);
        }

        inTrackLocally = false;
        ApplyAreaPresentation();
        if (car != null) AudioManager.PlayPortalExit(car.transform);

        // Boulders are simulated per-client (until Phase 5): clear ours so nothing we launched keeps
        // arcing/rumbling — or follows us — once we're back in the hub. Other players' clients run
        // their own boulders, so this touches nobody else's race.
        foreach (var boulder in FindObjectsByType<BoulderObstacle>(FindObjectsSortMode.None))
            Destroy(boulder.gameObject);

        if (notifyServer) SendAreaToServer(false);
    }

    /// <summary>Everything cosmetic that follows the LOCAL player's area: active scene (RenderSettings
    /// skybox/fog + scene-keyed music), per-area directional lights, the track speed HUD, and a camera
    /// snap so the follow rigs don't swoosh 35 km across the world.</summary>
    void ApplyAreaPresentation()
    {
        var scene = inTrackLocally ? trackScene : hubScene;
        if (scene.IsValid() && scene.isLoaded)
            SceneManager.SetActiveScene(scene);   // fires activeSceneChanged → SkyboxHueRandomizer recolors

        SetAreaLights(hubLights, !inTrackLocally);
        SetAreaLights(trackLights, inTrackLocally);

        if (trackSpeedometerRoot != null) trackSpeedometerRoot.SetActive(inTrackLocally);

        AudioManager.RefreshSceneMusic();
        SnapFollowCameras();
    }

    static void SetAreaLights(List<(Light light, bool wasEnabled)> lights, bool areaActive)
    {
        foreach (var (light, wasEnabled) in lights)
            if (light != null)
                light.enabled = areaActive && wasEnabled;   // restoring wasEnabled preserves blackout rounds
    }

    void SnapFollowCameras()
    {
        var car = PlayerRegistry.LocalCar;
        if (car == null) return;
        foreach (var cam in FindObjectsByType<CameraFollow>(FindObjectsSortMode.None))
        {
            if (cam.target == null) continue;
            cam.transform.position = car.transform.TransformPoint(cam.offset);
            cam.transform.LookAt(car.transform);
        }
    }

    // -------------------------------------------------------
    //  Round lifecycle (client side — applied from messages; the host applies locally too)
    // -------------------------------------------------------

    void ApplyRoundStart(int round, int seed)
    {
        if (!begun || roundActive) return;
        var manager = GameLoopManager.Instance;
        if (manager != null && manager.GameEnded) return;   // an ending has taken over — no more rounds
        roundNumber = round;
        roundActive = true;
        CurrentRoundSeed = seed;
        GameLoopManager.RemoteTrackSeed = seed;

        var glm = GameLoopManager.Instance;
        if (glm != null) glm.RemoteBeginRound(round, glm.roundDuration);   // fires OnPortalShouldSpawn

        Debug.Log($"[MultiplayerWorld] Round {round} started (seed {seed}) — loading the track area.");
        SceneManager.LoadSceneAsync(glm != null ? glm.trackSceneName : "TrackScene", LoadSceneMode.Additive);
    }

    void ApplyRoundEnd(byte reason)
    {
        if (!begun || !roundActive) return;
        roundActive = false;

        if (inTrackLocally) ReturnToHubLocally(notifyServer: false);

        var glm = GameLoopManager.Instance;
        if (glm != null) glm.RemoteEndRound();   // fires OnPortalShouldDespawn

        if (trackScene.IsValid() && trackScene.isLoaded)
            SceneManager.UnloadSceneAsync(trackScene);
        trackLights.Clear();
        trackSpeedometerRoot = null;

        CurrentRoundSeed = 0;
        GameLoopManager.RemoteTrackSeed = 0;
        Debug.Log($"[MultiplayerWorld] Round {roundNumber} ended ({(reason == 0 ? "timer expired" : "all racers left")}).");
    }

    /// <summary>Applies a server-declared ending on this client (called by MultiplayerScoring for the
    /// host and from its ENDING message for everyone else). Fires the puppet GameLoopManager events so
    /// the EXISTING HubSceneController presentations play on all machines at once — the drone swarm,
    /// or the victory banner with per-team text (the roadmap's "fold the deferred single-player
    /// victory presentation into the team win" — one banner, text chosen by which side you're on).</summary>
    public void ApplyEnding(bool isDroneEnding, int winningTeam)
    {
        if (!begun) return;
        roundActive = false;   // belt-and-braces: no portal entry during an ending

        var glm = GameLoopManager.Instance;
        if (glm == null) return;

        if (isDroneEnding)
        {
            glm.RemoteTriggerDroneEnding();   // HubSceneController: swarm + portal (inert in MP) + music
            return;
        }

        // Team victory: set the banner text for THIS machine's perspective before the presentation.
        var hub = FindAnyObjectByType<HubSceneController>();
        if (hub != null)
        {
            int myTeam = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;
            bool won = myTeam == winningTeam;
            hub.victoryBannerText = won ? "YOUR TEAM WINS" : $"TEAM {winningTeam} WINS";
            hub.victoryBannerColor = won ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.45f, 0.35f);
        }
        glm.RemoteTriggerTeamVictory();
    }

    // -------------------------------------------------------
    //  Server round loop — the single source of rounds and randomness
    // -------------------------------------------------------

    IEnumerator ServerRoundLoop()
    {
        // Let every member (and our own hub) load in before the first countdown, with a cap so one
        // stuck client can't hold the game hostage.
        float deadline = Time.time + 25f;
        while (Time.time < deadline)
        {
            if (hubCaptured && AllConnectedReady()) break;
            yield return new WaitForSeconds(0.5f);
        }

        var glm = GameLoopManager.Instance;
        while (begun)
        {
            float countdown = glm != null
                ? UnityEngine.Random.Range(glm.minPortalCountdown, glm.maxPortalCountdown)
                : UnityEngine.Random.Range(10f, 20f);
            yield return new WaitForSeconds(countdown);

            int seed = UnityEngine.Random.Range(1, 999999);
            enteredThisRound.Clear();
            inTrackNow.Clear();
            var scoring = GetComponent<MultiplayerScoring>();
            if (scoring != null) scoring.ResetRoundServer();   // fresh first-place claim
            BroadcastRoundStart(roundNumber + 1, seed);

            // Round runs for the configured duration, or ends early once at least one player raced
            // and every player who entered the track has left it (per the multiplayer design).
            float remaining = glm != null ? glm.roundDuration : 300f;
            bool allLeft = false;
            while (remaining > 0f)
            {
                yield return new WaitForSeconds(0.5f);
                remaining -= 0.5f;
                if (enteredThisRound.Count > 0 && inTrackNow.Count == 0) { allLeft = true; break; }
            }
            BroadcastRoundEnd(allLeft ? (byte)1 : (byte)0);

            // Phase 4: score the round ONCE, after the end broadcast — everyone is already back in
            // the hub, so an ending presentation lands on players standing in it. May broadcast an
            // ending; then this loop stops scheduling rounds.
            if (scoring != null)
            {
                scoring.EvaluateRoundServer();
                if (scoring.GameOverServer) yield break;
            }

            yield return new WaitForSeconds(glm != null ? glm.postRoundDelay : 2f);
        }
    }

    bool AllConnectedReady()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return true;
        foreach (var id in nm.ConnectedClientsIds)
            if (!readyClients.Contains(id)) return false;
        return true;
    }

    void MarkReady(ulong clientId) => readyClients.Add(clientId);

    void HandleAreaChanged(ulong clientId, bool inTrack)
    {
        if (inTrack)
        {
            enteredThisRound.Add(clientId);
            inTrackNow.Add(clientId);
        }
        else
        {
            inTrackNow.Remove(clientId);
        }
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;
        readyClients.Remove(clientId);
        inTrackNow.Remove(clientId);   // a vanished racer can't hold the round open
    }

    // -------------------------------------------------------
    //  Messaging
    // -------------------------------------------------------

    void RegisterMessageHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[MultiplayerWorld] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgRoundStart, (sender, reader) =>
        {
            reader.ReadValueSafe(out int round);
            reader.ReadValueSafe(out int seed);
            ApplyRoundStart(round, seed);
        });
        msg.RegisterNamedMessageHandler(MsgRoundEnd, (sender, reader) =>
        {
            reader.ReadValueSafe(out byte reason);
            ApplyRoundEnd(reason);
        });
        msg.RegisterNamedMessageHandler(MsgReady, (sender, reader) =>
        {
            reader.ReadValueSafe(out byte _);
            if (IsServer) MarkReady(sender);
        });
        msg.RegisterNamedMessageHandler(MsgArea, (sender, reader) =>
        {
            reader.ReadValueSafe(out bool inTrack);
            if (IsServer) HandleAreaChanged(sender, inTrack);
        });
    }

    void UnregisterMessageHandlers()
    {
        var msg = Msg;
        if (msg == null) return;
        msg.UnregisterNamedMessageHandler(MsgRoundStart);
        msg.UnregisterNamedMessageHandler(MsgRoundEnd);
        msg.UnregisterNamedMessageHandler(MsgReady);
        msg.UnregisterNamedMessageHandler(MsgArea);
    }

    void BroadcastRoundStart(int round, int seed)
    {
        using (var writer = new FastBufferWriter(sizeof(int) * 2, Allocator.Temp))
        {
            writer.WriteValueSafe(round);
            writer.WriteValueSafe(seed);
            SendToRemoteClients(MsgRoundStart, writer);
        }
        ApplyRoundStart(round, seed);   // the host applies directly rather than relying on loopback
    }

    void BroadcastRoundEnd(byte reason)
    {
        using (var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp))
        {
            writer.WriteValueSafe(reason);
            SendToRemoteClients(MsgRoundEnd, writer);
        }
        ApplyRoundEnd(reason);
    }

    void SendReadyToServer()
    {
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        msg.SendNamedMessage(MsgReady, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    void SendAreaToServer(bool inTrack)
    {
        if (IsServer)   // host: no loopback needed, book it directly
        {
            HandleAreaChanged(NetworkManager.Singleton.LocalClientId, inTrack);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
        writer.WriteValueSafe(inTrack);
        msg.SendNamedMessage(MsgArea, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    void SendToRemoteClients(string messageName, FastBufferWriter writer)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        foreach (var id in nm.ConnectedClientsIds)
            if (id != nm.LocalClientId)
                nm.CustomMessagingManager.SendNamedMessage(messageName, id, writer, NetworkDelivery.ReliableSequenced);
    }
}
