using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Arcade RAYCAST car controller. Replaces the old WheelCollider-based simulation,
/// whose suspension oscillated and shook the car on steep hills and loops at high
/// speed. Instead of physical wheel springs, this:
///
///   • Casts a ray down from each wheel anchor to find the ground.
///   • Holds ride height with a single damped hover force at the centre of mass
///     (no per-wheel spring torque, so nothing oscillates).
///   • Drives orientation EXPLICITLY — aligns the car's up to the averaged ground
///     normal and turns its heading directly — which is what keeps hills and loops
///     perfectly smooth at 300 mph.
///   • Uses a simple grip model (cancel sideways velocity) for cornering, softened
///     at the rear while drifting.
///
/// The existing prefab wiring is reused as-is: the four WheelCollider references are
/// kept (and DISABLED at runtime) purely so their transforms serve as ray anchors,
/// and the body's BoxCollider still handles drone hits / kill-floor triggers.
///
/// Public API (SpeedMph, IsTurboActive, IsLoopGravityCut, IsDrifting,
/// IsManuallyPitching) is unchanged so the camera and speedometer keep working.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [Header("Wheel Anchors (kept for ray origins; colliders are disabled at runtime)")]
    public WheelCollider wheelFL;
    public WheelCollider wheelFR;
    public WheelCollider wheelRL;
    public WheelCollider wheelRR;

    [Header("Wheel Meshes")]
    public Transform meshFL;
    public Transform meshFR;
    public Transform meshRL;
    public Transform meshRR;

    [Header("Speed")]
    [Tooltip("Top speed in mph.")]
    public float maxSpeedMph = 300f;
    [Tooltip("Forward acceleration at full throttle and low speed (m/s^2). Falls off " +
             "toward zero as the car nears its top speed.")]
    public float acceleration = 35f;
    [Tooltip("Reverse acceleration when holding the Left Trigger from a near stop (m/s^2).")]
    public float reverseAcceleration = 16f;
    [Tooltip("How quickly the car bleeds speed ABOVE its cap back down to it (m/s^2). The " +
             "cap is the base top speed times any turbo/loop multiplier. The car may exceed " +
             "it while going downhill, then eases back once it levels out or the boost ends.")]
    public float overspeedDamping = 20f;
    [Tooltip("Nose-down amount (forward.y) past which the car counts as 'going downhill' and " +
             "is allowed to exceed top speed without being clamped. ~0.05 ≈ 3 degrees.")]
    public float downhillThreshold = 0.05f;

    [Header("Ground Detection / Raycast")]
    [Tooltip("Layers the suspension rays test against. Should include the track, " +
             "shoulders, ramps and loops. Triggers are always ignored.")]
    public LayerMask groundMask = ~0;
    [Tooltip("How far below each wheel anchor the ray probes for ground (metres). " +
             "Must comfortably exceed Ride Height so the car still detects the surface " +
             "through suspension travel and over crests.")]
    public float suspensionRayLength = 1.6f;
    [Tooltip("Target gap from each wheel anchor down to the ground (metres). The hover " +
             "force keeps the car floating at this height.")]
    public float rideHeight = 0.5f;

    [Header("Suspension (hover)")]
    [Tooltip("Stiffness of the ride-height hover (acceleration units). Higher = the " +
             "car resists being pushed off ride height more strongly.")]
    public float springStrength = 130f;
    [Tooltip("Damping of the hover. Higher = less bounce. Keep high to stay smooth.")]
    public float springDamper = 16f;
    [Tooltip("How far the VISUAL wheel may travel up/down from its hub as the suspension " +
             "reacts to the ground (metres). Purely cosmetic — keeps the tyres on bumps " +
             "without ever detaching horizontally from the car.")]
    public float maxWheelTravel = 0.4f;

    [Header("Steering")]
    [Tooltip("Turn rate (degrees/second) at low speed.")]
    public float turnRateLowSpeed = 130f;
    [Tooltip("Turn rate (degrees/second) at top speed. Lower so the car isn't twitchy " +
             "at 300 mph.")]
    public float turnRateHighSpeed = 48f;
    [Tooltip("How quickly steering input ramps in/out. Higher = snappier.")]
    public float steerLerpSpeed = 6f;
    [Tooltip("How quickly the car's body re-aligns to the ground normal / new heading. " +
             "Higher = tighter to the surface, lower = floatier. This smoothing is what " +
             "removes the old WheelCollider shake on hills and loops.")]
    public float orientationLerpSpeed = 12f;

    [Header("Grip")]
    [Tooltip("Fraction of sideways velocity removed each physics step (0-1). 1 = the " +
             "car tracks its forward direction perfectly (no slide); lower = looser.")]
    [Range(0f, 1f)] public float gripFactor = 0.9f;
    [Tooltip("How quickly grip blends from the drift grip back up to Grip Factor after a drift " +
             "ends (per second). Higher = snappier recovery; lower = eases out of the slide " +
             "more gradually.")]
    public float gripRecoverySpeed = 4f;

    [Header("Braking")]
    [Tooltip("Deceleration applied while braking (m/s^2).")]
    public float brakeStrength = 45f;
    [Tooltip("Gentle deceleration while coasting (no throttle, no brake) (m/s^2).")]
    public float engineBraking = 6f;

    [Header("Grip & Stability")]
    public Transform centerOfMass;

    [Header("Drift")]
    [Tooltip("Hold Throttle (RT) and Brake (X) together to drift. The car keeps power " +
         "but brakes softly, loses grip, and gains heavy downforce so it slides in a " +
         "controlled arc. Release either input to leave the drift.")]
    [Range(0f, 1f)] public float driftGripFactor = 0.35f;
    [Tooltip("Deceleration while drifting (m/s^2) — much softer than the normal brake " +
             "so the car keeps rolling and slides rather than stopping.")]
    public float driftBrakeStrength = 8f;
    [Tooltip("Max downforce (Newtons at top speed) while drifting. Replaces the normal " +
         "braking downforce bonus to keep the sliding car planted.")]
    public float driftMaxDownforce = 30000f;
    [Tooltip("Turn rate (deg/s) at high speed while drifting — higher than the normal " +
         "high-speed rate so the player can counter-steer through a slide.")]
    public float driftTurnRateHighSpeed = 110f;
    [Tooltip("Top-speed multiplier while drifting, scaled by how far the stick is steered. At " +
         "full steer the cap reaches this multiple of Max Speed (2 = 600 mph from 300); at half " +
         "steer it's halfway (1.5x). The car accelerates up to the raised cap; the normal cap " +
         "(and overspeed damping) returns when the drift ends.")]
    public float driftMaxSpeedMultiplier = 2f;

    [Header("Downforce / Stick")]
    [Tooltip("Maximum downforce in Newtons at top speed, applied along -ground normal. " +
             "Scales with speed squared, so high-speed crests and loops stay planted.")]
    public float maxDownforce = 12000f;
    [Tooltip("Extra downforce multiplier while braking. 1 = no extra, 2 = double.")]
    public float brakingDownforceMultiplier = 2.5f;
    [Tooltip("Constant pull toward the surface while grounded (acceleration units). " +
             "Helps hug the track through dips. Stacked with Loop Stick Force on loops.")]
    public float groundStickForce = 8f;

    [Header("Hill Climb Assist")]
    [Tooltip("Counteracts gravity along the car's forward direction when climbing. " +
             "1.0 = no speed loss going uphill, 0.0 = full physics.")]
    [Range(0f, 1f)] public float hillGravityCompensation = 0.9f;
    [Tooltip("Extra acceleration multiplier when climbing, scaling with steepness.")]
    public float climbTorqueBoost = 1.5f;
    [Tooltip("Slope angle (degrees) at which the climb assist reaches full strength.")]
    public float fullAssistAngle = 25f;

    [Header("Air Drift")]
    [Tooltip("How fast the car can drift sideways while airborne (m/s).")]
    public float airDriftSpeed = 15f;
    [Tooltip("How quickly the car reaches max air-drift speed when input is held.")]
    public float airDriftAcceleration = 40f;
    [Tooltip("Seconds the car must be airborne before air drift / air control activate. " +
             "Prevents them triggering on high-speed crests and bumps.")]
    public float airDriftGracePeriod = 0.4f;

    [Header("Manual Air Pitch")]
    [Tooltip("How fast the player can manually pitch the car in midair (degrees/second), " +
         "available after it self-levels.")]
    public float manualPitchSpeed = 120f;
    [Tooltip("Extra gravity multiplier while braking midair. 1 = normal, 3 = triple. " +
         "Only when the car is level (same condition as air drift). Lets you dive to " +
         "lower track levels.")]
    public float airBrakeGravityMultiplier = 3f;

    [Header("Gravity")]
    [Tooltip("Multiplies gravity while the car is AIRBORNE so it falls faster and feels less " +
             "floaty. 1 = normal, 2 = falls twice as fast. Applied off the ground only — the " +
             "hover suspension handles vertical support when grounded, so it never fights the " +
             "ride height or the loop stick force.")]
    public float gravityMultiplier = 2f;

    [Header("Air Drag")]
    [Tooltip("Horizontal air resistance while airborne (per-second decay rate). Bleeds the " +
             "car's horizontal momentum so it can't just coast across the whole level in the " +
             "air. Vertical motion is left untouched, so gravity / falling is unaffected. " +
             "0 = no air drag.")]
    public float airDrag = 0.5f;

    [Header("Airborne Self-Leveling")]
    [Tooltip("How quickly the car's pitch and roll return to level while airborne " +
             "(degrees/second).")]
    public float airLevelingSpeed = 90f;
    [Tooltip("Tilt threshold (degrees) below which air drift / manual pitch unlock.")]
    public float airDriftLevelThreshold = 5f;

    [Header("Turbo Boost")]
    [Tooltip("Multiplier applied to top speed and acceleration during turbo.")]
    public float turboMultiplier = 2f;
    [Tooltip("How long turbo lasts when activated (seconds).")]
    public float turboDuration = 2f;
    [Tooltip("Cooldown before turbo can be used again (seconds). 0 = no cooldown.")]
    public float turboCooldown = 0f;

    [Header("Turbo Tire Trails")]
    [Tooltip("Draw skid-mark trails from the rear tires while the Turbo Boost is active.")]
    public bool turboTrails = true;
    [Tooltip("How long each piece of trail stays on the track before it fades away (seconds).")]
    public float turboTrailTime = 1f;
    [Tooltip("Width of the tire trail at the tire, tapering to ~60% at its tail (metres).")]
    public float turboTrailWidth = 0.3f;
    [Tooltip("Trail colour. Its alpha fades to zero over the trail's lifetime.")]
    public Color turboTrailColor = new Color(0.08f, 0.08f, 0.08f, 0.75f);
    [Tooltip("Lifts the trail up the car's local Y off the ground contact (metres) so it sits ON TOP " +
             "of the track surface instead of bleeding into it (z-fighting). Small values, ~0.02–0.05.")]
    public float turboTrailHeightOffset = 0.03f;
    [Tooltip("Optional material for the trail. Leave empty to auto-build a simple alpha-blended one.")]
    public Material turboTrailMaterial;

    [Header("Jump")]
    [Tooltip("Instantaneous velocity (m/s) added along the car's local up when A is pressed.")]
    public float jumpVelocity = 12f;
    [Tooltip("If true, the car can only jump when grounded. Uncheck to allow midair jumps.")]
    public bool jumpRequiresGround = true;
    [Tooltip("Suspension ray length used briefly right after a jump (metres). Shorter than the " +
             "normal ray so the ground 'lets go' quickly and even a small Jump Velocity can lift " +
             "the car off, instead of the hover spring instantly yanking it back to ride height.")]
    public float jumpSuspensionRayLength = 0.4f;
    [Tooltip("How long the shortened jump ray lasts after a jump (seconds). Make it long enough " +
             "to cover the hop's airtime, or the full-length ray re-catches the car mid-air and " +
             "the hover pulls it back down. The normal ray length returns afterward.")]
    public float jumpRayShortenDuration = 0.35f;

    [Header("Ability Costs")]
    [Tooltip("Each turbo boost consumes 1 of this inventory item. Blank = free.")]
    public string turboItemName = "Turbo";
    [Tooltip("Each jump consumes 1 of this inventory item. Blank = free.")]
    public string jetItemName = "Jet";

    [Header("Loop Assist")]
    [Tooltip("Tag on loop track meshes. Used for the loop speed boost and the camera's " +
         "loop FOV kick.")]
    public string loopTag = "Loop";
    [Tooltip("up.up dot below this (just past vertical) flags the car as 'in a loop' " +
         "for the camera FOV kick.")]
    public float loopGravityDisableDot = -0.05f;
    [Tooltip("up.up dot above this clears the loop flag (hysteresis prevents flicker).")]
    public float loopGravityEnableDot = 0.10f;
    [Tooltip("Extra pull toward the surface while on a loop (acceleration units). Keeps " +
         "the car glued through the inverted apex.")]
    public float loopStickForce = 12f;
    [Tooltip("Multiplier applied to max speed and acceleration while on a loop.")]
    public double loopSpeedMultiplier = 2.0;

    // -------------------------------------------------------
    //  Internal
    // -------------------------------------------------------

    [Header("Drift Audio")]
    [Tooltip("Tire-screech volume at FULL stick deflection. Volume scales linearly from 0 (stick " +
             "centred — no squeal) up to this at full lock.")]
    [Range(0f, 1f)] public float driftScreechMaxVolume = 1f;
    [Tooltip("Tire-screech pitch at the shallowest drift steer.")]
    public float driftScreechMinPitch = 0.9f;
    [Tooltip("Tire-screech pitch at full drift steer.")]
    public float driftScreechMaxPitch = 1.5f;
    [Tooltip("How fast volume & pitch chase the stick (higher = snappier / near-instant, lower = " +
             "softer glide).")]
    public float driftScreechResponsiveness = 10f;
    [Tooltip("Track speed (mph) at which the screech reaches full volume. Volume scales with speed " +
             "below this and is silent at a standstill (stationary tires don't screech).")]
    public float driftScreechFullSpeedMph = 150f;
    [Tooltip("Spatial blend for the drift screech: 1 = 3D positional (others hear it), 0 = 2D.")]
    [Range(0f, 1f)] public float driftScreechSpatialBlend = 1f;
    [Tooltip("For 3D blend, distance in metres beyond which the drift screech fades out.")]
    public float driftScreechMaxDistance = 80f;

    private Rigidbody rb;
    private float throttleInput;
    private float steerInput;
    private float brakeInput;
    private float manualPitchInput;
    private float smoothedSteer;          // steer input after lerp smoothing
    private float currentGrip;            // grip value, lerped back to gripFactor after a drift

    private float airborneTimer = 0f;
    private float turboTimer = 0f;
    private float turboCooldownTimer = 0f;
    private bool jumpRequested;
    private float jumpRayTimer;            // >0 = use the shortened jump suspension ray
    private bool manualPitchUnlocked;
    private bool isDrifting;
    private bool loopFlag;                // "in a loop" state for the camera (hysteresis)

    // Per-wheel ray results, filled each FixedUpdate (index 0..3 = FL,FR,RL,RR).
    private WheelCollider[] anchors;
    private Transform[] anchorTransforms;
    private Transform[] wheelMeshes;
    // Vertical distance (metres) from each wheel anchor straight down to the ground,
    // recorded each physics step. A scalar — NOT a world point — so the visual wheel
    // can be placed under the current hub without trailing behind at high speed.
    private readonly float[] groundDistance = new float[4];
    private readonly bool[] wheelGrounded = new bool[4];
    private float[] wheelSpinAngle = new float[4];

    private bool grounded;
    private Vector3 groundNormal = Vector3.up;
    private Collider groundCollider;

    private GeneracerControls controls;
    private AudioSource driftSource;   // looping tire-screech while drifting
    private bool wasAirborne;          // tracks the airborne -> grounded edge for the landing sound

    private TrailRenderer trailRL;     // rear-left turbo skid mark
    private TrailRenderer trailRR;     // rear-right turbo skid mark
    private bool trailsWereAirborne;   // rising edge of real airtime, to break the trail across a jump
    private float trailKickTimer;      // >0 forces the trail on for an external boost (BoostGate), no real turbo

    public bool IsTurboActive => turboTimer > 0f;
    /// <summary>True while the car is in the drift state (throttle + brake held).</summary>
    public bool IsDrifting => isDrifting;
    /// <summary>True while the car is past vertical on a loop (drives the camera FOV kick).</summary>
    public bool IsLoopGravityCut => loopFlag;
    public bool IsManuallyPitching { get; private set; }
    /// <summary>True while the car is genuinely airborne (off the ground past the brief grace
    /// window that ignores crests/bumps). Read by anti-air logic such as the homing boulders.</summary>
    public bool IsAirborne { get; private set; }
    /// <summary>Seconds since the car was last grounded — 0 while any wheel is on the ground, counting
    /// up while fully airborne. Lets external effects (e.g. the speed-barrier kick) apply their own
    /// grounded grace window.</summary>
    public float AirborneTime => airborneTimer;

    /// <summary>Fired the moment the car successfully fires its Jet (a jump that spent a Jet and
    /// launched). Accessories such as JetFlames subscribe to flare on jump.</summary>
    public event System.Action OnJumped;

    private const float MS_TO_MPH = 2.23694f;
    private const float MPH_TO_MS = 1f / MS_TO_MPH;

    public float SpeedMph => rb != null ? rb.linearVelocity.magnitude * MS_TO_MPH : 0f;

    // -------------------------------------------------------
    //  Setup
    // -------------------------------------------------------

    void OnEnable()
    {
        if (controls == null) controls = new GeneracerControls();
        controls.Driving.Enable();
    }

    void OnDisable()
    {
        controls?.Driving.Disable();
    }

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (centerOfMass != null)
            rb.centerOfMass = centerOfMass.localPosition;

        currentGrip = gripFactor;

        anchors = new[] { wheelFL, wheelFR, wheelRL, wheelRR };
        wheelMeshes = new[] { meshFL, meshFR, meshRL, meshRR };
        anchorTransforms = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            // Disable the WheelCollider so it exerts no physics — we only want its
            // transform as a ray origin. Keep a cached transform reference.
            if (anchors[i] != null)
            {
                anchors[i].enabled = false;
                anchorTransforms[i] = anchors[i].transform;
            }
        }

        SetUpDriftAudio();
        SetUpTurboTrails();
    }

    // -------------------------------------------------------
    //  Input
    // -------------------------------------------------------

    void Update()
    {
        throttleInput = controls.Driving.Throttle.ReadValue<float>();   // RT - LT, -1..1
        steerInput = controls.Driving.Steer.ReadValue<float>();
        manualPitchInput = controls.Driving.Pitch.ReadValue<float>();
        brakeInput = (!MenuState.AnyOpen && controls.Driving.Brake.IsPressed()) ? 1f : 0f;

        if (!MenuState.AnyOpen && controls.Driving.Turbo.triggered)
            TryActivateTurbo();

        // A = jump. Suppressed during the L+R+A LRA abort gesture so it doesn't also
        // fire a jump (and spend a Jet).
        if (!MenuState.AnyOpen && controls.Driving.Jump.triggered && !BothTriggersHeld())
            jumpRequested = true;

        UpdateWheelMeshes();
        UpdateTurboTrails();
        UpdateDriftAudio();
        UpdateLandingAudio();
    }

    void SetUpDriftAudio()
    {
        driftSource = gameObject.AddComponent<AudioSource>();
        driftSource.loop = true;
        driftSource.playOnAwake = false;
        driftSource.spatialBlend = driftScreechSpatialBlend;   // 3D by default so others hear it
        driftSource.rolloffMode = AudioRolloffMode.Linear;
        driftSource.minDistance = 5f;
        driftSource.maxDistance = driftScreechMaxDistance;
        driftSource.dopplerLevel = 0f;   // pitch is driven by steering — kill doppler so the car's motion doesn't shift it
        driftSource.volume = 0f;
        driftSource.pitch = driftScreechMinPitch;

        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (lib != null && lib.driftScreech != null)
        {
            driftSource.clip = lib.driftScreech;
            driftSource.Play();   // runs at volume 0; UpdateDriftAudio fades it in while drifting
        }
    }

    /// <summary>Drives the looping tire-screech while drifting on the GROUND. Volume and pitch TARGET
    /// the RAW steering (|stick|) — volume also scales with track SPEED (silent at a standstill), and
    /// the whole thing is cut when airborne or not drifting. The source then eases toward those targets
    /// at Drift Screech Responsiveness (higher = snappier / near-instant, lower = softer glide).</summary>
    void UpdateDriftAudio()
    {
        if (driftSource == null || driftSource.clip == null) return;

        float steer = Mathf.Clamp01(Mathf.Abs(steerInput));                           // raw stick
        float speed = Mathf.Clamp01(SpeedMph / Mathf.Max(1f, driftScreechFullSpeedMph));
        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;

        // Only while drifting AND grounded — no tire squeal in the air.
        bool screeching = isDrifting && !IsAirborne;

        float targetVol = screeching ? driftScreechMaxVolume * steer * speed * sfx : 0f;
        float targetPitch = Mathf.Lerp(driftScreechMinPitch, driftScreechMaxPitch, steer);

        // Smoothing pass: ease toward the targets (frame-rate independent).
        float k = 1f - Mathf.Exp(-driftScreechResponsiveness * Time.deltaTime);
        driftSource.volume = Mathf.Lerp(driftSource.volume, targetVol, k);
        driftSource.pitch = Mathf.Lerp(driftSource.pitch, targetPitch, k);
    }

    /// <summary>Plays a one-shot the moment the car touches down after real airtime — the grace-based
    /// IsAirborne going true -> false — so small bumps don't trigger it. 3D at the car, like the other
    /// vehicle sounds.</summary>
    void UpdateLandingAudio()
    {
        bool airborne = IsAirborne;
        if (wasAirborne && !airborne)
            AudioManager.PlayCarLanding(transform.position);
        wasAirborne = airborne;
    }

    void FixedUpdate()
    {
        if (turboTimer > 0f) turboTimer -= Time.fixedDeltaTime;
        if (turboCooldownTimer > 0f) turboCooldownTimer -= Time.fixedDeltaTime;
        if (jumpRayTimer > 0f) jumpRayTimer -= Time.fixedDeltaTime;

        ProbeGround();          // fills grounded / groundNormal / per-wheel contacts
        UpdateLoopFlag();

        airborneTimer = grounded ? 0f : airborneTimer + Time.fixedDeltaTime;

        if (jumpRequested)
        {
            jumpRequested = false;
            TryJump();
        }

        UpdateDriftState();

        bool inRealAir = airborneTimer >= airDriftGracePeriod;
        IsAirborne = inRealAir;

        if (grounded)
        {
            // On the ground: hover, steer/align to the surface, grip, drive, stick.
            ApplyHover();
            ApplyOrientation();
            ApplyGrip();
            ApplyDriveAndBrake();
            ApplyDownforce();

            // Reset air state so the next airtime starts with a fresh self-level.
            manualPitchUnlocked = false;
            IsManuallyPitching = false;
        }
        else if (inRealAir)
        {
            // Airborne past the grace window — gravity rules; the player gets air control.
            if (!manualPitchUnlocked)
            {
                ApplyAirLeveling();
                if (IsCarLevel()) manualPitchUnlocked = true;
            }
            else
            {
                ApplyManualPitchAndRollLeveling();
            }

            if (IsRollLevel())
            {
                ApplyAirDrift();
                ApplyAirBrakeGravity();
            }

            // Bleed horizontal momentum so the car can't fly across the level (gravity untouched).
            ApplyAirDrag();
        }
        // else: a brief hop within the grace window — just coast ballistically.

        // Extra gravity while airborne so the car falls faster and feels less floaty. The
        // hover spring handles vertical support on the ground, so this applies off the
        // ground only — it never fights the ride height or the loop stick force.
        if (!grounded)
            ApplyGravityMultiplier();
    }

    // -------------------------------------------------------
    //  Ground probing
    // -------------------------------------------------------

    /// <summary>
    /// Casts a ray straight down (in car space) from each wheel anchor, recording
    /// contacts and building the averaged ground normal used for orientation.
    /// </summary>
    void ProbeGround()
    {
        Vector3 down = -transform.up;
        Vector3 normalSum = Vector3.zero;
        int hits = 0;
        groundCollider = null;

        // Briefly after a jump the ray is shortened so the hover spring releases the car and a
        // small Jump Velocity can actually lift it off; otherwise the full ray length is used.
        float rayLen = jumpRayTimer > 0f ? jumpSuspensionRayLength : suspensionRayLength;

        for (int i = 0; i < 4; i++)
        {
            wheelGrounded[i] = false;
            Transform a = anchorTransforms[i];
            if (a == null) continue;

            if (Physics.Raycast(a.position, down, out RaycastHit hit,
                                 rayLen, groundMask,
                                 QueryTriggerInteraction.Ignore))
            {
                wheelGrounded[i] = true;
                groundDistance[i] = hit.distance;   // vertical gap (the ray runs along -up)
                normalSum += hit.normal;
                hits++;
                if (groundCollider == null) groundCollider = hit.collider;
            }
            else
            {
                // No hit — record full droop for the visual wheel.
                groundDistance[i] = rayLen;
            }
        }

        grounded = hits > 0;
        groundNormal = grounded ? (normalSum / hits).normalized : Vector3.up;
    }

    /// <summary>
    /// Single damped hover force at the centre of mass that keeps the car floating at
    /// ride height above the averaged ground. Applied at the COM (no torque) so it can
    /// never induce the rotational oscillation the WheelColliders suffered from.
    /// </summary>
    void ApplyHover()
    {
        // Average how far the wheels currently sit above the ground.
        float gapSum = 0f; int n = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!wheelGrounded[i]) continue;
            gapSum += groundDistance[i]; n++;
        }
        if (n == 0) return;

        float gap2 = gapSum / n;
        float offset = rideHeight - gap2;                       // + = too low, push up
        float upVel = Vector3.Dot(rb.linearVelocity, transform.up);
        float force = offset * springStrength - upVel * springDamper;

        rb.AddForce(transform.up * force, ForceMode.Acceleration);
    }

    // -------------------------------------------------------
    //  Orientation + steering (explicit, smooth)
    // -------------------------------------------------------

    /// <summary>
    /// Turns the car's heading by the speed-scaled steer rate and aligns its up to the
    /// ground normal, in a single smoothed MoveRotation. Driving orientation directly
    /// (rather than via wheel springs) is what keeps hills and loops shake-free.
    /// </summary>
    void ApplyOrientation()
    {
        smoothedSteer = Mathf.Lerp(smoothedSteer, steerInput, steerLerpSpeed * Time.fixedDeltaTime);

        float speedFactor = Mathf.Clamp01(SpeedMph / maxSpeedMph);
        float curved = Mathf.Pow(speedFactor, 0.6f);
        float highRate = isDrifting ? driftTurnRateHighSpeed : turnRateHighSpeed;
        float turnRate = Mathf.Lerp(turnRateLowSpeed, highRate, curved);

        // Only turn when actually moving, scaled by how fast (parked cars don't spin).
        float moveScale = Mathf.Clamp01(rb.linearVelocity.magnitude / 4f);

        // Flip the steering yaw when reversing so the car curves toward the way the front
        // wheels point (like a real car backing up), instead of mirroring it.
        float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
        if (forwardSpeed < 0f) moveScale = -moveScale;

        float yawDelta = smoothedSteer * turnRate * moveScale * Time.fixedDeltaTime;

        // Rotate the current forward around the ground normal, then re-seat it on the
        // plane so the heading rides the surface.
        Vector3 fwd = Quaternion.AngleAxis(yawDelta, groundNormal) * transform.forward;
        fwd = Vector3.ProjectOnPlane(fwd, groundNormal);
        if (fwd.sqrMagnitude < 1e-5f) fwd = transform.forward;

        Quaternion target = Quaternion.LookRotation(fwd.normalized, groundNormal);
        float t = 1f - Mathf.Exp(-orientationLerpSpeed * Time.fixedDeltaTime);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, target, t));

        // Damp residual spin so collisions/bumps don't fight the explicit orientation.
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Vector3.zero,
                                          orientationLerpSpeed * Time.fixedDeltaTime);
    }

    /// <summary>
    /// Cancels the chosen fraction of sideways velocity so the car tracks its forward
    /// direction. The rear "breaks loose" while drifting via a much lower grip factor.
    /// </summary>
    void ApplyGrip()
    {
        // Snap to the low drift grip while drifting (so the slide starts crisply), then blend
        // back up to the standard grip once the drift ends — the car eases out of the slide
        // instead of snapping straight to full grip.
        if (isDrifting)
            currentGrip = driftGripFactor;
        else
            currentGrip = Mathf.Lerp(currentGrip, gripFactor, gripRecoverySpeed * Time.fixedDeltaTime);

        Vector3 vel = rb.linearVelocity;
        Vector3 right = transform.right;
        float lateral = Vector3.Dot(vel, right);
        rb.linearVelocity = vel - right * (lateral * currentGrip);
    }

    // -------------------------------------------------------
    //  Drive / brake
    // -------------------------------------------------------

    void ApplyDriveAndBrake()
    {
        float turbo = IsTurboActive ? turboMultiplier : 1f;
        float loopMult = loopFlag ? (float)loopSpeedMultiplier : 1f;

        float maxMs = maxSpeedMph * MPH_TO_MS * turbo * loopMult;

        // While drifting, raise the top-speed cap with the steering angle: full stick = the
        // full drift multiplier (e.g. 2x -> 600 mph), scaling linearly down to 1x at centre.
        if (isDrifting)
            maxMs *= 1f + (driftMaxSpeedMultiplier - 1f) * Mathf.Abs(steerInput);

        Vector3 fwd = transform.forward;
        float fwdSpeed = Vector3.Dot(rb.linearVelocity, fwd);
        float speedRatio = Mathf.Clamp01(Mathf.Abs(fwdSpeed) / Mathf.Max(maxMs, 0.01f));

        // --- Throttle / reverse ---
        if (throttleInput > 0.05f)
        {
            float climbBoost = ClimbBoost(fwd);
            float accel = acceleration * turbo * loopMult * climbBoost
                        * (1f - speedRatio * speedRatio)        // fade out near top speed
                        * throttleInput;
            rb.AddForce(fwd * accel, ForceMode.Acceleration);
            ApplyHillGravityCompensation(fwd);
        }
        else if (throttleInput < -0.05f)
        {
            // Left Trigger: brake to a stop, then reverse.
            float reverseRatio = Mathf.Clamp01(Mathf.Abs(Mathf.Min(fwdSpeed, 0f)) / Mathf.Max(maxMs * 0.4f, 0.01f));
            float accel = reverseAcceleration * (1f - reverseRatio) * -throttleInput;
            rb.AddForce(-fwd * accel, ForceMode.Acceleration);
        }

        // --- Braking (X) ---
        if (brakeInput > 0.05f)
        {
            float decel = (isDrifting ? driftBrakeStrength : brakeStrength) * brakeInput;
            ApplyForwardDecel(fwd, fwdSpeed, decel);
        }
        else if (Mathf.Abs(throttleInput) < 0.05f)
        {
            // Coasting — gentle engine braking.
            ApplyForwardDecel(fwd, fwdSpeed, engineBraking);
        }

        // Ease any speed over the cap back down to it. While drifting that cap is the raised,
        // steer-scaled value computed above (up to ~600 mph at full steer); when the drift ends
        // the cap drops back to normal and overspeed damping bleeds the excess off.
        ApplyTopSpeedClamp(maxMs);
    }

    /// <summary>Reduces the forward component of velocity toward zero by decel*dt.</summary>
    void ApplyForwardDecel(Vector3 fwd, float fwdSpeed, float decel)
    {
        float newSpeed = Mathf.MoveTowards(fwdSpeed, 0f, decel * Time.fixedDeltaTime);
        rb.linearVelocity += fwd * (newSpeed - fwdSpeed);
    }

    /// <summary>
    /// Eases forward speed back down to allowedMaxMs (base top speed × turbo × loop) when
    /// it's over the cap. Skipped while descending, so gravity can still carry the car
    /// past its top speed downhill; once it levels out (or the turbo/loop boost ends) the
    /// excess bleeds off at overspeedDamping instead of coasting forever with no drag.
    /// </summary>
    void ApplyTopSpeedClamp(float allowedMaxMs)
    {
        Vector3 fwd = transform.forward;
        float fwdSpeed = Vector3.Dot(rb.linearVelocity, fwd);

        if (fwdSpeed <= allowedMaxMs) return;        // under the cap — nothing to do
        if (fwd.y < -downhillThreshold) return;      // going downhill — let gravity win

        float eased = Mathf.MoveTowards(fwdSpeed, allowedMaxMs,
                                        overspeedDamping * Time.fixedDeltaTime);
        rb.linearVelocity += fwd * (eased - fwdSpeed);
    }

    /// <summary>Extra acceleration multiplier when climbing, scaling with steepness.</summary>
    float ClimbBoost(Vector3 fwd)
    {
        float climbDot = fwd.y;
        if (climbDot <= 0f) return 1f;
        float climbAngle = Mathf.Asin(Mathf.Clamp(climbDot, -1f, 1f)) * Mathf.Rad2Deg;
        float climbFactor = Mathf.Clamp01(climbAngle / fullAssistAngle);
        return 1f + climbTorqueBoost * climbFactor;
    }

    /// <summary>Cancels the backward pull of gravity along the slope so the car doesn't
    /// bleed speed climbing steep hills.</summary>
    void ApplyHillGravityCompensation(Vector3 fwd)
    {
        if (fwd.y <= 0f) return;
        Vector3 gravityForce = Physics.gravity * rb.mass;
        float backwardPull = -Vector3.Dot(gravityForce, fwd);
        if (backwardPull > 0f)
            rb.AddForce(fwd * (backwardPull * hillGravityCompensation * Mathf.Abs(throttleInput)));
    }

    // -------------------------------------------------------
    //  Downforce / stick
    // -------------------------------------------------------

    /// <summary>
    /// Pushes the car toward the surface along -ground normal. Scales with speed
    /// squared (aero), plus a constant stick term (and extra on loops) so the car hugs
    /// dips and stays glued through inverted loop sections.
    /// </summary>
    void ApplyDownforce()
    {
        float speedRatio = Mathf.Clamp01(SpeedMph / maxSpeedMph);
        float activeMax = isDrifting ? driftMaxDownforce : maxDownforce;
        float force = activeMax * speedRatio * speedRatio;

        if (!isDrifting && brakeInput > 0.05f)
            force *= 1f + (brakingDownforceMultiplier - 1f) * brakeInput;

        // Aero downforce (Newtons) along -normal.
        rb.AddForce(-groundNormal * force);

        // Constant stick (acceleration units), extra on loops.
        float stick = groundStickForce + (loopFlag ? loopStickForce : 0f);
        if (stick > 0f)
            rb.AddForce(-groundNormal * stick, ForceMode.Acceleration);
    }

    // -------------------------------------------------------
    //  Drift state
    // -------------------------------------------------------

    /// <summary>Drift = hold Throttle (RT) + Brake (X). Lowers grip, softens braking,
    /// raises downforce and high-speed turn rate. Releasing either input exits.</summary>
    void UpdateDriftState()
    {
        isDrifting = throttleInput > 0.05f && brakeInput > 0.05f;
    }

    // -------------------------------------------------------
    //  Loop flag (for the camera FOV kick)
    // -------------------------------------------------------

    void UpdateLoopFlag()
    {
        bool onLoop = grounded && groundCollider != null && groundCollider.CompareTag(loopTag);

        if (!onLoop)
        {
            loopFlag = false;
            return;
        }

        float uprightDot = Vector3.Dot(transform.up, Vector3.up);
        if (!loopFlag && uprightDot < loopGravityDisableDot)
        {
            loopFlag = true;
            // Rising edge only (guarded by !loopFlag above): fire the one-shot once as the Loop Speed
            // Multiplier engages, at the car (3D).
            AudioManager.PlayLoopBoost(transform.position);
        }
        else if (loopFlag && uprightDot > loopGravityEnableDot) loopFlag = false;
    }

    // -------------------------------------------------------
    //  Turbo / jump / spending
    // -------------------------------------------------------

    void TryActivateTurbo()
    {
        if (turboTimer > 0f) return;
        if (turboCooldownTimer > 0f) return;
        if (!TrySpend(turboItemName)) return;

        turboTimer = turboDuration;
        turboCooldownTimer = turboCooldown + turboDuration;
        AudioManager.PlayTurbo(transform.position);
    }

    bool TrySpend(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return true;
        var inv = PlayerInventory.Instance;
        if (inv == null) return false;
        return inv.Consume(itemName, 1);
    }

    void TryJump()
    {
        if (jumpRequiresGround && !grounded) return;
        if (!TrySpend(jetItemName)) return;

        Vector3 vel = rb.linearVelocity;
        float upComponent = Vector3.Dot(vel, transform.up);
        if (upComponent > 0f) vel -= transform.up * upComponent;
        rb.linearVelocity = vel;

        rb.AddForce(transform.up * jumpVelocity, ForceMode.VelocityChange);
        AudioManager.PlayJump(transform.position);
        OnJumped?.Invoke();   // let accessories (JetFlames) react to the jump

        // Shorten the suspension ray for a moment so the hover spring lets go and the jump
        // velocity can carry the car off the ground before the ray re-catches it.
        jumpRayTimer = jumpRayShortenDuration;
    }

    /// <summary>Temporarily shortens the suspension ray (the same brief window a jump uses) so an
    /// external upward pop-up — a DronePissBall hit or a lightning strike — can actually launch the
    /// car into the air instead of being caught and damped by the hover spring. The impulse itself is
    /// applied by the hitting script; this just releases the spring for the launch.</summary>
    public void ShortenSuspensionRayForPopUp()
    {
        jumpRayTimer = jumpRayShortenDuration;
    }

    /// <summary>Lays the rear-tire turbo skid trail for a moment as if a normal Turbo were firing,
    /// WITHOUT granting the turbo speed boost — used by external boosts (e.g. driving through a
    /// BoostGate) so their launch leaves the same marks. Still grounded-only, like a real turbo trail.
    /// Duration defaults to the normal turbo length so it reads just like a real boost.</summary>
    public void TriggerTurboTrail(float duration = -1f)
    {
        float d = duration > 0f ? duration : turboDuration;
        trailKickTimer = Mathf.Max(trailKickTimer, d);
    }

    /// <summary>
    /// True when both triggers are held — the LRA race-abort gesture (L+R+A). Read
    /// straight off the gamepad because Throttle is a single RT-minus-LT axis that
    /// can't distinguish both-held.
    /// </summary>
    bool BothTriggersHeld()
    {
        var gp = Gamepad.current;
        return gp != null
            && gp.leftTrigger.ReadValue() > 0.5f
            && gp.rightTrigger.ReadValue() > 0.5f;
    }

    // -------------------------------------------------------
    //  Air control (orientation + velocity, unchanged in spirit)
    // -------------------------------------------------------

    void ApplyAirBrakeGravity()
    {
        if (brakeInput < 0.05f) return;
        Vector3 extraGravity = Physics.gravity * (airBrakeGravityMultiplier - 1f) * brakeInput;
        rb.AddForce(extraGravity, ForceMode.Acceleration);
    }

    /// <summary>
    /// Extra downward gravity while airborne so the car falls faster and feels less floaty.
    /// Mass-independent (ForceMode.Acceleration) just like world gravity, and additive on top
    /// of it: gravityMultiplier = 2 means a 2x-strength fall. Stacks with the air-brake dive
    /// when braking midair.
    /// </summary>
    void ApplyGravityMultiplier()
    {
        if (gravityMultiplier <= 1f) return;
        Vector3 extraGravity = Physics.gravity * (gravityMultiplier - 1f);
        rb.AddForce(extraGravity, ForceMode.Acceleration);
    }

    /// <summary>
    /// Air resistance while airborne. Exponentially bleeds only the WORLD-horizontal velocity
    /// (x/z), leaving the vertical axis alone so gravity and falling are unaffected — the car
    /// loses its fly-across momentum but still drops at the normal rate.
    /// </summary>
    void ApplyAirDrag()
    {
        if (airDrag <= 0f) return;
        float factor = Mathf.Exp(-airDrag * Time.fixedDeltaTime);
        Vector3 vel = rb.linearVelocity;
        rb.linearVelocity = new Vector3(vel.x * factor, vel.y, vel.z * factor);
    }

    void ApplyManualPitchAndRollLeveling()
    {
        float pitchDelta = 0f;
        if (Mathf.Abs(manualPitchInput) > 0.05f)
        {
            IsManuallyPitching = true;
            pitchDelta = manualPitchInput * manualPitchSpeed * Time.fixedDeltaTime;
        }

        Quaternion pitchRot = Quaternion.AngleAxis(pitchDelta, transform.right);
        Quaternion afterPitch = pitchRot * rb.rotation;

        Vector3 euler = afterPitch.eulerAngles;
        float currentRoll = NormalizeAngle(euler.z);
        float newRoll = Mathf.MoveTowardsAngle(currentRoll, 0f, airLevelingSpeed * Time.fixedDeltaTime);

        Quaternion finalRot = Quaternion.Euler(euler.x, euler.y, newRoll);
        rb.MoveRotation(finalRot);

        Vector3 angVel = rb.angularVelocity;
        rb.angularVelocity = Vector3.up * angVel.y;
    }

    void ApplyAirLeveling()
    {
        Vector3 currentEuler = transform.eulerAngles;
        float currentPitch = NormalizeAngle(currentEuler.x);
        float currentRoll = NormalizeAngle(currentEuler.z);

        float newPitch = Mathf.MoveTowardsAngle(currentPitch, 0f, airLevelingSpeed * Time.fixedDeltaTime);
        float newRoll = Mathf.MoveTowardsAngle(currentRoll, 0f, airLevelingSpeed * Time.fixedDeltaTime);

        rb.MoveRotation(Quaternion.Euler(newPitch, currentEuler.y, newRoll));
        rb.angularVelocity = Vector3.up * rb.angularVelocity.y;
    }

    bool IsCarLevel()
    {
        float pitch = Mathf.Abs(NormalizeAngle(transform.eulerAngles.x));
        float roll = Mathf.Abs(NormalizeAngle(transform.eulerAngles.z));
        return pitch < airDriftLevelThreshold && roll < airDriftLevelThreshold;
    }

    bool IsRollLevel()
    {
        float roll = Mathf.Abs(NormalizeAngle(transform.eulerAngles.z));
        return roll < airDriftLevelThreshold;
    }

    float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }

    void ApplyAirDrift()
    {
        float tilt = Vector3.Angle(transform.up, Vector3.up);
        if (tilt > 45f) return;

        Vector3 forwardAxis = transform.forward; forwardAxis.y = 0f;
        if (forwardAxis.sqrMagnitude < 0.01f) return;
        forwardAxis.Normalize();

        Vector3 driftAxis = transform.right; driftAxis.y = 0f;
        if (driftAxis.sqrMagnitude < 0.01f) return;
        driftAxis.Normalize();

        Vector3 vel = rb.linearVelocity;
        Vector3 horizontalVel = new Vector3(vel.x, 0f, vel.z);

        float forwardSpeed = Vector3.Dot(horizontalVel, forwardAxis);
        float currentDrift = Vector3.Dot(horizontalVel, driftAxis);
        float verticalSpeed = vel.y;

        float targetDrift = steerInput * airDriftSpeed;
        float newDrift = Mathf.MoveTowards(currentDrift, targetDrift, airDriftAcceleration * Time.fixedDeltaTime);

        Vector3 newHorizontal = forwardAxis * forwardSpeed + driftAxis * newDrift;
        rb.linearVelocity = new Vector3(newHorizontal.x, verticalSpeed, newHorizontal.z);
    }

    // -------------------------------------------------------
    //  Wheel mesh visuals
    // -------------------------------------------------------

    /// <summary>
    /// Keeps each visible wheel pinned to its anchor (the WheelCollider transform,
    /// which is rigid to the body) and spins it by the car's forward speed; the front
    /// wheels also visually steer. The wheels intentionally do NOT chase the ground
    /// contact point — the body hovers at rideHeight, so following the contact would
    /// drop the wheels away from the chassis. Tune rideHeight ≈ wheel radius so the
    /// pinned wheels sit on the road.
    /// </summary>
    void UpdateWheelMeshes()
    {
        if (anchorTransforms == null) return;

        float fwdSpeed = rb != null ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;

        for (int i = 0; i < 4; i++)
        {
            Transform mesh = wheelMeshes[i];
            Transform anchor = anchorTransforms[i];
            if (mesh == null || anchor == null) continue;

            // Vertical-only suspension: rest at the hub (when the gap equals rideHeight)
            // and travel up/down with the ground, clamped — while staying locked under
            // the car horizontally. Built from the scalar gap, not a world point, so the
            // wheel reacts to bumps without ever trailing behind at speed.
            float travel = wheelGrounded[i]
                ? Mathf.Clamp(groundDistance[i] - rideHeight, -maxWheelTravel, maxWheelTravel)
                : maxWheelTravel;   // hang at full droop while airborne
            mesh.position = anchor.position - transform.up * travel;

            // Spin around the wheel's local right axis from distance travelled.
            wheelSpinAngle[i] += fwdSpeed * Time.deltaTime * 90f;
            bool front = i < 2;
            float steerVis = front ? smoothedSteer * 25f : 0f;
            mesh.rotation = transform.rotation
                          * Quaternion.Euler(0f, steerVis, 0f)
                          * Quaternion.Euler(wheelSpinAngle[i], 0f, 0f);
        }
    }

    // -------------------------------------------------------
    //  Turbo tire trails
    // -------------------------------------------------------

    /// <summary>Builds a TrailRenderer for each rear tire once at startup. They live as children of
    /// the car (so they clean up with it) but lay their marks in WORLD space, and start switched off —
    /// <see cref="UpdateTurboTrails"/> turns them on only while the turbo is firing on the ground.</summary>
    void SetUpTurboTrails()
    {
        if (!turboTrails) return;

        // One shared, alpha-blended, vertex-coloured material (no texture needed). The per-vertex
        // colour comes from the gradient below, so the trail fades out along its length.
        Material mat = turboTrailMaterial != null
            ? turboTrailMaterial
            : new Material(Shader.Find("Sprites/Default"));

        trailRL = CreateTireTrail("TurboTrailRL", mat);
        trailRR = CreateTireTrail("TurboTrailRR", mat);
    }

    TrailRenderer CreateTireTrail(string name, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        var tr = go.AddComponent<TrailRenderer>();
        tr.time = Mathf.Max(0.01f, turboTrailTime);        // seconds a mark lingers before it's gone
        tr.startWidth = turboTrailWidth;
        tr.endWidth = turboTrailWidth * 0.6f;
        tr.minVertexDistance = 0.05f;
        tr.numCornerVertices = 2;
        tr.numCapVertices = 2;
        tr.autodestruct = false;
        tr.emitting = false;                               // off until the turbo fires
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tr.receiveShadows = false;
        tr.material = mat;

        // Solid at the tire, fading to transparent at the tail over the trail's lifetime.
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(turboTrailColor, 0f), new GradientColorKey(turboTrailColor, 1f) },
            new[] { new GradientAlphaKey(turboTrailColor.a, 0f), new GradientAlphaKey(0f, 1f) });
        tr.colorGradient = grad;

        return tr;
    }

    /// <summary>Each frame, pins each rear trail to that wheel's ground-contact point and emits only
    /// while the turbo is active AND the wheel is grounded — so marks are laid on the track, never in
    /// the air. When the turbo (or ground contact) ends, emitting stops and the existing mark fades
    /// over Turbo Trail Time. The trail is cut at the start of a real jump so landing doesn't streak a
    /// line back to the take-off point.</summary>
    void UpdateTurboTrails()
    {
        if (!turboTrails) return;

        if (trailKickTimer > 0f) trailKickTimer -= Time.deltaTime;

        if (IsAirborne && !trailsWereAirborne)
        {
            if (trailRL != null) trailRL.Clear();
            if (trailRR != null) trailRR.Clear();
        }
        trailsWereAirborne = IsAirborne;

        UpdateOneTrail(trailRL, 2);   // rear-left  wheel index
        UpdateOneTrail(trailRR, 3);   // rear-right wheel index
    }

    void UpdateOneTrail(TrailRenderer tr, int wheelIndex)
    {
        if (tr == null) return;

        // Emit only while "boosting" — a real turbo, an external boost's trail kick (BoostGate), or
        // the loop speed-multiplier state (IsLoopGravityCut, the same flag that drives the loop FOV
        // kick) — AND this rear wheel is on the ground. The loop term is read-only / purely visual
        // here; the loop multiplier itself lives in the drive code and is unaffected.
        bool emit = (IsTurboActive || trailKickTimer > 0f || IsLoopGravityCut) && wheelGrounded[wheelIndex];

        // Keep the emitter parked on the contact point while it's drawing. Left in place when not
        // emitting so a one-frame bump doesn't smear a line to a far-away point on the next contact.
        if (emit)
        {
            Transform a = anchorTransforms[wheelIndex];
            if (a != null)
                tr.transform.position = a.position
                                      - transform.up * (groundDistance[wheelIndex] - turboTrailHeightOffset);
        }

        tr.emitting = emit;
    }

    // -------------------------------------------------------
    //  Tuning gizmos
    // -------------------------------------------------------

    /// <summary>
    /// Draws the four suspension probes so they can be tuned in the Scene view.
    /// Per ray: the full probe line (green = hit this frame, orange = miss / edit mode),
    /// a cyan wire sphere at the target ride height, and a solid sphere at the live
    /// contact point while playing. A yellow line from the car shows the averaged
    /// ground normal that orientation aligns to.
    /// </summary>
    void OnDrawGizmos()
    {
        Vector3 down = -transform.up;

        for (int i = 0; i < 4; i++)
        {
            Transform a = GizmoAnchor(i);
            if (a == null) continue;

            Vector3 origin = a.position;
            bool hit = Application.isPlaying
                    && wheelGrounded != null && i < wheelGrounded.Length && wheelGrounded[i];

            // Full probe length.
            Gizmos.color = hit ? Color.green : new Color(1f, 0.5f, 0f);
            Gizmos.DrawLine(origin, origin + down * suspensionRayLength);

            // Where the wheel should float (ride height) along the probe.
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(origin + down * rideHeight, 0.08f);

            // Live contact point while playing — rebuilt from the CURRENT hub so it
            // stays directly under the wheel instead of trailing behind at speed.
            if (hit && groundDistance != null && i < groundDistance.Length)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(origin + down * groundDistance[i], 0.1f);
            }
        }

        // Averaged ground normal the body aligns to (play mode, when grounded).
        if (Application.isPlaying && grounded)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, transform.position + groundNormal * 2f);
        }
    }

    /// <summary>Anchor transform for gizmos — uses the cached one while playing, and
    /// falls back to the serialized WheelCollider's transform in the editor.</summary>
    Transform GizmoAnchor(int i)
    {
        // Bounds-checked: OnDrawGizmos can run after a live script recompile, before
        // Start has (re)sized this array, so never assume it's the expected length.
        if (anchorTransforms != null && i < anchorTransforms.Length && anchorTransforms[i] != null)
            return anchorTransforms[i];

        WheelCollider wc = i == 0 ? wheelFL : i == 1 ? wheelFR : i == 2 ? wheelRL : wheelRR;
        return wc != null ? wc.transform : null;
    }
}
