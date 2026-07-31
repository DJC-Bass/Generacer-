using UnityEngine;

/// <summary>
/// A patrolling DRONE PLANE: an airborne hunter that circles its own patch of sky, and dives on any
/// player car it spots — chasing and strafing them with the same vision-cone + projectile fire the
/// DroneCar uses, but flying in full 3D so it can follow a car up hills, through loops and off jumps.
///
/// Three states:
///  • PATROL — flies a horizontal circle around its spawn point at <see cref="patrolSpeed"/>, holding
///    its spawn altitude (the spawner varies that per plane, so some are skyline and some buzz low).
///  • CHASE — on spotting a player, locks on and pursues, HOLDING <see cref="standoffDistance"/> and
///    hovering <see cref="chaseHeightOffset"/> above them so it strafes rather than rams. If its target
///    leaves the track (LRA / kill floor / return portal) it drops back to PATROL around wherever it is.
///  • RAGDOLL — the instant it collides with ANYTHING (scenery, a car, another plane) it goes limp:
///    AI off, gravity on, tumbling for <see cref="ragdollDuration"/> before despawning. If it had a
///    player locked at that moment, THAT player is paid <see cref="killReward"/> credits for the wreck.
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

    /// <summary>Flies a steady horizontal circle around the patrol centre at the plane's own altitude.</summary>
    void Patrol()
    {
        // Advance around the circle at the cruising speed (v = ωr ⇒ ω = v / r).
        float radius = Mathf.Max(patrolRadius, 1f);
        patrolAngle += (patrolSpeed / radius) * Time.fixedDeltaTime;

        Vector3 targetPos = patrolCenter + new Vector3(Mathf.Cos(patrolAngle), 0f, Mathf.Sin(patrolAngle)) * radius;
        // Tangent of the circle = the direction of travel (derivative of the position above).
        Vector3 heading = new Vector3(-Mathf.Sin(patrolAngle), 0f, Mathf.Cos(patrolAngle));

        SteerToward(targetPos, patrolSpeed, heading);
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

    /// <summary>ANY solid contact downs the plane — scenery, a car, or another DronePlane. Trigger
    /// volumes (the track's Return Portal among them) don't raise collisions, so flying through the
    /// portal neither wrecks the plane nor counts for anything.</summary>
    void OnCollisionEnter(Collision collision) => Crash();

    void Crash()
    {
        if (state == State.Ragdoll) return;

        // Pay the player it was hunting for the wreck — nothing if it was just patrolling.
        if (target != null) AwardKillReward(target);

        state = State.Ragdoll;
        target = null;
        rb.useGravity = true;               // go limp and tumble
        rb.constraints = RigidbodyConstraints.None;
        Destroy(gameObject, ragdollDuration);
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
