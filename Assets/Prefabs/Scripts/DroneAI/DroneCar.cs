using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DroneCar : MonoBehaviour
{
    [Tooltip("Layers the vision spherecast can detect. Should include ONLY the " +
         "Player layer and the Drone layer — everything else (track, obstacles) " +
         "is ignored so the cast isn't blocked by terrain.")]
    public LayerMask visionMask;

    [Header("Path Following")]
    public float pathDistance = 0f;
    public float pathSpeed = 50f;

    [Header("Correction")]
    [Tooltip("Normal correction strength — high so the drone stays locked to its path.")]
    public float correctionStrength = 10f;
    public float rotationCorrection = 8f;
    public float maxOffPathDistance = 500f;

    [Header("Collision Response")]
    [Tooltip("Correction strength while the player is colliding with this drone. " +
             "Lower than normal so the player can knock the drone around.")]
    public float collisionCorrectionStrength = 1.5f;
    [Tooltip("Rotation correction while colliding — lower so the drone can be " +
             "spun/tilted by the impact rather than rigidly holding upright.")]
    public float collisionRotationCorrection = 1f;
    [Tooltip("Tag of the player car.")]
    public string playerTag = "Player";

    [Header("Vertical Offset")]
    [Tooltip("Distance above the sampled path point to hover at. Compensates for " +
             "the road centerline being on the underside of the mesh if road thickness " +
             "is negative.")]
    public float verticalSpawnOffset = 5f;

    [Header("Shooting")]
    [Tooltip("Projectile prefab to fire.")]
    public GameObject projectilePrefab;
    [Tooltip("Layer assigned to spawned projectiles (e.g. 'Projectile', layer 14). Lets the " +
             "collision matrix treat them as their own layer. Blank = keep the prefab's layer.")]
    public string projectileLayerName = "Projectile";
    private bool warnedMissingProjectileLayer;
    [Tooltip("Forward distance from the drone center where projectiles spawn and " +
             "the vision cast originates (units). Positions the muzzle at the front.")]
    public float muzzleForwardOffset = 3f;
    [Tooltip("Vertical offset for the muzzle position (units). Raise to match the " +
             "height of the drone's front.")]
    public float muzzleVerticalOffset = 0.5f;
    public float visionRange = 152f;
    [Tooltip("Half-angle of the vision cone (degrees). The player must be within " +
             "this angle of straight-ahead to be detected.")]
    public float visionHalfAngle = 20f;
    [Tooltip("Projectiles fired per second.")]
    public float fireRate = 3f;
    [Tooltip("Projectile speed in m/s. ~402 = 900mph.")]
    public float projectileSpeed = 402f;
    [Tooltip("Tag of the player.")]
    public string playerTagForShooting = "Player";

    [Header("Burst Fire Timing")]
    [Tooltip("How long the drone fires projectiles continuously (seconds).")]
    public float fireWindowDuration = 1f;
    [Tooltip("Cooldown after a firing window before the drone can fire again (seconds).")]
    public float fireCooldownDuration = 1f;

    [Tooltip("Downward acceleration applied after the player knocks this drone, " +
             "mimicking gravity so it falls off the track toward the kill floor. " +
             "Sink speed ≈ this / collisionCorrectionStrength.")]
    public float knockDownforce = 20f;

    [Header("Reward")]
    [Tooltip("Credits awarded to the player if they knock this car off the track and " +
             "into the kill floor. DroneCar = 100, ChallengerCar = 200.")]
    public int creditReward = 100;

    [Header("Chase Mode (hub Drone ending)")]
    [Tooltip("When true, the drone ignores its path and chases the player along the ground, firing " +
             "as it closes — used by the game-over Drone ending swarm in the hub. Enable via BeginChase().")]
    public bool chaseMode;
    [Tooltip("Horizontal acceleration toward the player while chasing (units/s²).")]
    public float chaseAcceleration = 35f;
    [Tooltip("Maximum horizontal chase speed (units/s).")]
    public float chaseMaxSpeed = 70f;
    [Tooltip("Downward acceleration applied while chasing, imitating gravity so the drone settles " +
             "onto and drives along the ground instead of flying up at the player (units/s²).")]
    public float chaseDownforce = 30f;
    private Transform chaseTarget;

    private TrackPath path;
    private Rigidbody rb;
    private float lastFireTime = -999f;
    private bool finished;
    // Burst-fire state machine
    private enum FireState { Firing, Cooldown }
    private FireState fireState = FireState.Firing;
    private float fireStateTimer = 0f;
    private bool hadSightLastFrame = false;

    // Tracks the last time the player touched this drone. Correction stays
    // reduced until recoveryDelay seconds have passed since this timestamp.
    private float lastPlayerContactTime = -999f;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
    }

    public void Initialize(TrackPath assignedPath, float startDistance, float speed)
    {
        path = assignedPath;
        pathDistance = startDistance;
        pathSpeed = speed;

        if (path != null && path.IsReady)
        {
            path.Sample(pathDistance, out Vector3 pos, out Vector3 tan);
            transform.position = pos + Vector3.up * verticalSpawnOffset;
            transform.rotation = Quaternion.LookRotation(tan, Vector3.up);
        }
    }

    void FixedUpdate()
    {
        // Round preload (multiplayer): the track — and its pre-spawned drones — exist but are FROZEN
        // until the hub portal spawns. No movement, no path progress, no burst-fire timers.
        if (MultiplayerWorld.TrackFrozen) return;

        if (finished) return;

        // Hub Drone-ending swarm: ignore the path and home in on the player.
        if (chaseMode) { ChasePlayer(); return; }

        if (path == null || !path.IsReady) return;

        pathDistance += pathSpeed * Time.fixedDeltaTime;

        path.Sample(pathDistance, out Vector3 targetPos, out Vector3 tangent);
        targetPos += Vector3.up * verticalSpawnOffset;

        if (pathDistance >= path.TotalLength)
        {
            finished = true;
            // This AI car crossed the finish before the player — they forfeit first place.
            if (GameLoopManager.Instance != null)
                GameLoopManager.Instance.NotifyRacerFinished();
            Destroy(gameObject, 1f);
            return;
        }

        Vector3 worldPos = transform.position;
        float deviation = Vector3.Distance(worldPos, targetPos);
        // Once hit, DON'T snap back to the path — let it drift and fall away.
        if (!playerHit && deviation > maxOffPathDistance)
        {
            transform.position = targetPos;
            rb.linearVelocity = tangent * pathSpeed;
            return;
        }

        TryShoot();

        // After a player hit, use the soft correction permanently (no recovery)
        // so the drone stays knock-able and downforce can carry it off-track.
        float activeCorrection = playerHit ? collisionCorrectionStrength : correctionStrength;
        float activeRotation = playerHit ? collisionRotationCorrection : rotationCorrection;

        Vector3 toTarget = targetPos - worldPos;
        rb.AddForce(toTarget * activeCorrection, ForceMode.Acceleration);

        Vector3 desiredVel = tangent * pathSpeed;
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, desiredVel,
                                          activeCorrection * Time.fixedDeltaTime);

        Quaternion targetRot = Quaternion.LookRotation(tangent, Vector3.up);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot,
                                          activeRotation * Time.fixedDeltaTime));

        // Steady downforce after a hit — mimics gravity so the drone sinks off the
        // track and falls to the kill floor. Applied after the path Lerp so it wins
        // on the vertical axis.
        if (playerHit)
            rb.AddForce(Vector3.down * knockDownforce, ForceMode.Acceleration);
    }

    /// <summary>Switches this drone into hub Drone-ending chase mode: it chases a player along the
    /// ground and fires, with no path required. Called by the spawner instead of Initialize().
    /// Multiplayer (host sim): picks ONE RANDOM player and STICKS with them for this drone's whole
    /// life — the swarm deliberately splits its attention across both teams (the per-entity sticky
    /// random targeting from the design spec). Single-player: the local car, as ever.</summary>
    public void BeginChase()
    {
        chaseMode = true;
        chaseTarget = MultiplayerWorld.PickStickyTarget(anyArea: true);
    }

    /// <summary>Drives along the ground toward the player (horizontal seek + downforce) and fires.
    /// Chasing on the ground plane — not flying up to the player's height — sells the "driving"
    /// look; the downforce keeps it pressed onto the floor. The relentless hub-swarm behaviour.</summary>
    void ChasePlayer()
    {
        // Sticky targeting: keep the picked player until they cease to exist (disconnect destroys
        // their puppet) — only then pick a random replacement; idle if no players remain.
        chaseTarget = MultiplayerWorld.ValidateStickyTarget(chaseTarget, anyArea: true);
        if (chaseTarget == null)
        {
            chaseTarget = MultiplayerWorld.PickStickyTarget(anyArea: true);
            if (chaseTarget == null) return;
        }

        // Seek the player along the ground plane only (ignore the height difference).
        Vector3 toPlayer = chaseTarget.position - transform.position;
        Vector3 toPlayerFlat = new Vector3(toPlayer.x, 0f, toPlayer.z);

        if (toPlayerFlat.sqrMagnitude > 0.0001f)
        {
            rb.AddForce(toPlayerFlat.normalized * chaseAcceleration, ForceMode.Acceleration);

            // Cap only the horizontal speed; leave the vertical axis to gravity / landing.
            Vector3 vel = rb.linearVelocity;
            Vector3 horizontal = new Vector3(vel.x, 0f, vel.z);
            if (horizontal.magnitude > chaseMaxSpeed)
            {
                horizontal = horizontal.normalized * chaseMaxSpeed;
                rb.linearVelocity = new Vector3(horizontal.x, vel.y, horizontal.z);
            }

            Quaternion targetRot = Quaternion.LookRotation(toPlayerFlat.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, rotationCorrection * Time.fixedDeltaTime));
        }

        // Imitate gravity (the drone's own useGravity is off) so it sits on and drives along the ground.
        rb.AddForce(Vector3.down * chaseDownforce, ForceMode.Acceleration);

        TryShoot();
    }

    /// <summary>
    /// Computes the muzzle position dynamically from this drone's current transform.
    /// Always attached to the drone because it uses the live transform each call —
    /// no serialized reference that could break on instantiation.
    /// </summary>
    Vector3 GetMuzzlePosition()
    {
        return transform.position
             + transform.forward * muzzleForwardOffset
             + transform.up * muzzleVerticalOffset;
    }

    /// <summary>
    /// Checks if the player is within the vision cone ahead and no other drone
    /// is closer in front. If so, fires a projectile at the player at the
    /// configured fire rate.
    /// </summary>
    void TryShoot()
    {
        if (projectilePrefab == null) return;

        Vector3 origin = GetMuzzlePosition();
        Vector3 forward = transform.forward;

        // Determine whether the player is currently in the cone with no drone blocking.
        bool hasSight = HasClearShotAtPlayer(origin, forward, out Vector3 targetPoint);

        // --- Sight transitions reset the burst cycle ---
        // If we just regained sight after losing it, restart the cycle fresh in the
        // firing window so the player always gets shot at promptly on re-acquisition.
        if (hasSight && !hadSightLastFrame)
        {
            fireState = FireState.Firing;
            fireStateTimer = 0f;
        }
        hadSightLastFrame = hasSight;

        // No sight → don't advance the cycle or fire. The cycle stays frozen and
        // will reset on next re-acquisition.
        if (!hasSight) return;

        // --- Advance the burst state machine ---
        fireStateTimer += Time.fixedDeltaTime;

        if (fireState == FireState.Firing)
        {
            // During the firing window, fire at the configured rate
            if (Time.time - lastFireTime >= 1f / fireRate)
            {
                FireAt(targetPoint, origin);
                lastFireTime = Time.time;
            }

            // Firing window elapsed → switch to cooldown
            if (fireStateTimer >= fireWindowDuration)
            {
                fireState = FireState.Cooldown;
                fireStateTimer = 0f;
            }
        }
        else // Cooldown
        {
            // Cooldown elapsed → switch back to firing
            if (fireStateTimer >= fireCooldownDuration)
            {
                fireState = FireState.Firing;
                fireStateTimer = 0f;
            }
        }
    }

    /// <summary>
    /// Returns true if the player is within the vision cone and no drone is
    /// closer in front. Outputs the player's current position for aiming.
    /// Extracted from the old TryShoot so the burst logic can call it cleanly.
    /// </summary>
    bool HasClearShotAtPlayer(Vector3 origin, Vector3 forward, out Vector3 targetPoint)
    {
        targetPoint = Vector3.zero;

        Collider[] candidates = Physics.OverlapSphere(origin, visionRange, visionMask);

        GameObject closestDrone = null; float closestDroneDist = float.MaxValue;
        GameObject closestPlayer = null; float closestPlayerDist = float.MaxValue;

        foreach (var col in candidates)
        {
            GameObject obj = col.gameObject;
            if (obj.GetComponentInParent<DroneCar>() == this) continue;

            Vector3 toObj = col.bounds.center - origin;
            float dist = toObj.magnitude;
            if (dist < 0.001f) continue;

            float angle = Vector3.Angle(forward, toObj.normalized);
            if (angle > visionHalfAngle) continue;

            if (IsDrone(obj))
            {
                if (dist < closestDroneDist) { closestDroneDist = dist; closestDrone = obj; }
            }
            else if (IsPlayerForShooting(obj))
            {
                if (dist < closestPlayerDist) { closestPlayerDist = dist; closestPlayer = obj; }
            }
        }

        if (closestPlayer == null) return false;
        if (closestDrone != null && closestDroneDist < closestPlayerDist) return false;

        targetPoint = closestPlayer.GetComponent<Collider>() != null
            ? closestPlayer.GetComponent<Collider>().bounds.center
            : closestPlayer.transform.position;
        return true;
    }

    void FireAt(Vector3 targetPoint, Vector3 origin)
    {
        // Aim at the player's CURRENT position at the moment of firing
        Vector3 direction = (targetPoint - origin).normalized;

        Quaternion rot = Quaternion.LookRotation(direction, Vector3.up);
        GameObject proj = Instantiate(projectilePrefab, origin, rot);

        // Put the spawned projectile on its dedicated layer for collision-matrix filtering.
        ApplyProjectileLayer(proj);

        var projectile = proj.GetComponent<DroneProjectile>();

        // 3D drone fire sound at the muzzle, using the projectile's own tweakable 3D settings.
        AudioManager.PlayDroneShoot(origin, projectile != null ? projectile.audio3D : null);

        if (projectile != null)
            projectile.Launch(direction, projectileSpeed);

        // Multiplayer host: stream this projectile to the clients (visual-only puppets there;
        // hits stay host-authoritative). No-op in single-player / on clients.
        NpcReplicator.Track(proj, NpcKind.Projectile, projectilePrefab);

        // Prevent the projectile from colliding with THIS drone as it spawns
        var myCol = GetComponentInChildren<Collider>();
        var projCol = proj.GetComponent<Collider>();
        if (myCol != null && projCol != null)
            Physics.IgnoreCollision(projCol, myCol);
    }

    /// <summary>Puts a freshly-spawned projectile (and any children) on the configured
    /// projectile layer. No-op, with a one-time warning, if that layer isn't defined.</summary>
    void ApplyProjectileLayer(GameObject proj)
    {
        if (proj == null || string.IsNullOrEmpty(projectileLayerName)) return;

        int layer = LayerMask.NameToLayer(projectileLayerName);
        if (layer < 0)
        {
            if (!warnedMissingProjectileLayer)
            {
                warnedMissingProjectileLayer = true;
                Debug.LogWarning($"[DroneCar] Layer '{projectileLayerName}' not found in Tags and " +
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

    bool IsDrone(GameObject obj)
    {
        // A drone has a DroneCar component somewhere in its hierarchy
        return obj.GetComponentInParent<DroneCar>() != null;
    }

    bool IsPlayerForShooting(GameObject obj)
    {
        Transform t = obj.transform;
        while (t != null)
        {
            // Remote players' solid puppets carry the RemotePlayer tag — on the multiplayer host
            // (the only place drones simulate) they're shoot-at-able exactly like the local player.
            if (t.CompareTag(playerTagForShooting) || t.CompareTag("RemotePlayer")) return true;
            t = t.parent;
        }
        return false;
    }

    // Draws the spherecast in the Scene view so you can see the drone's vision.
    // Green = casting and seeing nothing, Red = hit something, Yellow spheres
    // mark the start and end of the cast.
    void OnDrawGizmos()
    {
        Vector3 origin = GetMuzzlePosition();
        Vector3 forward = transform.forward;

        Gizmos.color = Color.green;

        // Draw the cone as a series of rings expanding with distance, plus edge lines
        int rings = 5;
        int segmentsPerRing = 16;

        Vector3 right = transform.right;
        Vector3 up = transform.up;

        Vector3 prevRingCenter = origin;
        for (int r = 1; r <= rings; r++)
        {
            float dist = (visionRange / rings) * r;
            // Cone radius at this distance = dist * tan(halfAngle)
            float radius = dist * Mathf.Tan(visionHalfAngle * Mathf.Deg2Rad);
            Vector3 ringCenter = origin + forward * dist;

            // Draw the ring
            Vector3 prevPoint = ringCenter + right * radius;
            for (int s = 1; s <= segmentsPerRing; s++)
            {
                float t = (s / (float)segmentsPerRing) * Mathf.PI * 2f;
                Vector3 point = ringCenter + (right * Mathf.Cos(t) + up * Mathf.Sin(t)) * radius;
                Gizmos.DrawLine(prevPoint, point);
                prevPoint = point;
            }

            // Draw 4 edge lines from origin out to this ring (only on the last ring)
            if (r == rings)
            {
                Gizmos.DrawLine(origin, ringCenter + right * radius);
                Gizmos.DrawLine(origin, ringCenter - right * radius);
                Gizmos.DrawLine(origin, ringCenter + up * radius);
                Gizmos.DrawLine(origin, ringCenter - up * radius);
            }
        }

        // Center line
        Gizmos.DrawLine(origin, origin + forward * visionRange);
    }

    // -------------------------------------------------------
    //  Player Contact Detection
    // -------------------------------------------------------

    // Set true once a player hits this drone. Correction drops to the soft
    // values permanently (no recovery) and downforce pulls the drone off-track.
    private bool playerHit = false;

    // Who last shoved this drone (multiplayer bounty attribution): the local (host) player, or a
    // remote player's clientId. Remote puppets are solid + tagged RemotePlayer, so on the host sim
    // ANY player can physically knock a drone off — the bounty goes to whoever actually did it.
    private bool lastHitByRemote;
    private ulong lastHitClientId;

    // Guards the bounty so multiple colliders crossing the kill floor in one frame
    // (before this object is actually destroyed) can't pay out more than once.
    private bool bountyClaimed = false;

    /// <summary>
    /// Called by the kill floor as this car is destroyed there. If a player knocked it off the track
    /// (playerHit), awards its credit bounty to THAT player — the local inventory for the local
    /// (host) player, or a bounty message to the remote player's machine.
    /// </summary>
    public void AwardKnockoffBounty()
    {
        if (bountyClaimed) return;
        bountyClaimed = true;

        if (!playerHit) return;   // not knocked off by a player — no reward

        if (lastHitByRemote)
        {
            NpcReplicator.SendBounty(lastHitClientId, creditReward);
            Debug.Log($"[DroneCar] Remote player (client {lastHitClientId}) knocked this car off — " +
                      $"bounty {creditReward} sent.");
            return;
        }

        if (PlayerInventory.Instance == null) return;
        PlayerInventory.Instance.AddCredits(creditReward);
        AudioManager.PlayKnockoffBounty();   // 2D reward stinger
        Debug.Log($"[DroneCar] Player knocked this car into the kill floor — " +
                  $"awarded {creditReward} credits");
    }

    // Support Ship laser damage. Unlike a player ramming it, one bolt isn't enough — it takes
    // `hitsToDown` of them, with NO invulnerability between (deliberately: a gunner walking rounds onto
    // a drone should be rewarded for accuracy, not throttled).
    private int laserHits;

    /// <summary>A Support Ship laser round landed. Once enough have, the drone enters exactly the same
    /// downed state a player ram produces — soft correction, downforce, off the track — and the PILOT is
    /// recorded as the last one to hit it, so the kill-floor bounty follows them.
    ///
    /// Attribution reuses the existing "last toucher wins" fields rather than adding a parallel system,
    /// which is what makes the user's rule fall out for free: a player who shoulder-checks the drone
    /// AFTER the gunner softened it simply overwrites these in RegisterPlayerContact and takes the
    /// credits, and vice versa.</summary>
    public void TakeLaserHit(ulong pilotClientId, bool pilotIsLocal, int hitsToDown)
    {
        if (++laserHits < Mathf.Max(1, hitsToDown)) return;

        playerHit = true;
        lastHitByRemote = !pilotIsLocal;
        lastHitClientId = pilotClientId;
    }

    void OnCollisionEnter(Collision collision) => RegisterPlayerContact(collision.collider);
    void OnCollisionStay(Collision collision) => RegisterPlayerContact(collision.collider);

    void RegisterPlayerContact(Collider other)
    {
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                playerHit = true;
                lastHitByRemote = false;
                return;
            }
            if (t.CompareTag("RemotePlayer"))
            {
                playerHit = true;
                if (MultiplayerWorld.TryGetCarOwner(t, out ulong clientId, out bool isLocal) && !isLocal)
                {
                    lastHitByRemote = true;
                    lastHitClientId = clientId;
                }
                return;
            }
            t = t.parent;
        }
    }
}