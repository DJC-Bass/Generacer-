using UnityEngine;

/// <summary>
/// The SUPPORT SHIP: a small plane that flies escort on a player car, and which a TEAMMATE sitting in
/// the HUB world can steer around that car from the <see cref="PilotControlCenter"/>. The racer is the
/// rail; the hub player is the gunner.
///
/// It is deliberately NOT a physics flyer like <see cref="DronePlane"/>. It is a FOLLOWER, and the
/// follow model is the game's own chase camera: the same lazy-Susan lag per axis (yaw / pitch / roll)
/// that <see cref="CameraFollow"/> uses, placing <see cref="defaultOffset"/> inside that lagged frame.
/// That is not a coincidence — the ship's authored position on the car prefab IS the camera's offset,
/// so an unpiloted ship sits exactly where the driver's viewpoint is and reads as "the camera, made
/// visible". The pilot then slides it around inside a 3D box in that frame (<see cref="PilotOffset"/>)
/// and angles it independently (<see cref="PilotLook"/>) to aim the guns.
///
/// The maths below is duplicated from CameraFollow rather than shared: that camera is tuned and in
/// daily use, and the two consumers want different things from it (the camera also does FOV kicks,
/// swivel and the speed-barrier muffle, none of which belong on a plane). Keep the two in sync by
/// hand if the follow feel is ever re-tuned.
///
/// DEATH: contacts spend a HEALTH POOL (<see cref="maxHits"/>, 5 by default) rather than downing it
/// outright — drone fire, scenery, cars and other ships all draw on the same pool, with separate
/// cooldowns for incoming FIRE and for ENVIRONMENTAL scrapes. Running it dry starts a 2 s tumble under
/// gravity, then despawn, exactly like a DronePlane wreck. Detection is by TRIGGER, not collision, for
/// two reasons: a kinematic body driven by transform writes raises no collision events against static
/// scenery (so it would sail through the track), and triggers still honour the Physics collision MATRIX
/// — so what can and can't damage the ship stays a Project Settings decision on the "SupportShip" layer
/// rather than something baked in here. The owner's own car is excluded in code, since the matrix
/// cannot tell it apart from an enemy's (both are on the Player layer).
///
/// ⚠️ Only ONE machine counts those hits — the HOST (see <see cref="detectCrashes"/> and where
/// SupportShipAbility sets it). Two counters would diverge, because the host and the owner see
/// overlapping but different hit sets.
///
/// Multiplayer: every machine runs its own copy glued to its own copy of the owner's car — see
/// <see cref="SupportShipReplicator"/>. Nothing about the flight is streamed; only the pilot's offset
/// and aim angles are.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
// Ahead of the cameras (which are unordered, i.e. 0) so the pilot's chase camera always frames the
// ship's pose for THIS frame rather than trailing it by one.
[DefaultExecutionOrder(-50)]
public class SupportShip : MonoBehaviour
{
    [Header("Follow (mirrors CameraFollow)")]
    [Tooltip("Resting position in the car's lagged local frame. Left at the authored value this is the " +
             "chase camera's own offset, so the ship flies where the driver's viewpoint is.")]
    public Vector3 defaultOffset = new Vector3(0f, 2.5f, -7f);
    [Tooltip("Lazy-Susan YAW lag: how slowly the ship's heading orbits to follow the car's. " +
             "0 = locked behind the car; higher = lazier.")]
    public float positionSmoothTime = 0.5f;
    [Tooltip("PITCH lag: how slowly the ship follows the car's climbs and dives. 0 = instant.")]
    public float pitchSmoothTime = 0.25f;
    [Tooltip("ROLL lag: how slowly the ship follows the car's banking. 0 = instant.")]
    public float rollSmoothTime = 0.25f;

    [Header("Pilot Movement Box (hub gunner's controls)")]
    [Tooltip("How far the pilot may slide the ship LEFT/RIGHT of its resting position (units, each way).")]
    public float maxHorizontalOffset = 30f;
    [Tooltip("How far the pilot may slide the ship UP/DOWN from its resting position (units, each way).")]
    public float maxVerticalOffset = 20f;
    [Tooltip("How far the pilot may push the ship FORWARD/BACK of its resting position (units, each " +
             "way), on B and X. Set all three equal for a true cube.")]
    public float maxForwardOffset = 20f;
    [Tooltip("How fast the offset moves at full deflection (units/second). Uniform on every axis, and " +
             "unaffected by the aim angles — turning the ship never speeds it up or slows it down.")]
    public float offsetMoveSpeed = 22f;
    [Tooltip("Stick deflection below this is ignored, so a resting stick never drifts the ship.")]
    public float stickDeadzone = 0.15f;
    [Tooltip("Flips the pilot's vertical axis. ON (default): pushing the stick UP drops the ship and " +
             "pulling DOWN climbs it — the classic flight-stick feel, where you're nosing the aircraft " +
             "rather than pointing at where you want it to go. OFF: stick up = ship up.")]
    public bool invertPilotVertical = true;

