using UnityEngine;

/// <summary>
/// A patrolling DRONE PLANE: an airborne hunter that circles its own patch of sky, and dives on any
/// player car it spots — chasing and strafing them with the same vision-cone + projectile fire the
/// DroneCar uses, but flying in full 3D so it can follow a car up hills, through loops and off jumps.
///
/// Three states:
///  • PATROL — flies a horizontal circle around its spawn point at <see cref="patrolSpeed"/>, holding
///    its spawn altitude (the spawner varies that per plane, so some are skyline and some buzz low).
///    It chases a point a short way AHEAD of its own bearing on that ring, sprinting while still off it
///    and pointing its nose along its real velocity — see <see cref="Patrol"/> for why all three of
///    those matter.
///  • CHASE — on spotting a player, locks on and pursues, HOLDING <see cref="standoffDistance"/> and
///    hovering <see cref="chaseHeightOffset"/> above them so it strafes rather than rams. If its target
///    leaves the track (LRA / kill floor / return portal) it drops back to PATROL around wherever it is.
///  • RAGDOLL — once its health pool runs out it goes limp: AI off, gravity on, tumbling for
///    <see cref="ragdollDuration"/> before despawning. EVERY solid contact spends a point of that pool
///    — scenery, a car, another plane, a Support Ship laser round alike — so with the default
///    <see cref="maxHits"/> of 1 this is still "touch anything and you're down". Raise it on a prefab
///    VARIANT for a tougher plane that has to be worn through; a 10-hit plane that clips the track has
///    9 left. The wreck pays the Support Ship gunner if one ever hit it, else the player it had locked.
///
/// NOT A RACER: unlike DroneCar this never calls NotifyRacerFinished, so a plane can never cost the
/// player first place or score a round for the drones — including flying through the track's Return
/// Portal (which is a TRIGGER, so it doesn't even count as a collision here).
///
/// Multiplayer: host-simulated only (the spawner is gated) and streamed to clients as NpcKind.Drone
/// puppets; the kill reward is routed to the local inventory or to the owning client via NpcReplicator.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DronePlane : MonoBehaviour
{
    [Header("Vision")]
    [Tooltip("Layers the vision check can detect. Should include ONLY the Player layer and the " +
             "DronePlane layer — everything else (track, obstacles) is ignored so terrain never blocks it.")]
    public LayerMask visionMask;
    [Tooltip("How far this plane can spot a player (units).")]
    public float visionRange = 400f;
    [Tooltip("Half-angle of the vision cone (degrees). A player must be within this angle of " +
             "straight-ahead to be spotted.")]
    public float visionHalfAngle = 35f;

    [Header("Patrol")]
    [Tooltip("Radius of the horizontal circle this plane patrols around its spawn point (units).")]
    public float patrolRadius = 220f;
    [Tooltip("Cruising speed while patrolling (units/s) — a moderate pace, well under chase speed.")]
    public float patrolSpeed = 55f;
    [Tooltip("How far AHEAD of the plane's own bearing the patrol target sits, measured along the ring " +
             "(units). This is what closes the loop: the target is derived from where the plane ACTUALLY " +
             "is, not from a clock, so a plane that falls behind still has a reachable point just ahead " +
             "of it. Too small and it wobbles chasing its own nose; too large and it cuts across the " +
             "circle. Expressed as a distance rather than an angle so the feel is the same on a 150-unit " +
             "ring and a 320-unit one.")]
    public float patrolLeadDistance = 70f;
    [Tooltip("Top speed multiplier while the plane is still OFF its ring, easing back to 1x as it " +
             "arrives. Without headroom above Patrol Speed a plane can never close the gap at all: the " +
             "target sweeps the ring at exactly Patrol Speed, so an equal cap makes it a stern chase " +
             "it cannot win. 1 = no catch-up (the old behaviour).")]
    public float catchUpSpeedMultiplier = 2.2f;
    [Tooltip("Distance from the ring at which the full catch-up multiplier applies, blending down to " +
             "normal cruising speed as the plane closes. Roughly the biggest gap you want it to fix " +
             "quickly — planes spawn one full radius out, so this wants to be a good fraction of that.")]
    public float catchUpDistance = 150f;

    [Header("Chase")]
    [Tooltip("Top speed while chasing a player (units/s). Must be high enough to keep up with a " +
             "boosting car — the player tops out around 268 (600 mph).")]
    public float chaseMaxSpeed = 260f;
    [Tooltip("Acceleration toward the chase position (units/s²). Higher = snappier pursuit.")]
    public float chaseAcceleration = 90f;
    [Tooltip("How far from the player the plane settles while chasing (units). It stops closing " +
             "inside this range so it strafes the car instead of ramming it.")]
    public float standoffDistance = 90f;
    [Tooltip("How high above the target car the plane tries to sit while chasing (units). Keeps it " +
             "out of the car's path even on loops and jumps.")]
    public float chaseHeightOffset = 60f;
    [Tooltip("How fast the plane swings to face its heading (higher = tighter turns).")]
    public float turnRate = 3.5f;

    [Header("Shooting")]
    [Tooltip("Projectile prefab to fire. Same one the DroneCars use.")]
    public GameObject projectilePrefab;
    [Tooltip("Layer assigned to spawned projectiles. Blank = keep the prefab's layer.")]
    public string projectileLayerName = "Projectile";
    [Tooltip("Forward distance from the plane's centre where projectiles spawn and vision originates.")]
    public float muzzleForwardOffset = 4f;
    [Tooltip("Vertical offset of the muzzle (units).")]
    public float muzzleVerticalOffset = 0f;
    [Tooltip("Projectiles fired per second while in a firing window.")]
    public float fireRate = 3f;
    [Tooltip("Projectile speed in m/s. ~402 = 900 mph.")]
    public float projectileSpeed = 402f;
    [Tooltip("How long the plane fires continuously before cooling down (seconds).")]
    public float fireWindowDuration = 1.2f;
    [Tooltip("Cooldown after a firing window (seconds).")]
    public float fireCooldownDuration = 1f;

    [Header("Predictive Aim")]
    [Tooltip("Lead the target: aim where the car WILL be, from its current trajectory, instead of " +
             "where it is right now. Off = the old aim-at-current-position behaviour.")]
    public bool leadTarget = true;
    [Tooltip("How far ahead to predict, in seconds. 1 = shoot at where the car will be one second " +
             "from now at its current velocity.")]
    public float leadTime = 1f;
    [Tooltip("Instead of the fixed Lead Time, lead by the projectile's actual FLIGHT time to the " +
             "target (distance ÷ projectile speed, solved twice). Far more accurate at close range, " +
             "where a fixed 1 s wildly over-leads — a 402 m/s shot only takes ~0.12 s to cross 50 m.")]
    public bool useProjectileFlightTime;
    [Tooltip("Upper bound on the predicted lead (seconds), so a wild velocity reading can't throw the " +
             "aim point kilometres away.")]
    public float maxLeadTime = 2f;

    [Header("Crash")]
    [Tooltip("Seconds the wreck tumbles under gravity after colliding, before it despawns.")]
    public float ragdollDuration = 1f;
    [Tooltip("Credits paid to the player this plane was hunting when it crashes. No target = no payout.")]
    public int killReward = 50;

    [Header("Durability")]
    [Tooltip("How many hits this plane survives. 1 (the default) is the original glass-jaw behaviour: " +
             "ANY contact downs it instantly. Raise it on a prefab VARIANT to make a tougher plane — a " +
             "10-hit version has to be worn down, and a scrape against the track costs it one of those " +
             "10 just as a laser round does.")]
    public int maxHits = 1;
    [Tooltip("Minimum seconds between two ENVIRONMENTAL hits (track, scenery, another plane). One " +
             "physical impact often raises several contacts as the plane bounces and scrapes, and " +
             "without this a single brush with the track would burn a whole health pool in a few " +
             "frames. Laser rounds are deliberately NOT throttled by this — every round counts, the " +
             "same rule DroneCars follow — so a burst is never partly swallowed.")]
    public float collisionHitCooldown = 0.25f;


    [Header("Gizmos (Scene view only)")]
    [Tooltip("Draw the vision cone — green while searching, red once a player is locked.")]
    public bool showVisionGizmo = true;
    [Tooltip("Draw the patrol circle, its centre, and the point on it this plane is currently flying to.")]
    public bool showPatrolGizmo = true;
    [Tooltip("Draw chase aids while hunting: the line to the target and the standoff hold sphere.")]
    public bool showChaseGizmo = true;

    // ---- State ----
    private enum State { Patrol, Chase, Ragdoll }
    private State state = State.Patrol;

    private Rigidbody rb;
    private Vector3 patrolCenter;
    private float patrolAngle;          // current position around the patrol circle (radians)
    private Transform target;           // the player car being hunted (null while patrolling)

    // Burst-fire state machine (mirrors DroneCar's, so the two read the same in play).
    private enum FireState { Firing, Cooldown }
    private FireState fireState = FireState.Firing;
    private float fireStateTimer;
    private float lastFireTime = -999f;
    private bool hadSightLastFrame;

    // Numeric velocity fallback for predictive aim (see SampleTargetVelocity).
    private Vector3 lastTargetPos;
    private Vector3 sampledVelocity;
    private bool hasVelocitySample;

    private bool warnedMissingProjectileLayer;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;      // flies under AI control until it crashes
        patrolCenter = transform.position;
        // Start at this plane's current bearing around the circle so a batch of planes spawned at the
        // same point don't all fly the identical arc in lockstep.
        patrolAngle = Random.Range(0f, Mathf.PI * 2f);
    }

    /// <summary>Called by the spawner to set the patch of sky this plane owns. Defaults to its spawn
    /// point if never called.</summary>
    public void Initialize(Vector3 center, float radius, float speed)
    {
        patrolCenter = center;
        patrolRadius = radius;
        patrolSpeed = speed;
    }

    void FixedUpdate()
    {
        // Round preload (multiplayer): the track and its NPCs exist but are FROZEN until the hub portal
        // spawns — no flying, no patrol progress, no burst-fire timers.
        if (MultiplayerWorld.TrackFrozen) return;

        if (state == State.Ragdoll) return;   // physics owns the wreck now

        // Drop the target the moment it stops being valid — its player left the track (LRA, kill floor,
        // return portal) or disconnected. anyArea:false is exactly the "still racing?" test.
        if (state == State.Chase)
        {
            target = MultiplayerWorld.ValidateStickyTarget(target, anyArea: false);
            if (target == null) ResumePatrol();
        }

        if (state == State.Patrol) AcquireTarget();

        if (state == State.Chase)
        {
            SampleTargetVelocity();   // keep the predictive-aim fallback fresh
            ChaseTarget();
        }
        else Patrol();

        TryShoot();
    }

    // -------------------------------------------------------
    //  Patrol
    // -------------------------------------------------------

    /// <summary>Flies a steady horizontal circle around the patrol centre.
    ///
    /// The target point is CLOSED-LOOP: it is the plane's own bearing around the ring plus
    /// <see cref="patrolLeadDistance"/>, so it always sits just ahead of wherever the plane actually is.
    /// The original version advanced an angle on its own clock, independent of the plane — and since
    /// that target swept the ring at exactly <see cref="patrolSpeed"/> while the plane was CAPPED at the
    /// same speed, a plane that started off-ring (every plane does: the spawner centres the circle on
    /// the spawn point, so they all begin one full radius out) could never catch up. They trailed their
    /// own circles forever, with the gizmo drawing a long straight line to a target they were chasing
    /// and never reaching.
    ///
    /// Two more things fall out of that fix:
    ///  • <see cref="catchUpSpeedMultiplier"/> gives the plane headroom above its cruising speed while
    ///    it is still off-ring, easing back to 1x as it arrives — otherwise even a reachable target is
    ///    approached at exactly the speed it retreats.
    ///  • The NOSE now follows the plane's actual velocity rather than the ring's tangent. The tangent
    ///    is where it would be pointed if it were ON the circle; while it is closing on one, that can be
    ///    90° from where it is really going, which is what made patrolling planes fly sideways.</summary>
    void Patrol()
    {
        float radius = Mathf.Max(patrolRadius, 1f);

        Vector3 offset = transform.position - patrolCenter;
        Vector2 flat = new Vector2(offset.x, offset.z);

        if (flat.sqrMagnitude > 0.01f)
        {
            // The ring is parameterised (cos θ, 0, sin θ), so the plane's own bearing is atan2(z, x).
            float bearing = Mathf.Atan2(flat.y, flat.x);
            float lead = Mathf.Clamp(patrolLeadDistance / radius, 0.02f, Mathf.PI * 0.5f);
            patrolAngle = bearing + lead;
        }
        else
        {
            // Dead centre: the bearing is undefined there, so fall back to advancing the old way until
            // the plane drifts far enough out for atan2 to mean something.
            patrolAngle += (patrolSpeed / radius) * Time.fixedDeltaTime;
        }

        Vector3 targetPos = patrolCenter + new Vector3(Mathf.Cos(patrolAngle), 0f, Mathf.Sin(patrolAngle)) * radius;
        Vector3 tangent = new Vector3(-Mathf.Sin(patrolAngle), 0f, Mathf.Cos(patrolAngle));

        // Distance to the nearest point ON the ring — radial error and altitude error combined.
        float radial = flat.magnitude - radius;
        float ringError = Mathf.Sqrt(radial * radial + offset.y * offset.y);

        float blend = catchUpDistance > 0.01f ? Mathf.Clamp01(ringError / catchUpDistance) : 0f;
        float maxSpeed = patrolSpeed * Mathf.Lerp(1f, Mathf.Max(1f, catchUpSpeedMultiplier), blend);

        // Face where it is actually flying; the tangent is only a fallback for a plane barely moving
        // (spawn frame, or the instant it resumes patrol), where velocity has no direction worth using.
        Vector3 nose = rb.linearVelocity.sqrMagnitude > 1f ? rb.linearVelocity : tangent;

        SteerToward(targetPos, maxSpeed, nose);
    }

    /// <summary>Returns to patrolling around wherever the plane currently is (used when a target is lost),
    /// so it doesn't fly all the way back to its original patch.</summary>
    void ResumePatrol()
    {
        state = State.Patrol;
        target = null;
        patrolCenter = transform.position;
        hadSightLastFrame = false;
        hasVelocitySample = false;   // don't lead a new lock off the old car's motion
    }

    // -------------------------------------------------------
    //  Target acquisition + chase
    // -------------------------------------------------------

    /// <summary>Looks for a player car in the vision cone; locks onto the nearest one found.</summary>
    void AcquireTarget()
    {
        Transform found = FindPlayerInCone();
        if (found == null) return;
        target = found;
        state = State.Chase;
        hasVelocitySample = false;   // fresh lock — start its velocity sampling clean
    }

    /// <summary>Pursues the locked player, settling into a standoff position above and behind them
    /// rather than flying into them. Faces the player throughout so the guns (and the vision cone that
    /// gates them) stay on target.</summary>
    void ChaseTarget()
    {
        Vector3 targetPos = target.position + Vector3.up * chaseHeightOffset;
        Vector3 toTarget = targetPos - transform.position;
        float distance = toTarget.magnitude;

        // Hold station at the standoff range: aim for the point that distance short of the target, so
        // the plane closes when far and eases off (even backs away) when it gets too close.
        Vector3 desiredPos = distance > 0.001f
            ? targetPos - toTarget.normalized * standoffDistance
            : transform.position;

        // Always LOOK at the actual car — the cone and muzzle must track the player, not the hold point.
        Vector3 aim = target.position - transform.position;
        SteerToward(desiredPos, chaseMaxSpeed, aim.sqrMagnitude > 0.001f ? aim.normalized : transform.forward);
    }

    /// <summary>Shared flight model: accelerate toward a point, cap the speed, and swing the nose onto
    /// the given heading. Full 3D — the plane climbs and dives freely, which is what lets it follow a
    /// car through loops and off jumps.</summary>
    void SteerToward(Vector3 destination, float maxSpeed, Vector3 heading)
    {
        Vector3 toDest = destination - transform.position;
        if (toDest.sqrMagnitude > 0.0001f)
            rb.AddForce(toDest.normalized * chaseAcceleration, ForceMode.Acceleration);

        if (rb.linearVelocity.magnitude > maxSpeed)
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;

        if (heading.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(heading.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, turnRate * Time.fixedDeltaTime));
        }
    }

    /// <summary>Nearest player car inside the vision cone, or null. Another plane sitting closer in the
    /// cone blocks the shot (same courtesy DroneCars give each other), so planes don't shoot each other
    /// in the back.</summary>
    Transform FindPlayerInCone()
    {
        Vector3 origin = GetMuzzlePosition();
        Vector3 forward = transform.forward;

        Collider[] candidates = Physics.OverlapSphere(origin, visionRange, visionMask);

        Transform closestPlayer = null; float closestPlayerDist = float.MaxValue;
        float closestPlaneDist = float.MaxValue;

        foreach (var col in candidates)
        {
            GameObject obj = col.gameObject;
            if (obj.GetComponentInParent<DronePlane>() == this) continue;

            Vector3 toObj = col.bounds.center - origin;
            float dist = toObj.magnitude;
            if (dist < 0.001f) continue;
            if (Vector3.Angle(forward, toObj.normalized) > visionHalfAngle) continue;

            if (obj.GetComponentInParent<DronePlane>() != null)
            {
                if (dist < closestPlaneDist) closestPlaneDist = dist;
            }
            else
            {
                Transform car = ResolvePlayerRoot(obj.transform);
                if (car != null && dist < closestPlayerDist) { closestPlayerDist = dist; closestPlayer = car; }
            }
        }

        if (closestPlayer == null) return null;
        if (closestPlaneDist < closestPlayerDist) return null;   // another plane is in the way
        return closestPlayer;
    }

    /// <summary>Walks up from a collider to the tagged car root — the local player, or a remote player's
    /// solid puppet (on the host, where planes simulate, remote cars are legitimate prey too).</summary>
    static Transform ResolvePlayerRoot(Transform t)
    {
        while (t != null)
        {
            if (t.CompareTag("Player") || t.CompareTag("RemotePlayer")) return t;
            t = t.parent;
        }
        return null;
    }

    // -------------------------------------------------------
    //  Shooting (burst cycle mirrors DroneCar)
    // -------------------------------------------------------

    Vector3 GetMuzzlePosition() =>
        transform.position + transform.forward * muzzleForwardOffset + transform.up * muzzleVerticalOffset;

    void TryShoot()
    {
        if (projectilePrefab == null || state != State.Chase || target == null) return;

        // Only fire while the target is genuinely in the cone — chasing alone isn't enough.
        Vector3 origin = GetMuzzlePosition();
        Vector3 toTarget = target.position - origin;
        bool hasSight = toTarget.sqrMagnitude > 0.001f
                     && toTarget.magnitude <= visionRange
                     && Vector3.Angle(transform.forward, toTarget.normalized) <= visionHalfAngle;

        // Re-acquiring sight restarts the cycle mid-firing so the player gets shot at promptly.
        if (hasSight && !hadSightLastFrame)
        {
            fireState = FireState.Firing;
            fireStateTimer = 0f;
        }
        hadSightLastFrame = hasSight;
        if (!hasSight) return;

        fireStateTimer += Time.fixedDeltaTime;

        if (fireState == FireState.Firing)
        {
            if (Time.time - lastFireTime >= 1f / fireRate)
            {
                // Lead the shot: aim where the car is HEADED, not where it currently sits.
                FireAt(PredictAimPoint(origin), origin);
                lastFireTime = Time.time;
            }
            if (fireStateTimer >= fireWindowDuration)
            {
                fireState = FireState.Cooldown;
                fireStateTimer = 0f;
            }
        }
        else if (fireStateTimer >= fireCooldownDuration)
        {
            fireState = FireState.Firing;
            fireStateTimer = 0f;
        }
    }

    // -------------------------------------------------------
    //  Predictive aim (target leading)
    // -------------------------------------------------------

    /// <summary>Where to actually shoot: the target's position projected forward along its current
    /// trajectory, so the shot arrives where the car is GOING rather than where it was. Returns the
    /// plain current position when leading is off or the car isn't really moving.</summary>
    Vector3 PredictAimPoint(Vector3 origin)
    {
        Vector3 pos = TargetAimCenter();
        if (!leadTarget || target == null) return pos;

        Vector3 vel = ResolveTargetVelocity();
        if (vel.sqrMagnitude < 0.01f) return pos;   // parked — nothing to lead

        float lead;
        if (useProjectileFlightTime && projectileSpeed > 0.01f)
        {
            // Solve the intercept twice: the first pass gives a flight time from the CURRENT distance,
            // the second re-times against that predicted point. Two passes converges well enough here
            // and costs nothing, whereas a single pass under-leads badly on fast crossing targets.
            float t = Vector3.Distance(origin, pos) / projectileSpeed;
            t = Vector3.Distance(origin, pos + vel * t) / projectileSpeed;
            lead = t;
        }
        else
        {
            lead = leadTime;
        }

        return pos + vel * Mathf.Clamp(lead, 0f, Mathf.Max(maxLeadTime, 0f));
    }

    /// <summary>The point on the target we aim at — its collider centre where available (the car's
    /// mass, not its pivot, which can sit at the wheels).</summary>
    Vector3 TargetAimCenter()
    {
        if (target == null) return transform.position;
        var col = target.GetComponentInChildren<Collider>();
        return col != null ? col.bounds.center : target.position;
    }

    /// <summary>The hunted car's world velocity. Remote players are KINEMATIC puppets whose rigidbody
    /// velocity is meaningless, so their replicated velocity comes off RemoteCarPuppet; the local car
    /// reports its own rigidbody. The sampled fallback (position delta over time) covers anything else,
    /// so leading still works even on a car rig that exposes neither.</summary>
    Vector3 ResolveTargetVelocity()
    {
        var puppet = target.GetComponentInParent<RemoteCarPuppet>();
        if (puppet != null) return puppet.CurrentVelocity;

        var trb = target.GetComponentInParent<Rigidbody>();
        if (trb == null) trb = target.GetComponentInChildren<Rigidbody>();
        if (trb != null && !trb.isKinematic) return trb.linearVelocity;

        return sampledVelocity;
    }

    /// <summary>Numeric velocity fallback: differentiate the target's position between physics steps.
    /// Reset whenever the target changes so a fresh lock never leads off the previous car's motion.</summary>
    void SampleTargetVelocity()
    {
        if (target == null) { hasVelocitySample = false; return; }

        Vector3 pos = target.position;
        if (hasVelocitySample && Time.fixedDeltaTime > 0f)
            sampledVelocity = (pos - lastTargetPos) / Time.fixedDeltaTime;

        lastTargetPos = pos;
        hasVelocitySample = true;
    }

    void FireAt(Vector3 targetPoint, Vector3 origin)
    {
        Vector3 direction = (targetPoint - origin).normalized;
        GameObject proj = Instantiate(projectilePrefab, origin, Quaternion.LookRotation(direction, Vector3.up));
        ApplyProjectileLayer(proj);

        var projectile = proj.GetComponent<DroneProjectile>();
        AudioManager.PlayDroneShoot(origin, projectile != null ? projectile.audio3D : null);
        if (projectile != null) projectile.Launch(direction, projectileSpeed);

        // Multiplayer host: stream the projectile to clients (visual puppets; hits stay host-authoritative).
        NpcReplicator.Track(proj, NpcKind.Projectile, projectilePrefab);

        // CRITICAL: never let our own bullet clip us — any collision ragdolls this plane instantly.
        var projCol = proj.GetComponent<Collider>();
        if (projCol != null)
            foreach (var myCol in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(projCol, myCol);
    }

    void ApplyProjectileLayer(GameObject proj)
    {
        if (proj == null || string.IsNullOrEmpty(projectileLayerName)) return;
        int layer = LayerMask.NameToLayer(projectileLayerName);
        if (layer < 0)
        {
            if (!warnedMissingProjectileLayer)
            {
                warnedMissingProjectileLayer = true;
                Debug.LogWarning($"[DronePlane] Layer '{projectileLayerName}' not found in Tags and " +
                                 "Layers — projectiles left on the prefab's layer.");
            }
            return;
        }
        SetLayerRecursively(proj, layer);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // -------------------------------------------------------
    //  Crash → ragdoll → despawn (+ bounty)
    // -------------------------------------------------------

    /// <summary>ANY solid contact damages the plane — scenery, a car, a laser round, another
    /// DronePlane. With <see cref="maxHits"/> at 1 that is still "one touch and you're down"; raise it
    /// and the plane has to be worn through. Trigger volumes (the track's Return Portal among them)
    /// don't raise collisions, so flying through the portal costs it nothing.
    ///
    /// ⚠️ The laser check has to be HERE, and the laser must NOT also apply the damage itself. Both
    /// objects receive OnCollisionEnter for the same contact, so counting it on both sides would make
    /// every round cost TWO hits — invisible while planes died in one, and quietly halving the health
    /// pool the moment one didn't. SupportShipLaser therefore only reports the hit for its audio and
    /// leaves the arithmetic to us.</summary>
    void OnCollisionEnter(Collision collision)
    {
        var laser = collision.collider.GetComponentInParent<SupportShipLaser>();
        if (laser != null)
        {
            TakeHit(true, laser.pilotClientId, laser.pilotIsLocal);
            return;
        }

        // Environmental contacts only: one impact usually raises several of these as the plane bounces
        // and scrapes along, and each would otherwise be a separate point of damage.
        if (Time.time - lastCollisionHitTime < collisionHitCooldown) return;
        lastCollisionHitTime = Time.time;
        TakeHit(false, 0, false);
    }

    /// <summary>Spends one point of the health pool and downs the plane when it runs out.
    ///
    /// Credit follows the DroneCar rule the user set for laser damage: the last player-attributable hit
    /// wins. A gunner who wore the plane down doesn't lose the kill because it clipped a wall on the
    /// way out — but if no gunner ever touched it, it falls back to the original behaviour and pays
    /// whoever it was hunting.</summary>
    void TakeHit(bool fromPilot, ulong clientId, bool isLocal)
    {
        if (state == State.Ragdoll) return;

        if (fromPilot)
        {
            damagedByPilot = true;
            lastPilotId = clientId;
            lastPilotIsLocal = isLocal;
        }

        if (++hitsTaken < Mathf.Max(1, maxHits))
        {
            // Survived: flash and deepen the tint so the gunner can see the pool draining. Sent to the
            // clients too — the Support Ship pilot is very often one of them, and they are the whole
            // audience for this feedback.
            ShowDamage();
            NpcReplicator.SendNpcDamage(gameObject, hitsTaken, Mathf.Max(1, maxHits));
            return;
        }

        if (damagedByPilot) DownedByPilot(lastPilotId, lastPilotIsLocal);
        else Crash();
    }

    /// <summary>Flashes and re-tints this plane's own copy. Cached because a 10-hit plane calls it nine
    /// times and GetComponentInChildren is not free.
    ///
    /// The component is ADDED if the prefab doesn't carry one, so damage feedback needs no editor
    /// wiring to work at all — put a DroneDamageTint on the prefab only when you want to TUNE it, and
    /// those values then win.</summary>
    void ShowDamage()
    {
        if (damageTint == null)
        {
            damageTint = GetComponentInChildren<DroneDamageTint>(true);
            if (damageTint == null) damageTint = gameObject.AddComponent<DroneDamageTint>();
        }
        damageTint.RegisterHit(hitsTaken, Mathf.Max(1, maxHits));
    }

    // Set when a Support Ship gunner shoots this plane down, so the wreck bounty goes to THEM rather
    // than to whoever the plane happened to be hunting.
    private bool downedByPilot;
    private int hitsTaken;                  // health pool spent so far (see maxHits)
    private float lastCollisionHitTime = -999f;
    private bool damagedByPilot;            // a gunner has landed at least one round on this plane
    private ulong lastPilotId;
    private bool lastPilotIsLocal;
    private DroneDamageTint damageTint;
    private ulong pilotClientId;
    private bool pilotIsLocal;

    /// <summary>Shot down by a Support Ship laser. Same ragdoll as any other crash — but the kill
    /// reward is redirected to the GUNNER, since they earned it, instead of to the plane's target who
    /// had nothing to do with it.</summary>
    public void DownedByPilot(ulong clientId, bool isLocal)
    {
        if (state == State.Ragdoll) return;
        downedByPilot = true;
        pilotClientId = clientId;
        pilotIsLocal = isLocal;
        Crash();
    }

    void Crash()
    {
        if (state == State.Ragdoll) return;

        // Pay whoever earned it: the Support Ship gunner who shot it down, else the player it was
        // hunting. Nothing if it was just patrolling and fell over on its own.
        if (downedByPilot) AwardPilotKill();
        else if (target != null) AwardKillReward(target);

        state = State.Ragdoll;
        target = null;
        rb.useGravity = true;               // go limp and tumble
        rb.constraints = RigidbodyConstraints.None;
        Destroy(gameObject, ragdollDuration);
    }

    /// <summary>Routes the wreck bounty to the Support Ship gunner who shot this plane down — their
    /// own inventory if they're on this machine, otherwise across the wire.</summary>
    void AwardPilotKill()
    {
        if (killReward <= 0) return;

        if (!pilotIsLocal)
        {
            NpcReplicator.SendBounty(pilotClientId, killReward);
            Debug.Log($"[DronePlane] Shot down by client {pilotClientId}'s Support Ship — " +
                      $"bounty {killReward} sent.");
            return;
        }

        if (PlayerInventory.Instance == null) return;
        PlayerInventory.Instance.AddCredits(killReward);
        AudioManager.PlayKnockoffBounty();
        Debug.Log($"[DronePlane] Shot down by the local Support Ship — awarded {killReward} credits.");
    }

    /// <summary>Routes the wreck bounty to whoever this plane was hunting: straight into the local
    /// inventory for the local player, or across the wire for a remote player's machine.</summary>
    void AwardKillReward(Transform hunted)
    {
        if (killReward <= 0) return;

        if (MultiplayerWorld.IsMultiplayerGame
            && MultiplayerWorld.TryGetCarOwner(hunted, out ulong clientId, out bool isLocalPlayer)
            && !isLocalPlayer)
        {
            NpcReplicator.SendBounty(clientId, killReward);
            Debug.Log($"[DronePlane] Crashed while hunting client {clientId} — bounty {killReward} sent.");
            return;
        }

        if (PlayerInventory.Instance == null) return;
        PlayerInventory.Instance.AddCredits(killReward);
        AudioManager.PlayKnockoffBounty();
        Debug.Log($"[DronePlane] Crashed while hunting the player — awarded {killReward} credits.");
    }

    // -------------------------------------------------------
    //  Scene-view gizmos
    // -------------------------------------------------------

    /// <summary>Draws the vision cone (same ring style as DroneCar, so the two read alike in the Scene
    /// view) plus the patrol circle and chase aids. Everything works BEFORE pressing play too: outside
    /// play mode the patrol centre falls back to this transform, since <see cref="patrolCenter"/> is
    /// only assigned in Awake — otherwise the circle would draw at the world origin while authoring.</summary>
    void OnDrawGizmos()
    {
        bool hunting = Application.isPlaying && state == State.Chase && target != null;

        if (showVisionGizmo) DrawVisionCone(hunting ? Color.red : Color.green);
        if (showPatrolGizmo) DrawPatrolPath();
        if (showChaseGizmo && hunting) DrawChaseAids();
    }

    /// <summary>The vision cone as a series of rings expanding with distance, with edge lines on the
    /// outermost ring and a centre line down the axis.</summary>
    void DrawVisionCone(Color color)
    {
        Gizmos.color = color;

        Vector3 origin = GetMuzzlePosition();
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        Vector3 up = transform.up;

        const int rings = 5;
        const int segmentsPerRing = 16;

        for (int r = 1; r <= rings; r++)
        {
            float dist = (visionRange / rings) * r;
            float radius = dist * Mathf.Tan(visionHalfAngle * Mathf.Deg2Rad);
            Vector3 ringCenter = origin + forward * dist;

            Vector3 prevPoint = ringCenter + right * radius;
            for (int s = 1; s <= segmentsPerRing; s++)
            {
                float t = (s / (float)segmentsPerRing) * Mathf.PI * 2f;
                Vector3 point = ringCenter + (right * Mathf.Cos(t) + up * Mathf.Sin(t)) * radius;
                Gizmos.DrawLine(prevPoint, point);
                prevPoint = point;
            }

            if (r == rings)
            {
                Gizmos.DrawLine(origin, ringCenter + right * radius);
                Gizmos.DrawLine(origin, ringCenter - right * radius);
                Gizmos.DrawLine(origin, ringCenter + up * radius);
                Gizmos.DrawLine(origin, ringCenter - up * radius);
            }
        }

        Gizmos.DrawLine(origin, origin + forward * visionRange);
    }

    /// <summary>The horizontal circle this plane patrols: the ring itself, a marker at its centre, and
    /// (in play mode) a line out to the point on the ring it's currently flying toward.</summary>
    void DrawPatrolPath()
    {
        // Before play, Awake hasn't run — anchor the preview to where the plane actually sits.
        Vector3 center = Application.isPlaying ? patrolCenter : transform.position;
        float radius = Mathf.Max(patrolRadius, 1f);

        // Dimmed while the plane is off hunting, since the circle isn't what it's flying right now.
        bool patrolling = !Application.isPlaying || state == State.Patrol;
        Gizmos.color = patrolling ? new Color(0.2f, 0.9f, 1f, 0.9f) : new Color(0.2f, 0.9f, 1f, 0.25f);

        const int segments = 48;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float t = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 point = center + new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t)) * radius;
            Gizmos.DrawLine(prev, point);
            prev = point;
        }

        // Centre marker + a short vertical tick, so the patrol altitude is readable against the track.
        Gizmos.DrawLine(center + Vector3.up * 8f, center - Vector3.up * 8f);
        Gizmos.DrawWireSphere(center, 4f);

        // Where on the ring it's heading right now.
        if (Application.isPlaying && state == State.Patrol)
        {
            Vector3 targetPos = center + new Vector3(Mathf.Cos(patrolAngle), 0f, Mathf.Sin(patrolAngle)) * radius;
            Gizmos.DrawLine(transform.position, targetPos);
            Gizmos.DrawWireSphere(targetPos, 6f);
        }
    }

    /// <summary>Chase aids: the line to the hunted car, the standoff sphere the plane holds around it
    /// (it should never fly inside this — that's what keeps it strafing instead of ramming), and the
    /// PREDICTED aim point it's actually shooting at.</summary>
    void DrawChaseAids()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawLine(transform.position, target.position);

        Gizmos.color = new Color(1f, 0.55f, 0f, 0.9f);   // orange = the hold radius
        Vector3 holdCenter = target.position + Vector3.up * chaseHeightOffset;
        Gizmos.DrawWireSphere(holdCenter, standoffDistance);
        Gizmos.DrawLine(target.position, holdCenter);

        // Predictive aim: yellow marks where the guns are actually pointed, with a line from the car
        // showing how far ahead it's leading. Tune leadTime by watching this sit on the car's path.
        if (leadTarget)
        {
            Vector3 muzzle = GetMuzzlePosition();
            Vector3 aim = PredictAimPoint(muzzle);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(aim, 8f);
            Gizmos.DrawLine(muzzle, aim);
            Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
            Gizmos.DrawLine(TargetAimCenter(), aim);   // the lead offset itself
        }
    }
}
