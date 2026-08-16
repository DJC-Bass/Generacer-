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
/// visible". The pilot then slides it around inside that frame with <see cref="PilotOffset"/>.
///
/// The maths below is duplicated from CameraFollow rather than shared: that camera is tuned and in
/// daily use, and the two consumers want different things from it (the camera also does FOV kicks,
/// swivel and the speed-barrier muffle, none of which belong on a plane). Keep the two in sync by
/// hand if the follow feel is ever re-tuned.
///
/// DEATH: any contact downs it — a 2 s tumble under gravity, then despawn, exactly like a DronePlane
/// wreck. Detection is by TRIGGER, not collision, for two reasons: a kinematic body driven by
/// transform writes raises no collision events against static scenery (so it would sail through the
/// track), and triggers still honour the Physics collision MATRIX — so what can and can't down the
/// ship stays a Project Settings decision on the "SupportShip" layer rather than something baked in
/// here. The owner's own car is excluded in code, since the matrix cannot tell it apart from an
/// enemy's (both are on the Player layer).
///
/// Multiplayer: every machine runs its own copy glued to its own copy of the owner's car — see
/// <see cref="SupportShipReplicator"/>. Nothing about the flight is streamed; only the pilot's stick
/// offset is.
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

    [Header("Pilot Offset (hub gunner's stick)")]
    [Tooltip("How far the pilot may slide the ship LEFT/RIGHT of its resting position (units, each way).")]
    public float maxHorizontalOffset = 30f;
    [Tooltip("How far the pilot may slide the ship UP/DOWN from its resting position (units, each way).")]
    public float maxVerticalOffset = 20f;
    [Tooltip("How fast the offset moves at full stick deflection (units/second).")]
    public float offsetMoveSpeed = 22f;
    [Tooltip("Stick deflection below this is ignored, so a resting stick never drifts the ship.")]
    public float stickDeadzone = 0.15f;

    [Header("Nose Tilt")]
    [Tooltip("How far the nose banks into a turn at full sideways travel (degrees).")]
    public float maxBankAngle = 40f;
    [Tooltip("How far the nose pitches up/down when climbing or diving (degrees).")]
    public float maxNoseAngle = 18f;
    [Tooltip("Sideways/vertical speed (units/s) that produces the FULL tilt angle. Lower = twitchier.")]
    public float tiltFullRateSpeed = 20f;
    [Tooltip("Lag easing the tilt in and out (seconds). 0 = instant.")]
    public float tiltSmoothTime = 0.18f;

    [Header("Crash")]
    [Tooltip("Seconds the wreck tumbles under gravity after being downed, before it despawns.")]
    public float ragdollDuration = 2f;
    [Tooltip("Spin imparted to the wreck so it tumbles instead of sinking straight down.")]
    public float ragdollTumbleTorque = 12f;
    [Tooltip("Layers that can NEVER down the ship, on top of whatever the collision matrix already " +
             "excludes. Portal is the one that matters — flying the racer through the return portal " +
             "must not cost them their ship.")]
    public LayerMask crashIgnoreMask;
    [Tooltip("Whether THIS copy of the ship may decide it has been downed. True on the owner's own " +
             "machine and on the host (which is where enemy projectiles actually exist); false on every " +
             "other viewer, whose copy is a visual derived from an interpolated puppet and would " +
             "otherwise call phantom crashes. A spectating copy ragdolls only when told to.")]
    public bool detectCrashes = true;

    /// <summary>The car this ship escorts. Set by <see cref="Attach"/>.</summary>
    public Transform Car { get; private set; }

    /// <summary>True once it has been downed — the wreck is falling and the AI/follow is off.</summary>
    public bool IsRagdolling { get; private set; }

    /// <summary>Raised the instant it is downed, on the machine that noticed. Who reports that
    /// upstream differs by machine (see SupportShipAbility / SupportShipReplicator), so the ship
    /// itself stays ignorant of the network.</summary>
    public System.Action<SupportShip> onCrashed;

    /// <summary>The pilot's slide away from <see cref="defaultOffset"/>: x = right, y = up, in the
    /// car's lagged frame. Set directly by the replicator when a REMOTE pilot is flying, or integrated
    /// from the stick by <see cref="ApplyPilotStick"/> on the pilot's own machine. Held (not reset)
    /// when nobody is flying — an abandoned ship stays where it was left.</summary>
    public Vector2 PilotOffset
    {
        get => pilotOffset;
        set => pilotOffset = ClampOffset(value);
    }

    private Vector2 pilotOffset;
    private Rigidbody rb;
    private Quaternion smoothedRot = Quaternion.identity;
    private bool posInitialised;
    private Vector2 tilt;          // x = bank (roll), y = nose (pitch), degrees
    private Vector2 tiltVel;
    private Vector3 lastPosition;
    private Collider[] ownColliders;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        // A follower, not a flyer: physics never moves it while alive, so nothing can fight the
        // transform writes. Gravity and solidity come back only when it is downed.
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;

        ownColliders = GetComponentsInChildren<Collider>(true);
        foreach (var col in ownColliders) col.isTrigger = true;
    }

    /// <summary>Binds the ship to a car and snaps it into place. Colliders on that car are ignored for
    /// the rest of the ship's life: the ship trails through the space the car occupies on loops and
    /// reverses, and the layer matrix can't distinguish "my car" from "an enemy's car".</summary>
    public void Attach(Transform car)
    {
        Car = car;
        posInitialised = false;
        IsRagdolling = false;
        tilt = Vector2.zero;
        tiltVel = Vector2.zero;

        if (car == null) return;

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

    /// <summary>Integrates one frame of the pilot's stick into the offset. Called only on the machine
    /// whose player is actually flying it, so their own control has zero network latency; everyone
    /// else receives the resulting offset and assigns <see cref="PilotOffset"/> directly.</summary>
    public void ApplyPilotStick(Vector2 stick, float deltaTime)
    {
        float mag = stick.magnitude;
        if (mag <= stickDeadzone) return;

        // Radial deadzone, rescaled past its edge — same treatment the camera swivel gives the stick,
        // so a gentle diagonal stays diagonal instead of snapping to a cardinal.
        float t = Mathf.Clamp01((mag - stickDeadzone) / Mathf.Max(0.001f, 1f - stickDeadzone));
        Vector2 dir = stick / mag;
        PilotOffset = pilotOffset + dir * (t * offsetMoveSpeed * deltaTime);
    }

    Vector2 ClampOffset(Vector2 raw) => new Vector2(
        Mathf.Clamp(raw.x, -Mathf.Abs(maxHorizontalOffset), Mathf.Abs(maxHorizontalOffset)),
        Mathf.Clamp(raw.y, -Mathf.Abs(maxVerticalOffset), Mathf.Abs(maxVerticalOffset)));

    // LateUpdate for the same reason the camera uses it: the car has finished moving for this frame,
    // so the ship never trails a stale pose.
    void LateUpdate()
    {
        if (IsRagdolling) return;   // physics owns the wreck now
        if (Car == null) return;

        // Round preload: the track and its contents exist but are frozen until the hub portal spawns.
        // The travel baseline has to be re-zeroed each frozen frame or the first frame after the thaw
        // would read the whole frozen interval as one enormous sideways lurch and slam the tilt over.
        if (MultiplayerWorld.TrackFrozen) { lastPosition = transform.position; return; }

        UpdateSmoothedRotation();

        Vector3 offset = defaultOffset
                       + Vector3.right * pilotOffset.x
                       + Vector3.up * pilotOffset.y;
        Vector3 desired = Car.position + smoothedRot * offset;

        if (!posInitialised)
        {
            // First frame: snap, so summoning the ship doesn't fly it in from wherever it was parked.
            smoothedRot = Car.rotation;
            desired = Car.position + smoothedRot * offset;
            transform.position = desired;
            lastPosition = desired;
            posInitialised = true;
        }

        UpdateTilt(desired);
        transform.position = desired;
        transform.rotation = smoothedRot * Quaternion.Euler(tilt.y, 0f, -tilt.x);
        lastPosition = desired;
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

    /// <summary>Banks the nose into the turn and pitches it into a climb/dive. Derived from the ship's
    /// ACTUAL travel through the car's frame, not from the stick, so it tilts both when the pilot
    /// slides it sideways AND when the car corners hard enough to swing it around — which is what
    /// makes an unpiloted ship still look like it's flying rather than being dragged.</summary>
    void UpdateTilt(Vector3 desired)
    {
        float dt = Time.deltaTime;
        Vector2 target = Vector2.zero;

        if (dt > 1e-5f)
        {
            // Travel expressed in the ship's own lagged frame: sideways drives bank, vertical the nose.
            Vector3 travel = Quaternion.Inverse(smoothedRot) * ((desired - lastPosition) / dt);
            float full = Mathf.Max(tiltFullRateSpeed, 0.01f);
            target.x = Mathf.Clamp(travel.x / full, -1f, 1f) * maxBankAngle;
            target.y = -Mathf.Clamp(travel.y / full, -1f, 1f) * maxNoseAngle;   // climbing = nose up
        }

        tilt = tiltSmoothTime <= 0f
            ? target
            : Vector2.SmoothDamp(tilt, target, ref tiltVel, tiltSmoothTime);
    }

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

        // Eat the shot that killed us, so a projectile doesn't sail on through and hit the racer too.
        // (The ship's colliders are triggers, so the projectile's own collision handler never fires.)
        var projectile = other.GetComponentInParent<DroneProjectile>();
        if (projectile != null) Destroy(projectile.gameObject);

        Crash();
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
        Gizmos.DrawWireCube(Vector3.zero,
            new Vector3(Mathf.Abs(maxHorizontalOffset) * 2f, Mathf.Abs(maxVerticalOffset) * 2f, 0.1f));
        Gizmos.matrix = Matrix4x4.identity;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(anchor.position, centre);
    }
}