    [Header("Aim Rotation")]
    [Tooltip("Local Y rotation at full sideways push (degrees). Pushing right turns the nose right. " +
             "This is what widens the arc of fire: the guns shoot along the nose, so angling the ship " +
             "lets the pilot cover ground the ship itself can't reach inside its movement box.")]
    public float maxYawAngle = 45f;
    [Tooltip("Local X rotation at full vertical push (degrees). Applied NEGATIVE when climbing and " +
             "POSITIVE when descending, which is Unity's convention for nose-up / nose-down.")]
    public float maxPitchAngle = 35f;
    [Tooltip("Local Z rotation held while a bumper is down (degrees). RB rolls RIGHT, LB rolls LEFT. " +
             "Holding BOTH cancels to zero, so the ship levels out mid-roll. If the roll comes out " +
             "mirrored on your model, negate this value.")]
    public float maxRollAngle = 80f;
    [Tooltip("Lag easing the aim angles in, and easing them back to level (0,0,0) when the controls are " +
             "released. 0 = instant/snappy.")]
    public float lookSmoothTime = 0.12f;

    [Header("Guns")]
    [Tooltip("The twin-laser round prefab (one object containing BOTH bolts). Blank = the ship can't " +
             "shoot. Must be on the Projectile layer — the layer is re-applied on spawn anyway.")]
    public GameObject laserPrefab;
    [Tooltip("Where rounds spawn, in the ship's own local frame. Push it forward far enough to clear " +
             "the ship's own model.")]
    public Vector3 muzzleOffset = new Vector3(0f, 0f, 3f);
    [Tooltip("Round speed in m/s. Fast — this is a laser, not a lobbed shot.")]
    public float laserSpeed = 700f;
    [Tooltip("Layer applied to spawned rounds. Blank = keep the prefab's layer.")]
    public string laserLayerName = "Projectile";

    [Header("Durability")]
    [Tooltip("Contacts the ship survives before going down. Every source shares the one pool — drone " +
             "fire, scenery, cars, other ships alike.")]
    public int maxHits = 5;
    [Tooltip("Minimum seconds between two PROJECTILE hits. A drone burst arrives faster than the ship " +
             "can react, so without this a single volley would empty the pool in a fraction of a second " +
             "and the health may as well not exist.")]
    public float projectileHitCooldown = 0.35f;
    [Tooltip("Minimum seconds between two ENVIRONMENTAL hits (scenery, cars, other ships). One physical " +
             "impact raises several trigger contacts as the ship bounces and scrapes along, and each " +
             "would otherwise be a separate point of damage.")]
    public float collisionHitCooldown = 0.35f;
    [Tooltip("Layer used to tell a PROJECTILE hit from an ENVIRONMENTAL one, so the two cooldowns above " +
             "can differ. Blank = everything counts as environmental.")]
    public string projectileLayerName = "Projectile";

    [Header("Crash")]
    [Tooltip("Seconds the wreck tumbles under gravity after being downed, before it despawns.")]
    public float ragdollDuration = 2f;
    [Tooltip("Spin imparted to the wreck so it tumbles instead of sinking straight down.")]
    public float ragdollTumbleTorque = 12f;
    [Tooltip("Layers that can NEVER down the ship, on top of whatever the collision matrix already " +
             "excludes. Portal is the one that matters — flying the racer through the return portal " +
             "must not cost them their ship.")]
    public LayerMask crashIgnoreMask;
    [Tooltip("A single frame's car movement beyond this (units) is read as a TELEPORT rather than " +
             "travel, and the ship re-snaps instead of banking. Hub↔track travel moves the car ~100 km " +
             "in one frame, so anything comfortably above a real frame of driving works.")]
    public float teleportDistance = 500f;

