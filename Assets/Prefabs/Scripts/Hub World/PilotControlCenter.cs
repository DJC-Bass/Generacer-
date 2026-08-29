using System.Collections.Generic;
using UnityEngine.Rendering.Universal;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;

/// <summary>
/// The HUB-world pilot station. Drop this on the PilotControlCenter prefab (the one with the box
/// collider the player drives into) — like the store and the upgrade ramp, the collider must be a
/// trigger, which Reset() sets automatically.
///
/// Driving in opens a list of every TEAMMATE currently flying a Support Ship. Pick one and this
/// machine takes the controls of that ship: the view cuts to a chase camera riding it, the left stick
/// slides it around a 3D movement box (B/X for forward/back) and angles it to aim, the TRIGGERS roll it,
/// A fires, and the pilot’s own hub car is input-locked so it can’t wander off. SELECT (or being shoved
/// off the pad) hands the ship back.
///
/// The pilot flies a LOCAL copy of the ship — the same trick <see cref="HubSpectatorTV"/> uses to show
/// a racer on a hub screen. A remote player's actual camera can't cross the network, but the world is
/// generated identically on every machine, so a chase camera stood up here and pointed at our own copy
/// of their ship is a faithful live view. Only the resulting offset and aim angles go on the wire; see
/// <see cref="SupportShipReplicator"/>.
///
/// The ship being flown belongs to someone in the TrackScene, ~100 km from the hub. Nothing special is
/// needed for the DISTANCE: the camera simply follows an object that far away, and the AudioListener
/// rides with it. What does need saying is that the pad only offers ships whose owner is ACTUALLY
/// RACING — a ship summoned in the hub is listed as IN HUB and cannot be taken, and a pilot already
/// flying one is handed back their car the moment its owner takes the return portal. There is nothing
/// to fly over until someone is out there, and being left pointed at a ship parked on the hub floor is
/// worse than not being offered it.
///
/// Because the pilot's own car stays parked in the hub, the world they are LOOKING at has to be
/// borrowed piece by piece: the sky per-camera (below), and the track's lights and music through
/// MultiplayerWorld.SetPilotPresentation. See there for why this isn't done by switching scenes.
/// </summary>
[RequireComponent(typeof(Collider))]
[DefaultExecutionOrder(1000)]   // see MenuState: run after CarController reads input
public class PilotControlCenter : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Tag on the player car (or any of its colliders).")]
    public string playerTag = "Player";

    [Header("Holding the controls")]
    [Tooltip("How long the pilot's car must be OFF the pad before the controls are handed back. Exists " +
             "so a bump at the edge, or one bad physics frame, can't eject a pilot — being shoved " +
             "properly clear still ends it well inside a second.")]
    public float padExitGrace = 0.5f;

    [Header("Flying")]
    [Range(0f, 0.9f)]
    [Tooltip("Dead zone at the bottom of each ROLL trigger's travel (0..1). Triggers rarely rest at " +
             "exactly zero, and a ship permanently banked a degree or two reads as a broken horizon. " +
             "Above this the response is rescaled, so a full pull is still a full roll.")]
    public float rollDeadzone = 0.06f;

    [Header("Firing (A)")]
    [Tooltip("Rounds one press can produce. Tap A and you get one; hold it and you get this many, then " +
             "the guns stop until you release and press again. Star Fox 64's semi-auto feel.")]
    public int burstRounds = 3;
    [Tooltip("Seconds between the rounds of a held burst. The FIRST round is always instant, so a tap " +
             "never waits on this.")]
    public float burstInterval = 0.12f;

    [Header("Who may be flown")]
    [Tooltip("Only list ships belonging to the local player's OWN team. Off = any active ship in the " +
             "session, which is really only useful for testing.")]
    public bool teamOnly = true;

    [Header("Pilot camera")]
    [Tooltip("Camera offset behind/above the ship, in the ship's level-flight frame.")]
    public Vector3 cameraOffset = new Vector3(0f, 3f, -12f);
    public float lookAheadDistance = 8f;
    public float fieldOfView = 75f;
    public float nearClip = 0.3f;
    [Tooltip("The TrackScene's skybox material (SimpleSkybox). The pilot stands in the HUB, whose sky " +
             "is Unity's default — but they are LOOKING at the track, so the camera is given the " +
             "track's sky per-camera. Leave blank and the pilot sees the hub's sky over the track.")]
    public Material trackSkybox;
    [Tooltip("Edge anti-aliasing for the pilot's view. SMAA matches what the car cameras are authored " +
             "with, so the two views resolve edges the same way; the pilot camera is built in code and " +
             "would otherwise default to None and look noticeably more jagged than a racer's.")]
    public AntialiasingMode antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
    [Tooltip("Quality of the above. Ignored when Antialiasing is None or FXAA.")]
    public AntialiasingQuality antialiasingQuality = AntialiasingQuality.High;
    [Tooltip("Render post-processing on the pilot's view. Keep ON: the game grades through URP's " +
             "DEFAULT VOLUME PROFILE, which applies to any camera with this ticked even though no scene " +
             "contains a Volume — so OFF gives the pilot a raw, ungraded picture. Scene volumes would " +
             "blend here too (URP blends at the CAMERA, which is out in the track area).")]
    public bool enablePostProcessing = true;
    [Tooltip("Far clip — generous, since the ship looks out over a whole generated track.")]
    public float farClip = 20000f;
    [Tooltip("Positional lag between the ship and the point the camera frames, in seconds. The camera " +
             "IGNORES the ship's aim rotation entirely and follows only its position, so this is the " +
             "only softness in the chase. Keep it small — a slight trail, not a drift. 0 = rigid.")]
    public float cameraFollowLag = 0.08f;

    [Header("Row Style")]
    public Color rowNormal = new Color(0f, 0f, 0f, 0.55f);
    public Color rowSelected = new Color(0.35f, 0.9f, 1f, 0.95f);
    public Color rowBusy = new Color(0.25f, 0.1f, 0.1f, 0.7f);
    public Color textNormal = Color.white;
    public Color textSelected = Color.black;

    // ---- detection state ----
    private readonly HashSet<Collider> playerColliders = new HashSet<Collider>();
    private bool PlayerInside => playerColliders.Count > 0;

    // ---- menu state ----
    private bool isOpen;
    private int selected;
    private bool suppressedUntilExit;
    private readonly List<KeyValuePair<ulong, ulong>> available = new List<KeyValuePair<ulong, ulong>>();

    // ---- piloting state ----
    private bool piloting;
    private ulong pilotedOwner;
    private Camera pilotCam;
    private CameraFollow pilotFollow;
    private AudioListener pilotListener;
    private Skybox camSkybox;                  // per-camera sky override (the TRACK's, not the hub's)
    private Material ownedSky;                 // our own recoloured copy, when we had to build one
    private Material suppressedSky;            // the hub's sky, parked while the track's lights the world
    private bool skySwapped;
    private SupportShipCamAnchor camAnchor;    // carries the ship's POSITION and its aim-free frame
    private Camera suppressedCam;              // the hub camera we switched off
    private AudioListener suppressedListener;  // and its listener
    private int burstFired;              // rounds already fired from the CURRENT press of A
    private float nextRoundTime;
    private float padExitSince = -1f;    // when the car left the pad (-1 = it's on it)
    private float shipLostSince = -1f;   // when the flown ship went missing (-1 = it's there)
    private float ownerLeftSince = -1f;  // when the ship's owner stopped racing (-1 = still out there)
    // A remote ship is destroyed and rebuilt whenever its owner's puppet is, so a frame or two of "no
    // ship" is normal and must not eject the pilot.
    private const float ShipLostGrace = 1f;
    // The owner's area rides a 2 Hz heartbeat, so "not racing" can be half a second stale in the
    // ordinary case and longer on a bad connection. Wider than ShipLostGrace for that reason.
    private const float OwnerLeftGrace = 1.5f;

    // ---- built UI ----
    private GameObject root;
    private readonly List<Image> rowBackgrounds = new List<Image>();
    private readonly List<TextMeshProUGUI> rowNames = new List<TextMeshProUGUI>();
    private readonly List<TextMeshProUGUI> rowStatus = new List<TextMeshProUGUI>();
    private TextMeshProUGUI emptyText;
    private TextMeshProUGUI hintText;
    private const int MaxRows = 8;

    void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    // -------------------------------------------------------
    //  Trigger detection
    // -------------------------------------------------------

    void OnTriggerEnter(Collider other)
    {
        if (IsPlayer(other)) playerColliders.Add(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (playerColliders.Remove(other) && !PlayerInside)
        {
            suppressedUntilExit = false;
            if (isOpen) Close();
            // Piloting is deliberately NOT ended here — a single spurious exit event must not eject a
            // pilot mid-flight. See TickPadOccupancy, which confirms it against the pad's actual bounds
            // and requires the car to stay out for padExitGrace.
        }
    }

    /// <summary>Is the player's car still on the pad? Belt-and-braces: the trigger's own bookkeeping OR
    /// an explicit bounds test. Trigger enter/exit events are edge-triggered and can be re-fired by
    /// unrelated physics changes (re-parenting, filtering resets, a collider toggling), and losing the
    /// cockpit to one of those is exactly the failure the user hit. The bounds test is level-triggered
    /// and cannot glitch, so the two together only agree the player has left when they really have —
    /// which is what makes "shoved off the pad by a rival" a real, and the ONLY physical, way out.</summary>
    bool CarOnPad()
    {
        if (PlayerInside) return true;

        var car = PlayerRegistry.LocalCar;
        if (car == null) return false;
        var col = GetComponent<Collider>();
        // bounds is the AABB, so this is slightly generous — deliberately, since it's the forgiving
        // half of the pair. Works for any collider shape and can never throw (ClosestPoint does, on a
        // non-convex MeshCollider).
        return col != null && col.bounds.Contains(car.transform.position);
    }

    bool IsPlayer(Collider other)
    {
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag)) return true;
            t = t.parent;
        }
        return false;
    }

    // -------------------------------------------------------
    //  Input
    // -------------------------------------------------------

    void Update()
    {
        if (piloting) { TickPiloting(); return; }

        if (!isOpen)
        {
            if (PlayerInside && !suppressedUntilExit && !MenuState.AnyOpen) Open();
            return;
        }

        if (!PlayerInside) { Close(); return; }

        RefreshRows();

        var gp = Gamepad.current;
        if (gp == null) return;

        if (gp.buttonEast.wasPressedThisFrame) { suppressedUntilExit = true; Close(); return; }

        if (available.Count > 0)
        {
            if (gp.dpad.up.wasPressedThisFrame || gp.leftStick.up.wasPressedThisFrame) Move(-1);
            if (gp.dpad.down.wasPressedThisFrame || gp.leftStick.down.wasPressedThisFrame) Move(1);
            if (gp.buttonSouth.wasPressedThisFrame) TryTakeControls();
        }
    }

    void Move(int dir)
    {
        if (available.Count == 0) return;
        selected = (selected + dir + available.Count) % available.Count;
        AudioManager.PlayStoreMove();
        RefreshRows();
    }

    // -------------------------------------------------------
    //  Taking / handing back the controls
    // -------------------------------------------------------

    void TryTakeControls()
    {
        if (selected < 0 || selected >= available.Count) return;

        var entry = available[selected];
        if (entry.Value != SupportShipReplicator.NoClient)
        {
            AudioManager.PlayStoreDenied();   // already flown by someone else
            return;
        }
        if (!SupportShipReplicator.IsPilotable(entry.Key))
        {
            AudioManager.PlayStoreDenied();   // summoned, but its owner hasn't entered the track yet
            return;
        }

        // Optimism is not allowed here: two hub players can reach for the same ship in the same frame,
        // so the server decides. We ask, and StartPiloting only runs once the answer names us.
        SupportShipReplicator.RequestPilot(entry.Key, claim: true);
        AudioManager.PlayStoreSelect();
        pendingOwner = entry.Key;
        awaitingGrant = true;
        grantDeadline = Time.unscaledTime + GrantTimeout;
    }

    private ulong pendingOwner;
    private bool awaitingGrant;
    private float grantDeadline;
    // A REFUSAL looks identical to a slow network — the server just never names us — so the wait has
    // to end on a clock. Generous enough to cover a bad connection, short enough that the player can
    // try again without wondering whether the button did anything.
    private const float GrantTimeout = 3f;

    void LateUpdate()
    {
        // The grant arrives asynchronously (or instantly, offline). Watching the replicator rather than
        // assuming success is what makes a contested ship resolve to exactly one pilot.
        if (!awaitingGrant || piloting) return;

        if (SupportShipReplicator.LocalPilotOf == pendingOwner)
        {
            awaitingGrant = false;
            StartPiloting(pendingOwner);
            return;
        }

        if (Time.unscaledTime >= grantDeadline)
        {
            awaitingGrant = false;
            AudioManager.PlayStoreDenied();
        }
    }

    void StartPiloting(ulong ownerId)
    {
        var ship = SupportShipReplicator.GetShip(ownerId);
        if (ship == null)
        {
            SupportShipReplicator.RequestPilot(ownerId, claim: false);
            return;
        }

        piloting = true;
        pilotedOwner = ownerId;
        Close();

        // Suppress everything the sticks would otherwise do: turbo, jump, the shield, the grapple, and
        // any other trigger menu opening under the parked car. This is the same flag the store uses.
        MenuState.AnyOpen = true;
        SuppressLocalCarInput(true);
        padExitSince = -1f;
        shipLostSince = -1f;
        ownerLeftSince = -1f;
        EnsureRig();
        BindCamera(ship);
        // Borrow the track's lights and music: the pilot's eyes and ears are out there even though
        // their car is not. The sky is handled per-camera in BindCamera.
        MultiplayerWorld.SetPilotPresentation(true);
        // Swap the HUD over with the camera: the car's instruments report on a vehicle parked where the
        // pilot cannot see it, so they go, and the ship's health pool takes their place. Credits stay -
        // currency is the player's, not the vehicle's.
        GameplayHud.SetPiloting(true);
        SupportShipPilotHUD.Show(pilotedOwner);
        ShowHint("Stick Fly    B / X Fwd / Back    LT / RT Roll    A Fire    Y Repair    Select Release");
        // A fresh cockpit starts with the guns cold — the A press that TOOK the controls must not also
        // loose a burst on the way in.
        burstFired = Mathf.Max(1, burstRounds);
        nextRoundTime = 0f;
    }

    /// <summary>Flying. Control is held until ONE of exactly four things happens, and nothing else may
    /// take it away:
    ///   1. SELECT — the pilot hands it back deliberately.
    ///   2. The ship is destroyed.
    ///   3. Its owner dismisses it (or the server reassigns the controls).
    ///   4. The car is pushed off the pad by an external force — a rival shoving or grappling them.
    ///   5. The ship's owner stops racing — they took the return portal, aborted, or were sent back.
    /// Notably NOT in that list: a stray trigger event, or one frame in which the replicated ship
    /// happens to be missing. Both are debounced below.</summary>
    void TickPiloting()
    {
        var gp = Gamepad.current;

        // 1. Manual release. SELECT (Xbox "View" / PS "Share") — B is left alone so it stays a pure
        //    menu button and can't eject a pilot by reflex.
        if (gp != null && gp.selectButton.wasPressedThisFrame)
        {
            // Handing the ship back drops them into the LIST, not out of the station — they're still
            // parked on the pad, and wanting to take a different ship is the likely next move. B from
            // there closes it properly.
            StopPiloting();
            return;
        }

        // 2 + 3. The ship is gone, wrecked, or no longer ours. A remote ship is rebuilt whenever its
        //        owner's puppet is rebuilt, so it can be null for a frame or two through no fault of
        //        the pilot — hence the grace window rather than an instant eject.
        var ship = SupportShipReplicator.GetShip(pilotedOwner);
        bool lost = ship == null || ship.IsRagdolling
                 || SupportShipReplicator.LocalPilotOf != pilotedOwner;
        if (lost)
        {
            if (shipLostSince < 0f) shipLostSince = Time.unscaledTime;
            if (Time.unscaledTime - shipLostSince > ShipLostGrace) { StopPiloting(); return; }
        }
        else shipLostSince = -1f;

        // 4. Shoved off the pad. This is the one physical way to break a pilot's concentration, and it
        //    only works because their car is input-locked rather than frozen solid.
        if (!CarOnPad())
        {
            if (padExitSince < 0f) padExitSince = Time.unscaledTime;
            if (Time.unscaledTime - padExitSince > padExitGrace)
            {
                Debug.Log("[SupportShip] Pilot left the control pad — controls released.");
                StopPiloting();
                return;
            }
        }
        else padExitSince = -1f;

        // 5. The owner took the return portal (or otherwise stopped racing). Their ship is still
        //    theirs and still summoned, but there is no longer a race to fly over, so the controls go
        //    back exactly as if SELECT had been pressed — the pilot lands in the list, still parked on
        //    the pad, ready for the next round. Debounced like the others: `IsPilotable` reads a
        //    replicated flag, and one dropped heartbeat must not eject anyone.
        if (!SupportShipReplicator.IsPilotable(pilotedOwner))
        {
            if (ownerLeftSince < 0f) ownerLeftSince = Time.unscaledTime;
            if (Time.unscaledTime - ownerLeftSince > OwnerLeftGrace)
            {
                Debug.Log("[SupportShip] The ship's owner is no longer racing — controls released.");
                StopPiloting();
                return;
            }
        }
        else ownerLeftSince = -1f;

        if (ship == null) return;   // riding out the grace window — nothing to fly this frame

        if (camAnchor != null) camAnchor.ship = ship;   // the anchor follows it in its own LateUpdate
        if (gp == null) return;
        // Left stick slides and aims; B/X push the ship forward and back; the TRIGGERS bank it. All
        // integrated LOCALLY so the pilot's own controls have no latency; the resulting offset and aim
        // angles are what go on the wire.
        //
        // Roll is ANALOGUE (changed from the bumpers 2026-08-24): a half-pulled trigger is a half roll,
        // which a button could only ever do as all-or-nothing. Pulling both still cancels to level,
        // exactly as holding both bumpers did, because the two subtract. It also frees LB/RB for
        // team voice chat.
        Vector2 stick = gp.leftStick.ReadValue();
        float depth = (gp.buttonEast.isPressed ? 1f : 0f) - (gp.buttonWest.isPressed ? 1f : 0f);
        float roll = RollAxis(gp.rightTrigger.ReadValue()) - RollAxis(gp.leftTrigger.ReadValue());
        ship.ApplyPilotMove(new Vector3(stick.x, stick.y, depth), roll, Time.deltaTime);
        TickGuns(gp);

        // Y spends one of the OWNER's "Support Ship Repair" items to give their ship hit points back.
        // The pilot decides WHEN; the owner paid for it. Nothing is validated here - the host checks the
        // ship is actually damaged and the owner's machine checks the item is actually held, because
        // neither of those facts exists on this one.
        if (gp.buttonNorth.wasPressedThisFrame)
            SupportShipReplicator.RequestRepair(pilotedOwner);
    }

    /// <summary>One trigger's contribution to the roll, dead-zoned and rescaled so the usable travel
    /// still spans the full 0..1. Without the rescale, a dead zone would cap the roll below its
    /// maximum — the ship could never quite reach maxRollAngle however hard the trigger was pulled.</summary>
    float RollAxis(float raw)
    {
        float dead = Mathf.Clamp(rollDeadzone, 0f, 0.9f);
        if (raw <= dead) return 0f;
        return (raw - dead) / (1f - dead);
    }

    /// <summary>Semi-auto burst on A. A press ARMS a fresh burst and fires its first round on the same
    /// frame — that's what makes a tap feel instant and produce exactly one shot. Holding walks through
    /// the remaining <see cref="burstRounds"/> at <see cref="burstInterval"/> and then stops dead; the
    /// guns don't speak again until the button is released and pressed anew.</summary>
    void TickGuns(Gamepad gp)
    {
        if (gp.buttonSouth.wasPressedThisFrame)
        {
            burstFired = 0;
            nextRoundTime = 0f;   // fire the opening round immediately, below
        }

        if (!gp.buttonSouth.isPressed) return;
        if (burstFired >= Mathf.Max(1, burstRounds)) return;
        if (Time.time < nextRoundTime) return;

        SupportShipReplicator.RequestFire(pilotedOwner);
        burstFired++;
        nextRoundTime = Time.time + Mathf.Max(0.01f, burstInterval);
    }

    void StopPiloting()
    {
        if (!piloting) { awaitingGrant = false; return; }
        piloting = false;
        awaitingGrant = false;

        SupportShipReplicator.RequestPilot(pilotedOwner, claim: false);

        MultiplayerWorld.SetPilotPresentation(false);   // back to the hub they never actually left
        GameplayHud.SetPiloting(false);
        SupportShipPilotHUD.Hide();
        RestoreCamera();
        SuppressLocalCarInput(false);
        MenuState.AnyOpen = false;
        padExitSince = -1f;
        shipLostSince = -1f;
        ownerLeftSince = -1f;
        AudioManager.PlayStoreClose();
    }

    void OnDisable()
    {
        // The hub scene can unload out from under us (entering the track portal, teardown to menu).
        if (piloting) StopPiloting();
        if (isOpen)
        {
            isOpen = false;
            MenuState.AnyOpen = false;
        }
    }

    void OnDestroy()
    {
        // We built this copy ourselves (SkyboxHueRandomizer owns the ones IT makes), so we free it.
        if (ownedSky != null) Destroy(ownedSky);
    }

    // -------------------------------------------------------
    //  The pilot's car
    // -------------------------------------------------------

    /// <summary>Takes the pilot's car away from them while they're flying — WITHOUT freezing it.
    ///
    /// `MenuState.AnyOpen` alone is not enough: it only gates the BUTTONS (turbo / jump / brake), while
    /// throttle and steering are read unconditionally, so the car would drive off under the very stick
    /// that's flying the ship. `CarController.InputSuppressed` closes that.
    ///
    /// It deliberately does NOT pin the Rigidbody. An earlier version used
    /// `RigidbodyConstraints.FreezeAll`, which made the car immovable — and therefore made "a rival
    /// shoves you off the pad" impossible, when that is precisely one of the ways the user wants a
    /// pilot to lose the controls. Input-locked but still a normal dynamic body is what allows both:
    /// the pilot cannot drive, and everyone else can still push them around.</summary>
    void SuppressLocalCarInput(bool suppress)
    {
        CarController.InputSuppressed = suppress;
        if (!suppress) return;

        // Kill any momentum they arrived with, so they don't coast straight back off the pad the
        // instant they stop steering. Everything after this frame is somebody else pushing them.
        var car = PlayerRegistry.LocalCar;
        var rb = car != null ? car.GetComponent<Rigidbody>() : null;
        if (rb == null) return;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    // -------------------------------------------------------
    //  The pilot camera
    // -------------------------------------------------------

    void EnsureRig()
    {
        if (pilotCam != null) return;

        // The camera frames THIS, not the ship — see SupportShipCamAnchor for why.
        var anchorGo = new GameObject("SupportShipCamAnchor");
        DontDestroyOnLoad(anchorGo);
        camAnchor = anchorGo.AddComponent<SupportShipCamAnchor>();
        camAnchor.followLag = cameraFollowLag;

        var camGo = new GameObject("SupportShipPilotCam");
        DontDestroyOnLoad(camGo);
        pilotCam = camGo.AddComponent<Camera>();
        pilotCam.clearFlags = CameraClearFlags.Skybox;
        pilotCam.fieldOfView = fieldOfView;
        pilotCam.nearClipPlane = nearClip;
        pilotCam.farClipPlane = farClip;
        pilotCam.enabled = false;

        // POST-PROCESSING. Must be ON, and it is doing real work: the project grades through URP's
        // DEFAULT VOLUME PROFILE (Project Settings > Graphics), which applies to every camera with this
        // flag set WITHOUT any Volume component in any scene — Tonemapping, Bloom, ColorAdjustments,
        // Vignette, fog and more are all live in it. A camera built in code comes up with this flag
        // FALSE, so leaving it alone would give the pilot a raw, ungraded picture next to the racer's.
        //
        // Scene volumes would ALSO apply here and blend correctly, since URP blends at the CAMERA's
        // position and this camera sits out in the track area — there simply aren't any yet.
        //
        // Grading is NOT what made the pilot's view look washed out next to a racer's, though: both had
        // this on. That was AMBIENT LIGHT — see ApplyTrackAmbient.
        var urp = pilotCam.GetUniversalAdditionalCameraData();
        if (urp != null)
        {
            urp.renderPostProcessing = enablePostProcessing;
            urp.renderShadows = true;    // the track's directional light should still cast
            urp.antialiasing = antialiasing;
            urp.antialiasingQuality = antialiasingQuality;
        }

        // PER-CAMERA SKYBOX. RenderSettings.skybox follows the ACTIVE scene, which for a hub-bound
        // pilot is the hub — and the hub's sky is Unity's built-in default while the track's is the
        // procedural SimpleSkybox. Without this override the pilot flies over the track under a plain
        // grey sky while the racer sees the real one. A Skybox COMPONENT overrides RenderSettings for
        // this camera alone, leaving everyone else's view untouched.
        camSkybox = camGo.AddComponent<Skybox>();
        pilotFollow = camGo.AddComponent<CameraFollow>();
        pilotFollow.enableSwivel = false;              // the stick flies the ship, it doesn't orbit the camera
        pilotFollow.offset = cameraOffset;
        pilotFollow.lookAheadDistance = lookAheadDistance;

        // ZERO frame smoothing, deliberately. CameraFollow's positionSmoothTime / pitchSmoothTime /
        // rollSmoothTime ease its frame toward its TARGET's rotation — but our target already carries
        // the ship's FollowFrame, which the ship has ALREADY smoothed against the car. Leaving them set
        // stacks a second lag on a first, and because it is an ANGULAR lag it only bites while the car
        // is turning: the ship would sit still on screen at a standstill and slide across it through a
        // fast corner. One layer of laziness (the ship's) is the whole design; tune it on the SHIP.
        pilotFollow.positionSmoothTime = 0f;
        pilotFollow.pitchSmoothTime = 0f;
        pilotFollow.rollSmoothTime = 0f;
        pilotFollow.rotationSmoothTime = 0f;

        // The ship has a Rigidbody, so CameraFollow's speed-scaled FOV would breathe as it slides
        // around. Pin both ends to hold a constant framing.
        pilotFollow.baseFOV = pilotFollow.maxFOV = fieldOfView;

        // Added last: CameraFollow.Start hangs the speed-barrier low-pass off whatever listener it
        // finds, and we want that to be inert here.
        pilotListener = camGo.AddComponent<AudioListener>();
        pilotListener.enabled = false;
    }


    /// <summary>Points the pilot camera at the TRACK's sky, with this round's hues.
    ///
    /// Three sources, in order of fidelity:
    ///  1. <see cref="SkyboxHueRandomizer.CurrentSky"/> — the live recoloured instance, but ONLY when it
    ///     was built for the CURRENT round. It goes stale otherwise: returning to the hub cannot
    ///     recolour the hub's non-SimpleSkybox sky, so last round's track sky lingers there. See the
    ///     warning on CurrentSky — serving that stale material is exactly how the pilot ended up under
    ///     a different sky from the racers.
    ///  2. A recoloured copy built from <see cref="trackSkybox"/>. A teammate who has spent the whole
    ///     session in the hub never made the track their ACTIVE scene, so the randomizer never ran for
    ///     it and (1) is null — but the hues are derived from the shared round seed, so building it
    ///     here lands on the same colours everyone else got.
    ///  3. The raw <see cref="trackSkybox"/> asset, un-hued. Single-player fallback.
    /// Falling all the way through leaves the camera on RenderSettings, i.e. the hub's sky.</summary>
    void ApplyTrackSkybox()
    {
        Material sky = ResolveTrackSky();
        if (sky == null) return;

        if (camSkybox != null) camSkybox.material = sky;
        ApplyTrackAmbient(sky);
    }

    Material ResolveTrackSky() => SkyboxHueRandomizer.ResolveRoundSky(trackSkybox, ref ownedSky);

    /// <summary>Lights the track for the pilot, by pointing the GLOBAL environment at the track's sky.
    ///
    /// ⚠️ The per-camera Skybox component above does NOT do this, and that is the whole bug it took a
    /// side-by-side screenshot to see. Both scenes are set to Ambient Mode = SKYBOX, so all ambient
    /// light is generated from <c>RenderSettings.skybox</c> — a GLOBAL property that follows the ACTIVE
    /// scene. A hub-bound pilot's active scene is the hub, whose sky is Unity's built-in bright daylight
    /// Default-Skybox, so they were lighting a night track with a blue afternoon: the ground came out
    /// flat and pale while the racer saw it dark and contrasty. The camera showed the right sky the
    /// whole time; only the LIGHT coming off it was wrong.
    ///
    /// Safe despite being global. This machine renders nothing else while piloting — the hub camera is
    /// disabled and the pilot's car is off-screen — and RenderSettings is per-machine, so no other
    /// player is affected. It is restored the instant the controls go back.
    ///
    /// (For the record: post-processing is NOT involved in the difference. There is not a single Volume
    /// component in the project, so nothing is graded, and `renderPostProcessing` changes nothing
    /// either way. The drama is entirely lighting.)</summary>
    void ApplyTrackAmbient(Material sky)
    {
        if (skySwapped || sky == null) return;

        suppressedSky = RenderSettings.skybox;
        skySwapped = true;
        RenderSettings.skybox = sky;
        DynamicGI.UpdateEnvironment();   // ambient + reflections are recomputed from the new sky
    }

    /// <summary>Hands the environment back to the hub. Guarded on the stored material still existing:
    /// leaving the TRACK's sky lighting the hub would be a worse bug than the one this fixes.</summary>
    void RestoreAmbient()
    {
        if (!skySwapped) return;
        skySwapped = false;

        if (suppressedSky != null) RenderSettings.skybox = suppressedSky;
        suppressedSky = null;
        DynamicGI.UpdateEnvironment();
    }

    /// <summary>Cuts to the ship: our camera on, the hub camera (and its listener) off. Exactly one
    /// AudioListener may be enabled at a time, or Unity spams warnings and the mix goes undefined.</summary>
    void BindCamera(SupportShip ship)
    {
        var main = Camera.main;
        if (main != null && main != pilotCam)
        {
            suppressedCam = main;
            suppressedCam.enabled = false;

            suppressedListener = main.GetComponent<AudioListener>();
            if (suppressedListener != null && suppressedListener.enabled) suppressedListener.enabled = false;
            else suppressedListener = null;
        }

        camAnchor.ship = ship;
        camAnchor.followLag = cameraFollowLag;
        camAnchor.Snap();                      // no swing on the way in
        ApplyTrackSkybox();
        pilotFollow.target = camAnchor.transform;
        pilotCam.enabled = true;
        if (pilotListener != null) pilotListener.enabled = true;
    }

    void RestoreCamera()
    {
        RestoreAmbient();
        if (pilotCam != null) pilotCam.enabled = false;
        if (pilotFollow != null) pilotFollow.target = null;
        if (camAnchor != null) camAnchor.ship = null;   // stop it chasing a ship nobody is flying
        if (pilotListener != null) pilotListener.enabled = false;

        // Guarded: the hub camera may have been destroyed under us by a scene load.
        if (suppressedCam != null) suppressedCam.enabled = true;
        if (suppressedListener != null) suppressedListener.enabled = true;
        suppressedCam = null;
        suppressedListener = null;
    }

    // -------------------------------------------------------
    //  Open / close + rows
    // -------------------------------------------------------

    void Open()
    {
        EnsureUI();
        selected = 0;
        RefreshRows();
        ShowHint("↑↓ Choose    A Take Controls    B Close");   // "Select" is the release button now
        root.SetActive(true);
        isOpen = true;
        MenuState.AnyOpen = true;
        AudioManager.PlayStoreOpen();
    }

    void Close()
    {
        if (root != null) root.SetActive(false);
        // Abandon any request still in flight, so a grant that lands after the player has walked away
        // doesn't yank them into a cockpit they've stopped asking for.
        if (!piloting) awaitingGrant = false;
        if (!isOpen) return;
        isOpen = false;
        // Piloting keeps the flag raised on purpose — the sticks are still spoken for.
        if (!piloting) MenuState.AnyOpen = false;
        AudioManager.PlayStoreClose();
    }

    /// <summary>Rebuilds the list of flyable ships. Recomputed every frame the menu is up because it's
    /// live data: a teammate can summon or lose a ship, or someone else can grab the controls, while
    /// the player is sitting here reading it.</summary>
    void RefreshRows()
    {
        SupportShipReplicator.ListActiveShips(available);

        if (teamOnly)
        {
            int myTeam = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;
            if (myTeam != 0)
            {
                for (int i = available.Count - 1; i >= 0; i--)
                {
                    var remote = PlayerRegistry.FindRemote(available[i].Key);
                    // A null remote is US (our own ship is always listed) — never filter that out.
                    if (remote != null && remote.Team != myTeam) available.RemoveAt(i);
                }
            }
        }

        if (available.Count > MaxRows) available.RemoveRange(MaxRows, available.Count - MaxRows);
        selected = available.Count == 0 ? 0 : Mathf.Clamp(selected, 0, available.Count - 1);

        if (emptyText != null)
        {
            emptyText.gameObject.SetActive(available.Count == 0);
            emptyText.text = "No teammates are flying a Support Ship.";
        }

        for (int i = 0; i < rowNames.Count; i++)
        {
            bool used = i < available.Count;
            rowBackgrounds[i].gameObject.SetActive(used);
            if (!used) continue;

            var entry = available[i];
            bool busy = entry.Value != SupportShipReplicator.NoClient
                     && entry.Value != LocalClientIdOrZero;
            // Summoned but its owner is still in the hub: shown, and shown as unavailable rather than
            // hidden. "No teammates are flying a Support Ship" would be a lie — they ARE flying one,
            // it just has nothing to escort yet — and the pilot needs to know it is coming.
            bool waiting = !SupportShipReplicator.IsPilotable(entry.Key);

            rowNames[i].text = DisplayName(entry.Key);
            rowStatus[i].text = waiting ? "IN HUB" : busy ? "IN USE" : "READY";

            bool sel = (i == selected);
            bool blocked = busy || waiting;
            rowBackgrounds[i].color = blocked ? rowBusy : (sel ? rowSelected : rowNormal);
            rowNames[i].color = (sel && !blocked) ? textSelected : textNormal;
            rowStatus[i].color = (sel && !busy) ? textSelected : textNormal;
        }
    }

    static ulong LocalClientIdOrZero =>
        Unity.Netcode.NetworkManager.Singleton != null
            ? Unity.Netcode.NetworkManager.Singleton.LocalClientId : 0;

    /// <summary>Who a ship belongs to, by player name where the roster knows it.</summary>
    static string DisplayName(ulong ownerId)
    {
        if (ownerId == LocalClientIdOrZero) return "Your Ship";
        var remote = PlayerRegistry.FindRemote(ownerId);
        return remote != null && !string.IsNullOrEmpty(remote.Name) ? remote.Name : $"Player {ownerId}";
    }

    void ShowHint(string text)
    {
        if (hintText != null) hintText.text = text;
    }

    // -------------------------------------------------------
    //  Code-built UI (no scene Canvas required)
    // -------------------------------------------------------

    void EnsureUI()
    {
        if (root != null) return;

        const float panelWidth = 760f;
        const float rowHeight = 64f;
        const float headerHeight = 120f;
        const float footerHeight = 110f;
        float panelHeight = headerHeight + MaxRows * rowHeight + footerHeight;

        root = new GameObject("PilotControlCanvas");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var panel = NewUI("Panel", root.transform);
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0.05f, 0.05f, 0.08f, 0.92f);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(panelWidth, panelHeight);
        prt.anchoredPosition = Vector2.zero;

        var title = NewText(panel.transform, "Title", 52, TextAlignmentOptions.Top);
        title.text = "PILOT CONTROL";
        title.color = Color.white;
        StretchTop(title.rectTransform, panelWidth - 60f, 66f, 20f);

        var subtitle = NewText(panel.transform, "Subtitle", 26, TextAlignmentOptions.Top);
        subtitle.text = "Take the controls of a teammate's Support Ship";
        subtitle.color = new Color(0.7f, 0.75f, 0.85f);
        StretchTop(subtitle.rectTransform, panelWidth - 60f, 34f, 82f);

        rowBackgrounds.Clear();
        rowNames.Clear();
        rowStatus.Clear();
        for (int i = 0; i < MaxRows; i++)
        {
            var rowGO = NewUI($"Row{i}", panel.transform);
            var bg = rowGO.AddComponent<Image>();
            bg.color = rowNormal;
            StretchTop(rowGO.GetComponent<RectTransform>(),
                       panelWidth - 40f, rowHeight - 8f, headerHeight + i * rowHeight);

            var nameTxt = NewText(rowGO.transform, "Name", 34, TextAlignmentOptions.MidlineLeft);
            FillWithPadding(nameTxt.rectTransform, 22f, 22f);

            var statusTxt = NewText(rowGO.transform, "Status", 28, TextAlignmentOptions.MidlineRight);
            FillWithPadding(statusTxt.rectTransform, 22f, 22f);

            rowBackgrounds.Add(bg);
            rowNames.Add(nameTxt);
            rowStatus.Add(statusTxt);
            rowGO.SetActive(false);
        }

        // Shown in place of the rows when nobody on the team has a ship out.
        emptyText = NewText(panel.transform, "Empty", 28, TextAlignmentOptions.Center);
        emptyText.color = new Color(0.85f, 0.86f, 0.92f);
        emptyText.enableWordWrapping = true;
        StretchTop(emptyText.rectTransform, panelWidth - 80f, 80f, headerHeight + 20f);

        hintText = NewText(panel.transform, "Hint", 26, TextAlignmentOptions.Bottom);
        hintText.color = new Color(1f, 1f, 1f, 0.6f);
        var hrt = hintText.rectTransform;
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0.5f, 0f);
        hrt.sizeDelta = new Vector2(panelWidth - 60f, 40f);
        hrt.anchoredPosition = new Vector2(0f, 22f);

        UiLayer.Apply(root);   // keep it off the grappling hook's radar, like every other HUD
        root.SetActive(false);
    }

    static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static TextMeshProUGUI NewText(Transform parent, string name, int fontSize, TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        return t;
    }

    static void StretchTop(RectTransform rt, float width, float height, float topOffset)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(0f, -topOffset);
    }

    static void FillWithPadding(RectTransform rt, float left, float right)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(left, 0f);
        rt.offsetMax = new Vector2(-right, 0f);
    }
}
