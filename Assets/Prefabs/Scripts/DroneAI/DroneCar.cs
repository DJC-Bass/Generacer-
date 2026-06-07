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
        if (path == null || !path.IsReady) return;
        if (finished) return;

        pathDistance += pathSpeed * Time.fixedDeltaTime;

        path.Sample(pathDistance, out Vector3 targetPos, out Vector3 tangent);
        targetPos += Vector3.up * verticalSpawnOffset;

        if (pathDistance >= path.TotalLength)
        {
            finished = true;
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

        var projectile = proj.GetComponent<DroneProjectile>();
        if (projectile != null)
            projectile.Launch(direction, projectileSpeed);

        // Prevent the projectile from colliding with THIS drone as it spawns
        var myCol = GetComponentInChildren<Collider>();
        var projCol = proj.GetComponent<Collider>();
        if (myCol != null && projCol != null)
            Physics.IgnoreCollision(projCol, myCol);
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
            if (t.CompareTag(playerTagForShooting)) return true;
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

    // Set true once the player hits this drone. Correction drops to the soft
    // values permanently (no recovery) and downforce pulls the drone off-track.
    private bool playerHit = false;

    void OnCollisionEnter(Collision collision)
    {
        if (IsPlayer(collision.collider))
            playerHit = true;
    }

    void OnCollisionStay(Collision collision)
    {
        if (IsPlayer(collision.collider))
            playerHit = true;
    }

    bool IsPlayer(Collider other)
    {
        // Walk up the hierarchy in case a wheel or sub-collider made contact
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag)) return true;
            t = t.parent;
        }
        return false;
    }
}