    [Tooltip("Whether THIS copy of the ship may decide it has been downed. True on the owner's own " +
             "machine and on the host (which is where enemy projectiles actually exist); false on every " +
             "other viewer, whose copy is a visual derived from an interpolated puppet and would " +
             "otherwise call phantom crashes. A spectating copy ragdolls only when told to.")]
    public bool detectCrashes = true;

    /// <summary>Which player's ship this is. Stamped at build time by whoever created it, so the copy
    /// that counts hits can name itself when reporting damage to the other machines.</summary>
    [HideInInspector] public ulong ownerClientId;

    /// <summary>The car this ship escorts. Set by <see cref="Attach"/>.</summary>
    public Transform Car { get; private set; }

    /// <summary>True once it has been downed — the wreck is falling and the AI/follow is off.</summary>
    public bool IsRagdolling { get; private set; }

    /// <summary>The ship's LEVEL-FLIGHT frame: the lagged car-following rotation, WITHOUT the pilot's
    /// aim angles. This is what the ship's rotation is built on — its actual transform.rotation is this
    /// times <see cref="PilotLook"/>.
    ///
    /// Exposed for the pilot's chase camera, which follows the ship's POSITION but must ignore its aim:
    /// if the camera swung with the yaw, angling the ship to shoot left would just drag the whole view
    /// left and nothing would appear to have been aimed. Framing on this instead keeps the camera
    /// pointed down the car's heading while the ship visibly angles inside the shot — the Star Fox
    /// arrangement, and the thing that makes the widened arc of fire readable.</summary>
    public Quaternion FollowFrame => smoothedRot;

    /// <summary>Where the ship sits INSIDE <see cref="FollowFrame"/>: the resting offset plus whatever
    /// the pilot has slid it to. This — not the world position — is the thing that only changes when
    /// the PILOT does something; the car hurtling down the track moves the frame, not this.
    ///
    /// That distinction is what the pilot's camera smooths against, so the chase looks identical at a
    /// standstill and at 600 mph. Smoothing the world position instead makes the trail proportional to
    /// the car's speed, which reads as the camera being dragged rather than as the ship being flown.</summary>
    public Vector3 LocalOffset => defaultOffset
                                + Vector3.right * pilotOffset.x
                                + Vector3.up * pilotOffset.y
                                + Vector3.forward * pilotOffset.z;

    /// <summary>Raised the instant it is downed, on the machine that noticed. Who reports that
    /// upstream differs by machine (see SupportShipAbility / SupportShipReplicator), so the ship
    /// itself stays ignorant of the network.</summary>
    public System.Action<SupportShip> onCrashed;

    /// <summary>The pilot's slide away from <see cref="defaultOffset"/> inside the movement box:
    /// x = right, y = up, z = forward, in the car's lagged frame. Set directly by the replicator when a
    /// REMOTE pilot is flying, or integrated from the controls by <see cref="ApplyPilotMove"/> on the
    /// pilot's own machine. Held (not reset) when nobody is flying — an abandoned ship stays where it
    /// was left.</summary>
    public Vector3 PilotOffset
    {
        get => pilotOffset;
        set => pilotOffset = ClampOffset(value);
    }

    /// <summary>The pilot's aim angles in degrees: x = yaw (local Y), y = pitch (local X), z = roll
    /// (local Z, on the bumpers). Replicated
    /// alongside the offset rather than derived from the ship's motion, because the two deliberately
    /// disagree — a pilot pinned against the movement box keeps AIMING the way they're pushing while
    /// no longer MOVING that way, and nothing about the ship's travel could reproduce that.</summary>
    public Vector3 PilotLook
    {
        get => pilotLook;
        set { pilotLook = value; pilotLookTarget = value; pilotLookVel = Vector3.zero; }
    }

