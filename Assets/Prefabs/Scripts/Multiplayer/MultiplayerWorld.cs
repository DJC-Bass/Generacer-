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
    /// the LRA abort gates on it.</summary>
    public bool InTrackLocally => inTrackLocally;

    /// <summary>True when the local player is racing, in EITHER mode. Multiplayer presence is
    /// per-player (the shared world's phase never reaches InTrack, since the server holds it at
    /// HubPortalActive for the whole round), while single-player really does load the track as a
    /// scene — so the two have to be asked different questions. One definition, so callers that need
    /// "am I on the track right now" cannot drift apart.</summary>
    public static bool LocalInTrackArea =>
        IsMultiplayerGame
            ? Instance.inTrackLocally
            : GameLoopManager.Instance != null
              && GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.InTrack;

    /// <summary>Show the TRACK's world to a player whose CAR is still in the hub.
    ///
    /// Exists for the Support Ship pilot, who stands on a hub pad flying a ship ~100 km away: their
    /// camera is out there, so they should get the track's lighting and music rather than the hub's
    /// they are physically standing in. Their car really is still in the hub, so this cannot be done
    /// by moving them — it is a PRESENTATION lie, and deliberately a narrow one.
    ///
    /// It covers lights and music ONLY. Notably it does NOT call SetActiveScene: that would send every
    /// object the hub instantiates from here on into the track scene, to be destroyed with it at the
    /// end of the round. The sky is handled per-CAMERA by PilotControlCenter for the same reason —
    /// a targeted override beats moving the whole machine's idea of where it is.</summary>
    public static void SetPilotPresentation(bool showTrack)
    {
        if (pilotPresentation == showTrack) return;
        pilotPresentation = showTrack;
        AudioManager.MusicSceneOverride = showTrack ? "TrackScene" : null;
        if (Instance != null) Instance.ApplyAreaPresentation();
        else AudioManager.RefreshSceneMusic();   // single-player: no area machinery, music still applies
    }
    private static bool pilotPresentation;

    /// <summary>True on a multiplayer CLIENT that is not the host. The AI/obstacle spawners gate on
    /// this (Phase 5): the host runs the one real simulation, clients render replicated puppets.</summary>
    public static bool IsClientOnly =>
        IsMultiplayerGame && NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer;

    /// <summary>Server: is anyone racing right now? Host-side obstacle spawners idle when nobody is.</summary>
    public static bool AnyPlayerInTrackServer =>
        IsMultiplayerGame && Instance.inTrackNow.Count > 0;

    /// <summary>Where the track area lives relative to the hub. -35 km keeps the whole generated
    /// track (which spans up to ~28 km forward from its origin) clear of the hub while staying inside
    /// the float-precision envelope the single-player tracks already occupy (~30 km ⇒ mm-scale).</summary>
    public static readonly Vector3 TrackAreaOffset = new Vector3(0f, 0f, -100000f);

    /// <summary>The server-rolled seed for the current round (0 between rounds). Every per-round
    /// random decision on every client must derive from this (see <see cref="DeriveRandom"/>).</summary>
    public static int CurrentRoundSeed { get; private set; }

    // -------------------------------------------------------
    //  Sticky entity targeting (Phase 5, HOST-side): an entity that targets "the player" picks ONE
    //  random player and keeps it for its whole lifespan; it retargets only when the target ceases
    //  to be valid (left the track / disconnected), per the design decision.
    // -------------------------------------------------------

    /// <summary>Picks a random player car for an entity to target and STICK with. On the multiplayer
    /// host: a random player currently in the track (`anyArea` widens to everyone — the hub ending
    /// swarm hunts both teams). Single-player: the local car, unchanged behaviour.</summary>
    public static Transform PickStickyTarget(bool anyArea, bool preferAirborne = false)
    {
        if (!IsMultiplayerGame)
        {
            var localCar = PlayerRegistry.LocalCar;
            return localCar != null ? localCar.transform : null;
        }

        var self = Instance;
        var nm = NetworkManager.Singleton;
        var pool = new List<Transform>();

        var local = PlayerRegistry.LocalCar;
        if (local != null && (anyArea || (nm != null && self.inTrackNow.Contains(nm.LocalClientId))))
            pool.Add(local.transform);
        foreach (var remote in PlayerRegistry.Remotes)
            if (remote.Car != null && (anyArea || self.inTrackNow.Contains(remote.ClientId)))
                pool.Add(remote.Car.transform);

        if (pool.Count == 0) return null;

        // Anti-air hunters (lava boulders) narrow the draw to whoever is actually in the air, then pick
        // at RANDOM among them and stick - so with two players jumping, each is equally likely to be the
        // one chased, rather than the host always being it. With nobody airborne the full pool stands,
        // which keeps a boulder shower falling on a grounded field exactly as it always did.
        if (preferAirborne)
        {
            var airborne = new List<Transform>();
            foreach (var t in pool)
                if (IsPlayerAirborne(t)) airborne.Add(t);
            if (airborne.Count > 0) pool = airborne;
        }

        return pool[UnityEngine.Random.Range(0, pool.Count)];
    }

    /// <summary>Is this player car off the ground? Works for ANY player - the local car answers from its
    /// own CarController, a remote player from bit 6 of the replicated state byte.
    ///
    /// ⚠️ Every host-side hunter must ask THIS rather than reaching for a CarController. Remote players
    /// are stripped puppets and have none, so a direct GetComponent returns null and the entity quietly
    /// concludes "not airborne" - which is how lava boulders came to ignore airborne clients entirely
    /// while hounding an airborne host. The failure is silent and looks like a tuning problem.</summary>
    public static bool IsPlayerAirborne(Transform playerRoot)
    {
        if (playerRoot == null) return false;

        var puppet = playerRoot.GetComponentInParent<RemoteCarPuppet>();
        if (puppet != null) return puppet.Airborne;

        var car = playerRoot.GetComponentInParent<CarController>();
        return car != null && car.IsAirborne;
    }

    /// <summary>Returns the target unchanged while it's still valid; null once its player left the
    /// pool (left the track for track-scoped entities, or disconnected — the puppet is destroyed),
    /// telling the entity to retarget (or idle if the pool is empty).</summary>
    public static Transform ValidateStickyTarget(Transform target, bool anyArea)
    {
        if (target == null) return null;
        if (!IsMultiplayerGame) return target;

        var self = Instance;
        var nm = NetworkManager.Singleton;

        var local = PlayerRegistry.LocalCar;
        if (local != null && target == local.transform)
            return (anyArea || (nm != null && self.inTrackNow.Contains(nm.LocalClientId))) ? target : null;

        foreach (var remote in PlayerRegistry.Remotes)
            if (remote.Car != null && target == remote.Car.transform)
                return (anyArea || self.inTrackNow.Contains(remote.ClientId)) ? target : null;

        return null;   // owner gone (disconnect destroyed the puppet)
    }

    /// <summary>Resolves which player's car a collider belongs to: the LOCAL player, a remote player
    /// (outs their clientId), or neither. Used for bounty attribution on the host.</summary>
    public static bool TryGetCarOwner(Transform hit, out ulong clientId, out bool isLocalPlayer)
    {
        clientId = 0;
        isLocalPlayer = false;
        if (hit == null) return false;

        var local = PlayerRegistry.LocalCar;
        if (local != null && (hit == local.transform || hit.IsChildOf(local.transform)))
        {
            isLocalPlayer = true;
            return true;
        }
        foreach (var remote in PlayerRegistry.Remotes)
        {
            if (remote.Car == null) continue;
            if (hit == remote.Car.transform || hit.IsChildOf(remote.Car.transform))
            {
                clientId = remote.ClientId;
                return true;
            }
        }
        return false;
    }

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
    const string MsgRoundStart = "GNRC_ROUND_START"; // PRELOAD: {round, seed, live, remaining} — load + freeze
    const string MsgRoundGo = "GNRC_ROUND_GO";       // GO: {remaining} — portal spawns, track unfreezes, timers start
    const string MsgRoundEnd = "GNRC_ROUND_END";     // {reason: 0 timeout, 1 all racers left}
    const string MsgArea = "GNRC_AREA";              // client → server: {inTrack}
    const string MsgRacerFin = "GNRC_RACER_FIN";     // an AI racer crossed the finish — first place forfeit
    const string MsgToLobby = "GNRC_TO_LOBBY";       // server → all: the HOST left the world, so the run is over

    const string MainMenuSceneName = "MainMenu";

    // ---- Local state ----
    private bool begun;
    private Scene hubScene;
    private Scene trackScene;
    private bool hubCaptured;
    private Vector3 hubSpawnPos;
    private Quaternion hubSpawnRot;
    private bool inTrackLocally;
    private bool roundActive;        // the round is LIVE (portal up, timers running)
    private bool roundLoaded;        // the track is (pre)loaded for this round — set before roundActive
    private bool loadingTrack;       // the async load/generation is still in flight
    private float pendingGoRemaining = -1f;   // a GO arrived mid-load — apply when the load settles
    private int roundNumber;
    private bool teleporting;

    // ---- Loading screen ("ROADING") ----
    private GameObject loadingCanvas;
    private RectTransform loadingFill;

    /// <summary>True between a round's PRELOAD and its GO: the track exists but is FROZEN — host AI
    /// (DroneCar) holds still and no round/AI timing advances until the hub portal spawns.</summary>
    public static bool TrackFrozen { get; private set; }

    /// <summary>True once this round's track is (pre)loaded locally — the spawners' cue to do their
    /// heavy spawning early (frozen) instead of at portal time.</summary>
    public static bool RoundLoadedLocally => Instance != null && Instance.roundLoaded;
    private GameObject trackSpeedometerRoot;   // TrackScene's own speed HUD — shown only while in the track area
    private readonly List<(Light light, bool wasEnabled)> hubLights = new List<(Light, bool)>();
    private readonly List<(Light light, bool wasEnabled)> trackLights = new List<(Light, bool)>();

    // ---- Server state ----
    private readonly HashSet<ulong> readyClients = new HashSet<ulong>();
    private readonly HashSet<ulong> enteredThisRound = new HashSet<ulong>();
    private readonly HashSet<ulong> inTrackNow = new HashSet<ulong>();
    private bool gameLoopStarted;        // full room reached once — rounds are running
    private float serverRoundRemaining;  // current round's time left (for syncing mid-game joiners)

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
        if (Instance == this)
        {
            Instance = null;
            TrackFrozen = false;   // never leak a freeze into single-player / the next session
        }
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

        // Phase 5: host-simulated AI/obstacles streamed to clients as puppets.
        gameObject.AddComponent<NpcReplicator>();

        // Grappling hook: streams each player's rope so others can see it, and routes a reel on a
        // remote player's car to the machine that owns it.
        gameObject.AddComponent<GrappleReplicator>();

        // Support Ship: whose ship is out, where its hub pilot is holding it, and the server's verdict
        // on when one has been downed.
        gameObject.AddComponent<SupportShipReplicator>();

        // Voice chat via Vivox: proximity (positional 3D) + LB team-direct (2D). The persistent,
        // self-bootstrapped VoiceService logs in and joins THIS match's channels; EndMatch on teardown.
        VoiceService.BeginMatch();

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
        ReleaseWorld();

        Debug.Log($"[MultiplayerWorld] Teardown to menu: {reason}");
        SceneManager.LoadScene(MainMenuSceneName);   // single-mode load also clears the additive track
        Destroy(gameObject);
    }

    /// <summary>Leave the WORLD but stay in the SESSION, landing back in the lobby room.
    ///
    /// The game-over exit, and the hub's MAIN MENU pad, in multiplayer. Everything about the run is
    /// torn down exactly as TeardownToMenu does it — the same method does the work — but the session,
    /// the roster and every player's team assignment survive, so the room the player lands in is the
    /// one they started from and the host can run it again without anyone renavigating the menus.
    ///
    /// ⚠️ It must NOT call LeaveSessionAsync. That is the QUIT path, and for a HOST it DELETES the
    /// session for everyone — which is exactly the behaviour we are moving away from. Losing is not
    /// quitting.</summary>
    public void TeardownToLobby(string reason)
    {
        if (!begun) return;

        // ⚠️ THE HOST LEAVING TAKES EVERYONE WITH THEM, and this is not a courtesy — it is the only
        // correct behaviour. The host runs the entire simulation: every drone, boulder, round timer and
        // projectile. The moment they leave the world, the survivors' hub FREEZES — the drones stop
        // moving, so nothing can shoot them, so nothing can ever send them to the lobby either. They
        // were stranded in a dead world with no way out.
        //
        // Sent BEFORE ReleaseWorld, which unregisters the handlers this rides on. NGO itself stays up
        // (only LeaveSessionAsync shuts it down), so the message reaches everyone and they land in the
        // same room we do.
        if (IsServer) BroadcastToLobby();

        ReleaseWorld();

        // Told BEFORE the scene load, so the host's auto-launch hook is already suppressed by the time
        // the menu comes up and re-evaluates it — otherwise it drags them straight back in.
        if (NetworkSessionManager.Instance != null) NetworkSessionManager.Instance.ReturnedToLobby();
        MainMenuController.OpenLobbyOnLoad = true;

        Debug.Log($"[MultiplayerWorld] Teardown to lobby: {reason}");
        SceneManager.LoadScene(MainMenuSceneName);
        Destroy(gameObject);
    }

    /// <summary>Everything both exits must undo: the run, the inventory, the voice channels, the
    /// message handlers and every static this world set. Shared so the two cannot drift — a leak here
    /// is the kind that only shows up as "the SECOND game of a session is broken".</summary>
    void ReleaseWorld()
    {
        begun = false;

        VoiceService.EndMatch();   // leave Vivox voice channels + log out
        StopAllCoroutines();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        UnregisterMessageHandlers();
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

        CurrentRoundSeed = 0;
        TrackFrozen = false;
        GameLoopManager.RemoteDriven = false;
        GameLoopManager.RemoteTrackSeed = 0;
        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();
        if (loadingCanvas != null) Destroy(loadingCanvas);
        SetPilotPresentation(false);   // never leave a pilot's borrowed lighting on the menu
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
            // Every client's car was placed at the SAME authored pose — with solid puppets that means
            // spawning inside a teammate. Shift into this player's formation slot immediately.
            car.transform.SetPositionAndRotation(ApplySpawnFormation(hubSpawnPos, hubSpawnRot), hubSpawnRot);
            ClearInterpolationHistory(car.GetComponent<Rigidbody>());   // placed, not driven, into the slot
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
        TeleportCar(rb, car.transform,
            ApplySpawnFormation(generator.CarSpawnPosition, generator.CarSpawnRotation),
            generator.CarSpawnRotation);

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
            TeleportCar(rb, car.transform, ApplySpawnFormation(hubSpawnPos, hubSpawnRot), hubSpawnRot);
        }

        inTrackLocally = false;
        ApplyAreaPresentation();
        if (car != null) AudioManager.PlayPortalExit(car.transform);
        // (Phase 5 note: boulders are now HOST-simulated for everyone — a returning player must NOT
        // destroy them; the old per-client boulder cleanup that lived here is gone deliberately.)
        if (notifyServer) SendAreaToServer(false);
    }

    /// <summary>Teleports the local car cleanly across the ~35 km area jump. `SyncTransforms` pushes the
    /// new pose into the physics engine immediately, so THIS frame's suspension raycasts and camera reads
    /// sample the destination rather than the old spot. The interpolation toggle is a safety net: IF a car
    /// rigidbody is ever set to Interpolate, a plain transform set leaves interpolation's pose-history
    /// behind and the car render-smears across the jump (reads as "kept falling"); toggling off→on clears
    /// that history. Player cars currently use interpolation NONE, so that branch simply no-ops.</summary>
    /// <summary>Tells the physics interpolator that the pose it is holding is obsolete, after a body has
    /// been PLACED somewhere rather than having travelled there. Call it right after any direct
    /// transform write on an interpolated Rigidbody.
    ///
    /// Interpolation renders a body BETWEEN its last two physics poses. A plain transform write leaves
    /// the older of those back at the departure point, so for a frame or two the mesh render-slides
    /// across the gap — the object appears to streak to its new home rather than appear there. Over the
    /// 100 km area jump that is a hyperspeed smear across the map; over a drone's few-metre path
    /// recovery it is a flicker. Toggling interpolation off and straight back on discards the history.
    ///
    /// Lives here because this is where the trick was first needed (the area teleport), but it is not
    /// multiplayer-specific: the single-player track spawn and the rewind use it too. Cheap, and a
    /// no-op on a body whose interpolation is off, so it is safe to call unconditionally.</summary>
    public static void ClearInterpolationHistory(Rigidbody rb)
    {
        if (rb == null || rb.interpolation == RigidbodyInterpolation.None) return;
        var mode = rb.interpolation;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.interpolation = mode;
    }

    static void TeleportCar(Rigidbody rb, Transform car, Vector3 pos, Quaternion rot)
    {
        car.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
        ClearInterpolationHistory(rb);
    }

    /// <summary>Since puppets are SOLID (kinematic colliders), players can't all be placed on the
    /// exact same spawn pose — the local car would materialise inside a teammate. Every placement
    /// (hub capture, portal entry, hub return) offsets by this per-player formation slot: 3 abreast,
    /// then rows behind, keyed by this client's stable index among the connected ids.</summary>
    Vector3 ApplySpawnFormation(Vector3 basePos, Quaternion baseRot)
    {
        var nm = NetworkManager.Singleton;
        int index = 0;
        if (nm != null)
        {
            var ids = new List<ulong>(nm.ConnectedClientsIds);
            ids.Sort();
            index = Mathf.Max(0, ids.IndexOf(nm.LocalClientId));
        }
        float lateral = ((index % 3) - 1) * 5f;
        float back = (index / 3) * 9f;
        return basePos + baseRot * new Vector3(lateral, 0f, -back);
    }

    /// <summary>Everything cosmetic that follows the LOCAL player's area: active scene (RenderSettings
    /// skybox/fog + scene-keyed music), per-area directional lights, the track speed HUD, and a camera
    /// snap so the follow rigs don't swoosh 35 km across the world.</summary>
    void ApplyAreaPresentation()
    {
        // Keyed on the CAR, never on the pilot override: the active scene decides where newly
        // instantiated objects land, and a hub player must keep spawning things into the hub.
        var scene = inTrackLocally ? trackScene : hubScene;
        if (scene.IsValid() && scene.isLoaded)
            SceneManager.SetActiveScene(scene);   // fires activeSceneChanged → SkyboxHueRandomizer recolors

        ApplyAreaLights();

        // The speedometer is deliberately keyed on the CAR, not on the pilot override: it reads the
        // pilot's own parked car, so showing it would just report a stationary 0 mph over someone
        // else's race.
        if (trackSpeedometerRoot != null) trackSpeedometerRoot.SetActive(inTrackLocally);

        AudioManager.RefreshSceneMusic();
        SnapFollowCameras();
    }

    /// <summary>Lights the world for wherever this player currently IS — or, for a Support Ship pilot,
    /// wherever they are LOOKING. A blackout round still reads as one either way, because SetAreaLights
    /// restores each light's RECORDED state rather than forcing it on.</summary>
    void ApplyAreaLights()
    {
        bool showTrack = inTrackLocally || pilotPresentation;
        SetAreaLights(hubLights, !showTrack);
        SetAreaLights(trackLights, showTrack);
    }

    /// <summary>Light the TRACK for the duration of ONE camera's render, then <see cref="PopTrackLighting"/>.
    ///
    /// For the hub Spectator TVs, which are a harder case than the Support Ship pilot: a pilot's whole
    /// screen is the track, so they can swap the world's lighting for as long as they fly. A TV viewer
    /// is looking at the hub AND at a screen showing the track in the SAME frame, and lights are global
    /// — there is one set of enabled lights per frame, not one per camera. The only way to give two
    /// cameras different lighting is to change it between them, which is what these two do, driven from
    /// URP's per-camera render callbacks.
    ///
    /// Both are no-ops outside a multiplayer session, where there is no track to light.</summary>
    public static void PushTrackLighting()
    {
        if (Instance == null) return;
        SetAreaLights(Instance.hubLights, false);
        SetAreaLights(Instance.trackLights, true);
    }

    /// <summary>Undoes <see cref="PushTrackLighting"/> by re-deriving the correct state rather than
    /// restoring a snapshot — so it stays right even if the player's area or the pilot override changed
    /// in between.</summary>
    public static void PopTrackLighting()
    {
        if (Instance != null) Instance.ApplyAreaLights();
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

    /// <summary>PRELOAD: load + generate the track NOW (behind the ROADING screen, during the hub
    /// countdown — the least-crucial moment) and keep it FROZEN. The heavy work — scene load, track
    /// generation, the zero-delay drone groups — all lands here instead of at portal-spawn time, so
    /// the portal/boost-gate appear later without a lag spike. `live` = a mid-game joiner being
    /// synced into an already-running round: unfreeze immediately after the load with the true
    /// remaining time.</summary>
    void ApplyRoundPreload(int round, int seed, bool live, float remaining)
    {
        if (!begun || roundLoaded || roundActive) return;
        var glm = GameLoopManager.Instance;
        if (glm != null && glm.GameEnded) return;   // an ending has taken over — no more rounds

        roundNumber = round;
        roundLoaded = true;
        TrackFrozen = true;
        pendingGoRemaining = live ? remaining : -1f;
        CurrentRoundSeed = seed;
        GameLoopManager.RemoteTrackSeed = seed;
        if (glm != null) glm.RemotePrepareRound(round, glm.roundDuration);   // state set, timers held

        Debug.Log($"[MultiplayerWorld] Round {round} preloading (seed {seed}) — track generates now, frozen until the portal.");
        StartCoroutine(LoadTrackRoutine(glm != null ? glm.trackSceneName : "TrackScene"));
    }

    IEnumerator LoadTrackRoutine(string trackName)
    {
        loadingTrack = true;
        ShowLoadingScreen();

        var op = SceneManager.LoadSceneAsync(trackName, LoadSceneMode.Additive);
        while (op != null && !op.isDone)
        {
            SetLoadingProgress(op.progress / 0.9f * 0.85f);   // async load maps to 0..85%
            yield return null;
        }

        // Generation + the zero-delay spawner bursts run in the next frames' Start/Update — the bar
        // rides through them so the (single-frame) generation hitch happens behind the screen.
        SetLoadingProgress(0.9f);
        yield return null;
        yield return null;
        SetLoadingProgress(0.97f);
        yield return null;
        SetLoadingProgress(1f);
        yield return null;

        HideLoadingScreen();
        loadingTrack = false;

        // A GO (or live mid-join sync) arrived while we were loading — apply it now.
        if (pendingGoRemaining >= 0f)
        {
            float remaining = pendingGoRemaining;
            pendingGoRemaining = -1f;
            ApplyRoundGo(remaining);
        }
    }

    /// <summary>GO: the hub countdown ended — the portal/boost gate spawn (no load hitch: the track
    /// already exists), the track UNFREEZES, and the round + AI timers officially start.</summary>
    void ApplyRoundGo(float remaining)
    {
        if (!begun || !roundLoaded || roundActive) return;
        if (loadingTrack) { pendingGoRemaining = remaining; return; }   // apply the moment the load settles

        roundActive = true;
        TrackFrozen = false;
        var glm = GameLoopManager.Instance;
        if (glm != null) glm.RemoteBeginRound(roundNumber, remaining);   // fires OnPortalShouldSpawn
        Debug.Log($"[MultiplayerWorld] Round {roundNumber} LIVE — portal up, {remaining:F0}s on the clock.");
    }

    // -------------------------------------------------------
    //  "ROADING" loading screen (code-built overlay + progress bar)
    // -------------------------------------------------------

    void EnsureLoadingScreen()
    {
        if (loadingCanvas != null) return;

        loadingCanvas = new GameObject("RoadingCanvas");
        DontDestroyOnLoad(loadingCanvas);
        var canvas = loadingCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;   // above everything, including the victory banner (400)
        var scaler = loadingCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var bg = new GameObject("Background", typeof(RectTransform));
        bg.transform.SetParent(loadingCanvas.transform, false);
        var bgImage = bg.AddComponent<UnityEngine.UI.Image>();
        bgImage.color = new Color(0.02f, 0.03f, 0.05f, 0.97f);
        var bgrt = bgImage.rectTransform;
        bgrt.anchorMin = Vector2.zero; bgrt.anchorMax = Vector2.one;
        bgrt.offsetMin = Vector2.zero; bgrt.offsetMax = Vector2.zero;

        var title = new GameObject("Title", typeof(RectTransform));
        title.transform.SetParent(loadingCanvas.transform, false);
        var titleText = title.AddComponent<TMPro.TextMeshProUGUI>();
        titleText.text = "ROADING";
        titleText.fontSize = 130f;
        titleText.fontStyle = TMPro.FontStyles.Bold;
        titleText.alignment = TMPro.TextAlignmentOptions.Center;
        titleText.color = Color.white;
        var trt = titleText.rectTransform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
        trt.sizeDelta = new Vector2(1200f, 200f);
        trt.anchoredPosition = new Vector2(0f, 60f);

        var barBack = new GameObject("BarBack", typeof(RectTransform));
        barBack.transform.SetParent(loadingCanvas.transform, false);
        var barBackImage = barBack.AddComponent<UnityEngine.UI.Image>();
        barBackImage.color = new Color(1f, 1f, 1f, 0.12f);
        var brt = barBackImage.rectTransform;
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
        brt.sizeDelta = new Vector2(820f, 26f);
        brt.anchoredPosition = new Vector2(0f, -90f);

        var fill = new GameObject("Fill", typeof(RectTransform));
        fill.transform.SetParent(barBack.transform, false);
        var fillImage = fill.AddComponent<UnityEngine.UI.Image>();
        fillImage.color = new Color(0.90f, 0.45f, 0.12f, 1f);   // the menus' orange accent
        loadingFill = fillImage.rectTransform;
        loadingFill.anchorMin = new Vector2(0f, 0f);
        loadingFill.anchorMax = new Vector2(0f, 1f);   // anchorMax.x is driven by progress
        loadingFill.offsetMin = Vector2.zero;
        loadingFill.offsetMax = Vector2.zero;
    }

    void ShowLoadingScreen()
    {
        EnsureLoadingScreen();
        SetLoadingProgress(0f);
        loadingCanvas.SetActive(true);
    }

    void SetLoadingProgress(float t)
    {
        if (loadingFill != null)
            loadingFill.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
    }

    void HideLoadingScreen()
    {
        if (loadingCanvas != null) loadingCanvas.SetActive(false);
    }

    void ApplyRoundEnd(byte reason)
    {
        if (!begun || (!roundActive && !roundLoaded)) return;
        roundActive = false;
        roundLoaded = false;
        TrackFrozen = false;
        pendingGoRemaining = -1f;
        loadingTrack = false;
        HideLoadingScreen();

        if (inTrackLocally) ReturnToHubLocally(notifyServer: false);

        var glm = GameLoopManager.Instance;
        if (glm != null) glm.RemoteEndRound();   // fires OnPortalShouldDespawn

        // Nobody can be presenting a track that is about to stop existing. The pilot pad releases its
        // own controls a moment later anyway, but the ORDER matters: this restores the hub's lights
        // while trackLights is still populated, whereas doing it after the Clear() below would leave a
        // pilot who was still flying at the bell standing in an unlit hub with no way back.
        SetPilotPresentation(false);

        if (trackScene.IsValid() && trackScene.isLoaded)
            SceneManager.UnloadSceneAsync(trackScene);
        trackLights.Clear();
        trackSpeedometerRoot = null;
        NpcReplicator.ClearRoundPuppets();   // round-scoped NPC puppets die with the round

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

        // Shut the door on the lobby at the same moment. From here the Drone swarm picks players off
        // ONE AT A TIME, and each casualty lands back in the lobby room while their teammates are still
        // being hunted — so without this the first player out would see ENTER GAME and walk straight
        // back into the massacre. Host-only and idempotent; cleared when the host starts the next run.
        if (NetworkSessionManager.Instance != null) NetworkSessionManager.Instance.FlagRunEnding();

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
        // The game loop does NOT begin until EVERY seat the lobby allows (2 × team size) is filled
        // AND all of those players have entered the hub — each on their own accord via ENTER GAME
        // (the host is just the first one in). Two teams of one ⇒ wait for the 2nd player; two teams
        // of two ⇒ wait for all four. No timeout: waiting for the room IS the design.
        int capacity;
        while (begun)
        {
            capacity = SessionCapacity();
            if (hubCaptured && capacity > 0 && readyClients.Count >= capacity) break;
            yield return new WaitForSeconds(0.5f);
        }
        if (!begun) yield break;

        // Full room: LOCK the lobby and begin the rounds. (A mid-game leave unlocks it again so a
        // replacement can join; MarkReady re-locks when the room refills.)
        gameLoopStarted = true;
        if (NetworkSessionManager.Instance != null)
            _ = NetworkSessionManager.Instance.SetSessionLockedAsync(true);
        Debug.Log("[MultiplayerWorld] Full room in the hub — the game loop begins.");

        // Phase 6: assign every player their RIVAL from the opposing team, now that all seats are in.
        var scoringForRivals = GetComponent<MultiplayerScoring>();
        if (scoringForRivals != null) scoringForRivals.AssignRivalsServer();

        var glm = GameLoopManager.Instance;
        while (begun)
        {
            // PRELOAD first: the track loads/generates on every machine NOW (behind the ROADING
            // screen, frozen) — then the hub countdown runs, comfortably covering the load. The
            // portal/boost gate spawn at GO with zero load hitch.
            int seed = UnityEngine.Random.Range(1, 999999);
            enteredThisRound.Clear();
            inTrackNow.Clear();
            var scoring = GetComponent<MultiplayerScoring>();
            if (scoring != null) scoring.ResetRoundServer();   // fresh first-place claim
            BroadcastRoundPreload(roundNumber + 1, seed);

            float countdown = glm != null
                ? UnityEngine.Random.Range(glm.minPortalCountdown, glm.maxPortalCountdown)
                : UnityEngine.Random.Range(10f, 20f);
            yield return new WaitForSeconds(countdown);

            BroadcastRoundGo();

            // Round runs for the configured duration, or ends early once at least one player raced
            // and every player who entered the track has left it (per the multiplayer design).
            serverRoundRemaining = glm != null ? glm.roundDuration : 300f;
            bool allLeft = false;
            bool racerFinBroadcast = false;
            while (serverRoundRemaining > 0f)
            {
                yield return new WaitForSeconds(0.5f);
                serverRoundRemaining -= 0.5f;

                // Phase 5: the host's sim is THE AI truth — replicate "an AI finished first" so every
                // client's first-place verdict (credits + finish reports) matches the server's.
                if (!racerFinBroadcast && glm != null && glm.AnyRacerFinishedAhead)
                {
                    racerFinBroadcast = true;
                    BroadcastRacerFinished();
                }

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

    /// <summary>The lobby's full seat count (2 × team size). 0 while the session is unknown.</summary>
    int SessionCapacity()
    {
        var session = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.Session : null;
        return session != null ? session.MaxPlayers : 0;
    }

    void MarkReady(ulong clientId)
    {
        readyClients.Add(clientId);
        if (!gameLoopStarted) return;   // pre-start: the round loop's own wait watches the count

        // Mid-game joiner (a freed seat was refilled): catch them up on the round in progress so
        // they get the portal + the same track instead of idling until the next round…
        if (roundActive)
            SendRoundPreloadTo(clientId, roundNumber, CurrentRoundSeed, live: true, serverRoundRemaining);
        else if (roundLoaded)
            SendRoundPreloadTo(clientId, roundNumber, CurrentRoundSeed, live: false, 0f);

        // …give them the vacant RIVAL slot (they inherit the leaver's rival; the leaver's orphaned
        // opponents get them)…
        var scoring = GetComponent<MultiplayerScoring>();
        if (scoring != null) scoring.HandleJoinerServer(clientId);

        // …and re-LOCK the lobby once every seat is filled again.
        int capacity = SessionCapacity();
        if (capacity > 0 && readyClients.Count >= capacity && NetworkSessionManager.Instance != null)
            _ = NetworkSessionManager.Instance.SetSessionLockedAsync(true);
    }

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

        // A seat freed up mid-game: UNLOCK the lobby so a replacement can join and enter the hub
        // (MarkReady re-locks once the room is full again). The rounds keep running meanwhile.
        if (gameLoopStarted && begun && NetworkSessionManager.Instance != null)
            _ = NetworkSessionManager.Instance.SetSessionLockedAsync(false);
    }

    // -------------------------------------------------------
    //  Messaging
    // -------------------------------------------------------

    void RegisterMessageHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[MultiplayerWorld] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgToLobby, (sender, reader) =>
        {
            // The host has left the world. There is no world without them — they simulate every drone,
            // boulder and round timer — so come back to the lobby rather than sit in a frozen hub.
            if (!IsServer) TeardownToLobby("THE HOST ENDED THE RUN");
        });

        msg.RegisterNamedMessageHandler(MsgRoundStart, (sender, reader) =>
        {
            reader.ReadValueSafe(out int round);
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out bool live);
            reader.ReadValueSafe(out float remaining);
            ApplyRoundPreload(round, seed, live, remaining);
        });
        msg.RegisterNamedMessageHandler(MsgRoundGo, (sender, reader) =>
        {
            reader.ReadValueSafe(out float remaining);
            ApplyRoundGo(remaining);
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
        msg.RegisterNamedMessageHandler(MsgRacerFin, (sender, reader) =>
        {
            reader.ReadValueSafe(out byte _);
            // The host's sim says an AI racer beat everyone — mirror into the local puppet manager
            // so this client's own first-place verdict matches the server's.
            if (GameLoopManager.Instance != null) GameLoopManager.Instance.NotifyRacerFinished();
        });
    }

    void BroadcastRacerFinished()
    {
        using var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        SendToRemoteClients(MsgRacerFin, writer);
    }

    void UnregisterMessageHandlers()
    {
        var msg = Msg;
        if (msg == null) return;
        msg.UnregisterNamedMessageHandler(MsgRoundStart);
        msg.UnregisterNamedMessageHandler(MsgRoundGo);
        msg.UnregisterNamedMessageHandler(MsgRoundEnd);
        msg.UnregisterNamedMessageHandler(MsgReady);
        msg.UnregisterNamedMessageHandler(MsgArea);
        msg.UnregisterNamedMessageHandler(MsgRacerFin);
        msg.UnregisterNamedMessageHandler(MsgToLobby);
    }

    /// <summary>HOST → everyone: abandon the world and regroup in the lobby. Carries no payload — the
    /// fact that it arrived IS the message.</summary>
    void BroadcastToLobby()
    {
        using var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        SendToRemoteClients(MsgToLobby, writer);
    }

    void BroadcastRoundPreload(int round, int seed)
    {
        using (var writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(bool) + sizeof(float), Allocator.Temp))
        {
            writer.WriteValueSafe(round);
            writer.WriteValueSafe(seed);
            writer.WriteValueSafe(false);   // not live — load + freeze until GO
            writer.WriteValueSafe(0f);
            SendToRemoteClients(MsgRoundStart, writer);
        }
        ApplyRoundPreload(round, seed, live: false, remaining: 0f);   // host applies directly
    }

    void BroadcastRoundGo()
    {
        var glm = GameLoopManager.Instance;
        float remaining = glm != null ? glm.roundDuration : 300f;
        using (var writer = new FastBufferWriter(sizeof(float), Allocator.Temp))
        {
            writer.WriteValueSafe(remaining);
            SendToRemoteClients(MsgRoundGo, writer);
        }
        ApplyRoundGo(remaining);
    }

    /// <summary>Targeted round sync for a MID-GAME joiner: same round + seed as everyone else, with
    /// `live` set if the round is already running (their track loads, then unfreezes immediately with
    /// the TRUE time remaining so their timer matches).</summary>
    void SendRoundPreloadTo(ulong clientId, int round, int seed, bool live, float remaining)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null || clientId == nm.LocalClientId) return;
        using var writer = new FastBufferWriter(sizeof(int) * 2 + sizeof(bool) + sizeof(float), Allocator.Temp);
        writer.WriteValueSafe(round);
        writer.WriteValueSafe(seed);
        writer.WriteValueSafe(live);
        writer.WriteValueSafe(remaining);
        nm.CustomMessagingManager.SendNamedMessage(MsgRoundStart, clientId, writer, NetworkDelivery.ReliableSequenced);
        Debug.Log($"[MultiplayerWorld] Synced mid-game joiner {clientId} into round {round} " +
                  $"({(live ? $"live, {remaining:F0}s left" : "preloading")}).");
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