    private Vector3 pilotOffset;
    private Vector3 pilotLook;         // current, smoothed
    private Vector3 pilotLookTarget;   // what the pilot's controls are asking for
    private Vector3 pilotLookVel;
    private Rigidbody rb;
    private Quaternion smoothedRot = Quaternion.identity;
    private bool posInitialised;
    private Vector3 lastCarPosition;
    private Collider[] ownColliders;
    private int hitsTaken;                       // health pool spent so far (see maxHits)
    private float lastProjectileHitTime = -999f;
    private float lastCollisionHitTime = -999f;
    private DroneDamageTint damageTint;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        // A follower, not a flyer: physics never moves it while alive, so nothing can fight the
        // transform writes. Gravity and solidity come back only when it is downed.
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        // Kinematic bodies only support Discrete and ContinuousSpeculative — leaving the prefab on
        // Continuous/ContinuousDynamic makes Unity complain every time this runs. Speculative is also
        // the right choice for us: it's the one that still catches a fast pass through a trigger.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        ownColliders = GetComponentsInChildren<Collider>(true);
        foreach (var col in ownColliders) col.isTrigger = true;
    }

    /// <summary>Binds the ship to a car and snaps it into place. Colliders on that car are ignored for
    /// the rest of the ship's life: the ship trails through the space the car occupies on loops and
    /// reverses, and the layer matrix can't distinguish "my car" from "an enemy's car".</summary>
    public void Attach(Transform car)
    {
        Car = car;
        posInitialised = false;   // re-snap onto the new car rather than flying over to it
        IsRagdolling = false;
        PilotLook = Vector3.zero;   // a freshly attached ship starts level

        if (car == null) return;
        lastCarPosition = car.position;

        // ACTIVE colliders only: Physics.IgnoreCollision errors out on a collider whose object is
        // disabled, and a car is full of those (the shield, jet flames, SD effects, the ship template
        // itself). Nothing is lost — a trigger overlap with any of them is caught by the hierarchy
        // check in OnTriggerEnter; this pass exists for the RAGDOLL, where the wreck turns solid again
        // and must not knock its own racer off the road on the way down.
        var carColliders = car.GetComponentsInChildren<Collider>(false);
        foreach (var mine in ownColliders)
        {
            if (mine == null || !mine.gameObject.activeInHierarchy) continue;
            foreach (var theirs in carColliders)
                if (theirs != null && theirs.gameObject.activeInHierarchy)
                    Physics.IgnoreCollision(mine, theirs, true);
        }
    }

    /// <summary>Copies every tuning value off another SupportShip — used for a REMOTE player's ship,
    /// which cannot inherit them.
    ///
    /// Why it's needed: a remote ship is cloned from the template on that player's PUPPET, and
    /// `RemoteCarManager.StripPuppet` destroys every MonoBehaviour on a puppet — so the template there
    /// has no SupportShip component and the clone gets a freshly `AddComponent`ed one carrying nothing
    /// but code defaults. Anything tuned in the Inspector (movement box, speed, aim angles, ragdoll) would
    /// silently apply to your own ship and not to anyone else's, so a teammate's ship would fly
    /// differently from your own. Feeding it the untouched PREFAB ASSET's values fixes that — the same
    /// trick RemoteCarEffects/RemoteCarAudio use for engine sound and VFX.
    ///
    /// <see cref="defaultOffset"/> is NOT copied (it's derived from where the template actually sits on
    /// that car) and neither is <see cref="detectCrashes"/>, which is a per-machine authority decision.</summary>
    public void CopyTuningFrom(SupportShip src)
    {
        if (src == null) return;

        positionSmoothTime = src.positionSmoothTime;
        pitchSmoothTime = src.pitchSmoothTime;
        rollSmoothTime = src.rollSmoothTime;

        maxHorizontalOffset = src.maxHorizontalOffset;
        maxVerticalOffset = src.maxVerticalOffset;
        offsetMoveSpeed = src.offsetMoveSpeed;
        stickDeadzone = src.stickDeadzone;
        invertPilotVertical = src.invertPilotVertical;

        maxForwardOffset = src.maxForwardOffset;
        maxYawAngle = src.maxYawAngle;
        maxPitchAngle = src.maxPitchAngle;
        maxRollAngle = src.maxRollAngle;
        lookSmoothTime = src.lookSmoothTime;

        // The health pool especially: the HOST's copy of a remote ship is the one that counts hits,
        // and it is built by BuildShip + CopyTuningFrom — so without these it would count against code
        // defaults and ignore whatever the prefab was tuned to.
        maxHits = src.maxHits;
        projectileHitCooldown = src.projectileHitCooldown;
        collisionHitCooldown = src.collisionHitCooldown;
        projectileLayerName = src.projectileLayerName;

        ragdollDuration = src.ragdollDuration;
        ragdollTumbleTorque = src.ragdollTumbleTorque;
        crashIgnoreMask = src.crashIgnoreMask;
        teleportDistance = src.teleportDistance;

        // The gun too — laserPrefab especially. A puppet-cloned ship has NO prefab reference at all, so
        // without this a teammate's ship would be completely unarmed.
        laserPrefab = src.laserPrefab;
        muzzleOffset = src.muzzleOffset;
        laserSpeed = src.laserSpeed;
        laserLayerName = src.laserLayerName;
    }

    /// <summary>Integrates one frame of the pilot's controls. Called only on the machine whose player
    /// is actually flying, so their own input has zero network latency; everyone else receives the
    /// resulting offset and angles and assigns them directly.
    ///
    /// <paramref name="move"/> is the intent, in the car's frame: x = slide right, y = climb (left
    /// stick), z = push forward (B/X); `roll` is the bumpers. It does TWO independent things, and
    /// keeping them independent is the point of this control scheme:
    ///  • It slides the offset at a UNIFORM speed, clamped to the movement box.
    ///  • It sets the aim angles — yaw from the sideways push, pitch from the vertical one, roll from
    ///    <paramref name="roll"/> (RB/LB, +1/-1, both = 0) — from the CONTROLS,
    ///    not from whether the ship actually moved. So a pilot pinned against the edge of the
    ///    box keeps pointing the way they're pushing, which widens the arc of fire from a hard corner
    ///    of the box instead of leaving them stuck facing straight ahead.</summary>
    public void ApplyPilotMove(Vector3 move, float roll, float deltaTime)
    {
        Vector2 stick = new Vector2(move.x, move.y);
        // Flipped before the deadzone maths, which only reads the stick's LENGTH and direction — a sign
        // change leaves the magnitude untouched, so the radial deadzone and diagonals are unaffected.
        if (invertPilotVertical) stick.y = -stick.y;

        // Radial deadzone, rescaled past its edge — same treatment the camera swivel gives the stick,
        // so a gentle diagonal stays diagonal instead of snapping to a cardinal. `planar` comes out as
        // a 0..1 push in the direction the pilot means, and drives BOTH the slide and the angles, which
        // is what lets a light push pick a shallow angle and a full push the maximum.
        float mag = stick.magnitude;
        Vector2 planar = Vector2.zero;
        if (mag > stickDeadzone)
        {
            float t = Mathf.Clamp01((mag - stickDeadzone) / Mathf.Max(0.001f, 1f - stickDeadzone));
            planar = (stick / mag) * t;
        }

        // B/X are buttons, so depth is already a clean -1/0/+1 and needs no deadzone.
        float depth = Mathf.Clamp(move.z, -1f, 1f);

        PilotOffset = pilotOffset + new Vector3(planar.x, planar.y, depth) * (offsetMoveSpeed * deltaTime);

        // Yaw follows the slide (push right, nose right). Pitch is NEGATIVE going up, per the spec —
        // which is also Unity's convention, where a negative X rotation raises the nose.
        // Roll is a pure AIM angle — it never moves the ship, only banks it. RB gives +1, LB -1, and
        // both together give 0, which is exactly the "they cancel out" rule: the target drops to level
        // and the ship smooths back out of whatever roll it was in.
        pilotLookTarget = new Vector3(planar.x * maxYawAngle,
                                      -planar.y * maxPitchAngle,
                                      Mathf.Clamp(roll, -1f, 1f) * maxRollAngle);
    }

    Vector3 ClampOffset(Vector3 raw) => new Vector3(
        Mathf.Clamp(raw.x, -Mathf.Abs(maxHorizontalOffset), Mathf.Abs(maxHorizontalOffset)),
        Mathf.Clamp(raw.y, -Mathf.Abs(maxVerticalOffset), Mathf.Abs(maxVerticalOffset)),
        Mathf.Clamp(raw.z, -Mathf.Abs(maxForwardOffset), Mathf.Abs(maxForwardOffset)));

    // LateUpdate for the same reason the camera uses it: the car has finished moving for this frame,
    // so the ship never trails a stale pose.
    void LateUpdate()
    {
        if (IsRagdolling) return;   // physics owns the wreck now
        if (Car == null) return;

        // NOTE: deliberately NOT gated on MultiplayerWorld.TrackFrozen, unlike DroneCar/DronePlane.
        // That flag means "the preloaded TrackScene's AI must hold still until the hub portal spawns",
        // and it only applies to things that LIVE in the track. This ship escorts a PLAYER CAR, and
        // during preload every player is still in the hub driving around normally — so honouring the
        // freeze pinned the ship in mid-air while its car drove off, then snapped it back at GO.

        // Moving between the hub and the track is a ~100 km TELEPORT of the car, not travel. Treated as
        // movement the rotation lag would unwind over the following second; treated as a teleport it
        // simply re-snaps, which is what the camera does too.
        if (posInitialised && (Car.position - lastCarPosition).sqrMagnitude > teleportDistance * teleportDistance)
            posInitialised = false;
        lastCarPosition = Car.position;

        UpdateSmoothedRotation();
        UpdateLook();

        Vector3 offset = LocalOffset;
        Vector3 desired = Car.position + smoothedRot * offset;

        if (!posInitialised)
        {
            // First frame (or a teleport): snap everything, so summoning the ship doesn't fly it in
            // from wherever it was parked and an area change doesn't throw the aim.
            smoothedRot = Car.rotation;
            desired = Car.position + smoothedRot * offset;
            transform.position = desired;
            posInitialised = true;
        }

        transform.position = desired;
        // Aim rides INSIDE the car-following frame, so the angles the pilot dials in are relative to
        // the ship's own level flight rather than to the world — a banked car doesn't skew the aim.
        // Euler order is (pitch about local X, yaw about local Y, no roll).
        transform.rotation = smoothedRot * Quaternion.Euler(pilotLook.y, pilotLook.x, pilotLook.z);
    }

    /// <summary>Eases the ship's reference rotation toward the car's with an independent lag per axis.
    /// Each correction is applied around its own axis from a signed angle, which stays robust through
    /// steep hills and loops where Euler angles would flip. (Lifted from CameraFollow.)</summary>
    void UpdateSmoothedRotation()
    {
        Quaternion targetRot = Car.rotation;

        // Yaw — ease heading around WORLD up. Skipped when the car's forward is near-vertical (a loop
        // apex), where heading is undefined, so the ship holds its bearing there instead of spinning.
        Vector3 smFwdH = Vector3.ProjectOnPlane(smoothedRot * Vector3.forward, Vector3.up);
        Vector3 tgFwdH = Vector3.ProjectOnPlane(targetRot * Vector3.forward, Vector3.up);
        if (smFwdH.sqrMagnitude > 1e-5f && tgFwdH.sqrMagnitude > 1e-5f)
        {
            float yawErr = Vector3.SignedAngle(smFwdH, tgFwdH, Vector3.up);
            smoothedRot = Quaternion.AngleAxis(yawErr * Approach(positionSmoothTime), Vector3.up) * smoothedRot;
        }

        Vector3 smRight = smoothedRot * Vector3.right;
        float pitchErr = Vector3.SignedAngle(smoothedRot * Vector3.forward, targetRot * Vector3.forward, smRight);
        smoothedRot = Quaternion.AngleAxis(pitchErr * Approach(pitchSmoothTime), smRight) * smoothedRot;

        Vector3 smFwd = smoothedRot * Vector3.forward;
        float rollErr = Vector3.SignedAngle(smoothedRot * Vector3.up, targetRot * Vector3.up, smFwd);
        smoothedRot = Quaternion.AngleAxis(rollErr * Approach(rollSmoothTime), smFwd) * smoothedRot;
    }

    // Exponential approach factor (0-1) for a smooth time. 0 = instant (snaps each frame).
    static float Approach(float smoothTime) =>
        smoothTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(smoothTime, 1e-5f));

    /// <summary>Eases the aim angles toward whatever the pilot's stick is asking for — and, when the
    /// stick is centred (or nobody is flying at all), back to a level 0,0. Driven by the TARGET rather
    /// than by the ship's actual travel, which is the whole point of the rework: holding into the wall
    /// of the movement box keeps the ship banked that way even though it has stopped moving.</summary>
    void UpdateLook()
    {
        pilotLook = lookSmoothTime <= 0f
            ? pilotLookTarget
            : Vector3.SmoothDamp(pilotLook, pilotLookTarget, ref pilotLookVel, lookSmoothTime);
    }

    // -------------------------------------------------------
    //  Guns
    // -------------------------------------------------------

    /// <summary>Fires one twin-bolt round straight along the ship's nose.
    ///
    /// ⚠️ Call this on the AUTHORITY only — <see cref="SupportShipReplicator.RequestFire"/> routes a
    /// pilot's trigger pull to the host and calls it there, so the round is simulated once, on the
    /// machine that owns the drones it might hit, and streamed to everyone else. Calling it directly on
    /// a client spawns a round that exists on that screen alone.
    ///
    /// Direction is <c>transform.forward</c>, which includes the pilot's AIM ANGLES — so a ship yawed
    /// left shoots left. That is the whole reason the aim angles exist: they widen the arc of fire well
    /// beyond the movement box, including from a corner of it the ship cannot slide past.</summary>
    public void FireLaser(ulong pilotClientId, bool pilotIsLocal)
    {
        if (laserPrefab == null || IsRagdolling) return;

        Vector3 origin = transform.TransformPoint(muzzleOffset);
        Vector3 direction = transform.forward;

        GameObject round = Instantiate(laserPrefab, origin, transform.rotation);
        ApplyLayer(round, laserLayerName);

        var laser = round.GetComponent<SupportShipLaser>();
        if (laser == null) laser = round.AddComponent<SupportShipLaser>();

        // Never shoot ourselves — a round spawning inside our own muzzle would die on the spot. The
        // OWNER'S CAR is deliberately NOT excluded: the ship flies behind it, so the racer is squarely
        // in the line of fire and keeping them out of it is the pilot's job, not the code's.
        laser.IgnoreCollisionsWith(transform);
        laser.pilotClientId = pilotClientId;
        laser.pilotIsLocal = pilotIsLocal;
        laser.Launch(direction, laserSpeed);

        AudioManager.PlaySupportShipLaserFire(origin);

        // Host only (no-op elsewhere): stream it to the clients as a visual, hits staying host-side.
        NpcReplicator.Track(round, NpcKind.Projectile, laserPrefab);
    }

    void ApplyLayer(GameObject go, string layerName)
    {
        if (go == null || string.IsNullOrEmpty(layerName)) return;
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
        {
            if (!warnedMissingLaserLayer)
            {
                warnedMissingLaserLayer = true;
                Debug.LogWarning($"[SupportShip] Layer '{layerName}' not found in Tags and Layers — " +
                                 "laser rounds keep the prefab's layer.");
            }
            return;
        }
        SetLayerRecursively(go, layer);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    private bool warnedMissingLaserLayer;

    // -------------------------------------------------------
    //  Being downed
    // -------------------------------------------------------

    /// <summary>Any contact the collision matrix permits downs the ship. Triggers rather than
    /// collisions — see the class comment for why. The owner's own car is already excluded via
    /// Physics.IgnoreCollision in <see cref="Attach"/>, but that only suppresses COLLISIONS, not
    /// trigger overlaps, so it is re-checked here by hierarchy.</summary>
    void OnTriggerEnter(Collider other)
    {
        if (IsRagdolling || other == null || !detectCrashes) return;
        if (((1 << other.gameObject.layer) & crashIgnoreMask.value) != 0) return;
        if (BelongsToCar(other.transform)) return;

        // Eat the shot regardless of whether it registers as damage, so a projectile never sails on
        // through to the racer behind. (The ship's colliders are triggers, so the projectile's own
        // collision handler never fires against it.)
        var projectile = other.GetComponentInParent<DroneProjectile>();
        if (projectile != null) Destroy(projectile.gameObject);

        TakeHit(IsProjectile(other));
    }

    /// <summary>Is this contact incoming FIRE rather than scenery? Only used to pick which cooldown
    /// applies — the two share one health pool.</summary>
    bool IsProjectile(Collider other)
    {
        if (string.IsNullOrEmpty(projectileLayerName)) return false;
        int layer = LayerMask.NameToLayer(projectileLayerName);
        return layer >= 0 && other.gameObject.layer == layer;
    }

    /// <summary>Spends one point of the health pool, and downs the ship when it runs out.
    ///
    /// Both sources are rate-limited, and for the same reason from opposite directions: one physical
    /// impact raises several trigger contacts as the ship scrapes along, and a drone burst arrives
    /// faster than any pilot could react to. Without the cooldowns a five-point pool empties in a few
    /// frames either way and may as well not exist. They are SEPARATE knobs because a volley and a
    /// scrape have nothing to do with each other's timing.</summary>
    void TakeHit(bool fromProjectile)
    {
        float cooldown = fromProjectile ? projectileHitCooldown : collisionHitCooldown;
        ref float last = ref (fromProjectile ? ref lastProjectileHitTime : ref lastCollisionHitTime);

        if (Time.time - last < cooldown) return;
        last = Time.time;

        if (++hitsTaken < Mathf.Max(1, maxHits))
        {
            // Survived: a flash, and nothing more. Only the counting machine reaches here, so it
            // also tells the others — every other copy of this ship has no idea it was touched.
            ApplyDamageFeedback(hitsTaken, maxHits);
            SupportShipReplicator.ReportShipDamage(ownerClientId, hitsTaken, maxHits);
            return;
        }
        Crash();
    }

    /// <summary>Flash this copy of the ship and play the survivable-hit sound on it. Public because
    /// the copies that DON'T count hits (every machine but the host) are driven from the replicated
    /// damage report instead — which is exactly why the audio belongs HERE and not in TakeHit. Every
    /// machine runs this once per hit and only once, so putting it here is what makes the sound audible
    /// to the racer being escorted and to anyone nearby, rather than only on the host.
    ///
    /// Non-fatal hits ONLY. The killing blow never reaches here — it goes to Crash(), which plays the
    /// destroyed sound instead, so the two never overlap.
    ///
    /// The component is ADDED if the prefab doesn't carry one, so feedback needs no editor wiring — put
    /// a DroneDamageTint on the SupportShip prefab only to TUNE it, and those values then win.</summary>
    public void ApplyDamageFeedback(int hits, int max)
    {
        if (damageTint == null)
        {
            damageTint = GetComponentInChildren<DroneDamageTint>(true);
            if (damageTint == null) damageTint = gameObject.AddComponent<DroneDamageTint>();
        }
        damageTint.RegisterHit(hits, Mathf.Max(1, max));
        AudioManager.PlaySupportShipHit(transform.position);
    }

    /// <summary>Paints the wreck red and holds it for the ragdoll.
    ///
    /// Needs no message of its own, unlike the drones': a downed ship's verdict is already fanned out
    /// as GNRC_SHIP_DOWN and every machine runs this same Crash() on its own copy, so calling it here
    /// puts the tint on all of them for free.</summary>
    void ShowDowned()
    {
        if (damageTint == null)
        {
            damageTint = GetComponentInChildren<DroneDamageTint>(true);
            if (damageTint == null) damageTint = gameObject.AddComponent<DroneDamageTint>();
        }
        damageTint.MarkDowned();
    }

    /// <summary>Copies tint tuning onto this ship's (possibly auto-added) component. Used for a REMOTE
    /// copy, cloned from a puppet whose scripts were stripped — without this it would flash in code
    /// defaults while the prefab said something else.</summary>
    public void SeedDamageTint(DroneDamageTint authored)
    {
        if (authored == null) return;
        if (damageTint == null)
        {
            damageTint = GetComponentInChildren<DroneDamageTint>(true);
            if (damageTint == null) damageTint = gameObject.AddComponent<DroneDamageTint>();
        }
        damageTint.CopyTuningFrom(authored);
    }

    bool BelongsToCar(Transform t)
    {
        if (Car == null) return false;
        while (t != null)
        {
            if (t == Car) return true;
            t = t.parent;
        }
        return false;
    }

    /// <summary>Goes limp: gravity on, solid again so the wreck tumbles off scenery instead of
    /// falling through it, and despawned after <see cref="ragdollDuration"/>. Idempotent — the first
    /// contact wins and later ones are ignored.</summary>
    public void Crash()
    {
        if (IsRagdolling) return;
        IsRagdolling = true;

        foreach (var col in ownColliders)
            if (col != null) col.isTrigger = false;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;
        rb.AddTorque(Random.onUnitSphere * ragdollTumbleTorque, ForceMode.VelocityChange);

        ShowDowned();
        AudioManager.PlaySupportShipDestroyed(transform.position);

        onCrashed?.Invoke(this);
        Destroy(gameObject, ragdollDuration);
    }

    // -------------------------------------------------------
    //  Scene-view gizmos
    // -------------------------------------------------------

    /// <summary>Draws the box the pilot may slide the ship around inside, anchored at the resting
    /// offset, so the movement limits can be tuned against the actual car without playing.</summary>
    void OnDrawGizmosSelected()
    {
        Transform anchor = Car != null ? Car : transform.parent;
        if (anchor == null) return;

        Quaternion frame = Application.isPlaying && Car != null ? smoothedRot : anchor.rotation;
        Vector3 centre = anchor.position + frame * defaultOffset;

        Gizmos.color = new Color(0.35f, 0.9f, 1f, 0.9f);
        Gizmos.matrix = Matrix4x4.TRS(centre, frame, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(Mathf.Abs(maxHorizontalOffset) * 2f,
                                                      Mathf.Abs(maxVerticalOffset) * 2f,
                                                      Mathf.Abs(maxForwardOffset) * 2f));
        Gizmos.matrix = Matrix4x4.identity;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(anchor.position, centre);
    }
}
