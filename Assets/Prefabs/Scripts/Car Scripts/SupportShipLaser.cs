using UnityEngine;

/// <summary>
/// A twin-bolt laser round fired by a <see cref="SupportShip"/>. The prefab is the whole PAIR (a
/// LeftLaser and a RightLaser under one root), so one shot is one Instantiate.
///
/// Lifetime behaves like <see cref="DroneProjectile"/>: it flies dead straight with no gravity and
/// dies on the first thing it touches, or after <see cref="maxLifetime"/> if it hits nothing. Timing
/// out is a single float compare per frame, which is why it's a timer rather than a travelled-distance
/// check — same result, less work, and the speed is fixed so the two are equivalent anyway.
///
/// WHAT A ROUND DOES depends on what it hits:
///  • A PLAYER CAR — including the gunner's OWN racer, which is deliberately shootable — gets popped
///    into the air at half a lightning strike's force, keeping its momentum (a DronePissBall halts the
///    car; this does not). Its own 2 s window then ignores further laser rounds. That window is kept
///    SEPARATE from the DronePissBall one, so being lasered never grants immunity to drone fire.
///  • A DRONE PLANE spends one point of its health pool (`DronePlane.maxHits`, 1 by default so the
///    stock plane still dies in one) and the bounty is redirected to the GUNNER. The PLANE counts the
///    round, not this script — see TryHitDronePlane.
///  • A DRONE CAR / CHALLENGER takes <see cref="droneHitsToDown"/> rounds with no window between them,
///    then enters the same downed state a player ram produces. The credits are settled at the kill
///    floor and go to whoever touched it LAST — gunner or driver.
///  • A LAVA BOULDER is destroyed outright for <see cref="boulderBounty"/>.
///  • ANOTHER PROJECTILE (<see cref="cancelLayerName"/>) is CANCELLED — both die. This is what lets a
///    gunner shoot incoming drone fire out of the air before it reaches the racer, and it needs the
///    collision matrix to permit the contact (see that field's tooltip).
///
/// It still ignores the ship that fired it — a round spawning inside its own muzzle would die instantly
/// — but nothing else. Watching your own racer is the pilot's problem.
///
/// Multiplayer: spawned only by the HOST (routed there by SupportShipReplicator), so every one of these
/// judgements is made once, on the machine that owns the drones and obstacles. Effects that land on a
/// player car are routed to the machine that owns THAT car.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SupportShipLaser : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("Max seconds before the round despawns if it never hits anything. At the default speed " +
             "this is also what sets its effective range.")]
    public float maxLifetime = 3f;

    [Header("Player Hit")]
    [Tooltip("Upward impulse applied to a player car on hit (velocity change, m/s). Half a lightning " +
             "strike's 80 by default. Their forward momentum is NOT halted — unlike a DronePissBall, " +
             "this bumps a car into the air without stopping it.")]
    public float popUpForce = 40f;
    [Tooltip("Seconds a car ignores further SUPPORT SHIP rounds after one lands. Deliberately its own " +
             "window, separate from DroneProjectile's: being lasered must not make you immune to drone " +
             "fire, and vice versa.")]
    public float hitInvulnerabilitySeconds = 2f;
    [Tooltip("Tag identifying the local player's car.")]
    public string playerTag = "Player";

    [Header("Damage")]
    [Tooltip("Rounds needed to down a DroneCar or Challenger. They get NO window between hits, so a " +
             "steady burst can drop one. DronePlanes have their OWN pool — see DronePlane.maxHits.")]
    public int droneHitsToDown = 3;
    [Tooltip("Credits paid to the GUNNER for destroying a LavaBoulder outright.")]
    public int boulderBounty = 25;
    [Tooltip("Layer whose objects this round CANCELS on contact — both are destroyed. Aimed at drone " +
             "fire (DronePissBalls), so the gunner can shoot incoming shots down before they reach the " +
             "racer.\n\n" +
             "⚠️ This only works if the collision MATRIX lets the two touch. Rounds are themselves on " +
             "the Projectile layer by default, so cancelling Projectiles means ticking Projectile ↔ " +
             "Projectile — which also makes drone shots cancel EACH OTHER. To avoid that, give the " +
             "laser prefab its own layer and leave this pointing at Projectile.")]
    public string cancelLayerName = "Projectile";

    [Header("Audio (3D, at the impact)")]
    [Tooltip("3D tuning for the round hitting something it does NOTHING to — the track, a wall. A miss " +
             "wants a tighter max distance than a kill: the gunner should hear their own strays, but " +
             "the whole lobby shouldn't.")]
    public Spatial3DSettings environmentAudio3D = new Spatial3DSettings();
    [Tooltip("3D tuning for the round actually DOING something — popping a car, damaging a drone, " +
             "bursting a boulder. Give this the more generous range of the two; it is the sound that " +
             "tells everyone nearby a shot counted. (The FIRING sound is separate again, tuned from " +
             "AudioLibrary.supportShipAudio3D with the rest of the ship's audio.)")]
    public Spatial3DSettings entityAudio3D = new Spatial3DSettings();

    /// <summary>Who is flying the ship that fired this — every bounty this round earns goes to them.
    /// Stamped at spawn by <see cref="SupportShip.FireLaser"/>.</summary>
    [HideInInspector] public ulong pilotClientId;
    /// <summary>True when that pilot is playing on THIS machine, so bounties land in the local
    /// inventory instead of going out over the wire.</summary>
    [HideInInspector] public bool pilotIsLocal = true;

    private Rigidbody rb;
    private float spawnTime;
    private bool consumed;

    // ---- The local player's LASER invulnerability window (STATIC = shared by every round in flight,
    //      which is the point: the window belongs to the CAR, not to any one round). Each machine owns
    //      its own car's window — the host tracks its own, a client applies the same test to a
    //      host-reported hit — so nothing about it has to be replicated. ----
    static float invulnerableUntil = -999f;

    /// <summary>True while the local player is still immune to Support Ship rounds. Completely
    /// independent of <see cref="DroneProjectile.PlayerInvulnerable"/>.</summary>
    public static bool PlayerInvulnerable => Time.time < invulnerableUntil;

    /// <summary>Opens (or extends) the local player's immunity window.</summary>
    public static void BeginInvulnerability(float seconds)
    {
        if (seconds <= 0f) return;
        invulnerableUntil = Mathf.Max(invulnerableUntil, Time.time + seconds);
    }

    // Statics survive a play-mode restart when domain reload is disabled, and Time.time restarts at 0 —
    // a stale future value would leave the player permanently immune. Clear it on every load.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => invulnerableUntil = -999f;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;   // travels in a straight line, not an arc
        // At ~700 m/s a round covers ~12 m per physics step, so discrete detection would tunnel it
        // clean through the track and most targets. This is the difference between a working gun and
        // one that appears to shoot blanks.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        spawnTime = Time.time;
    }

    /// <summary>Send it on its way at a given speed (m/s), ADDED to the velocity of whatever fired
    /// it.
    ///
    /// <paramref name="speedMs"/> is therefore muzzle velocity — speed RELATIVE TO THE SHOOTER, not
    /// through the world. That is the only way a round reliably outruns the thing that fired it: a
    /// Support Ship escorting a car at 600 m/s would otherwise watch its own 700 m/s rounds crawl away
    /// at 100, hanging in the ship's face long enough to collide with the car, each other, or the ship
    /// as it overtook them. Same rule GrappleHook fires by, for the same reason.</summary>
    public void Launch(Vector3 direction, float speedMs, Vector3 inheritedVelocity)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        rb.linearVelocity = direction.normalized * speedMs + inheritedVelocity;
    }

    /// <summary>Makes this round pass straight through everything under <paramref name="root"/>.
    /// Active colliders only — Physics.IgnoreCollision errors on a disabled one, and a car is full of
    /// those (shield, jet flames, SD effects, the ship template).</summary>
    public void IgnoreCollisionsWith(Transform root)
    {
        if (root == null) return;

        var mine = GetComponentsInChildren<Collider>(true);
        var theirs = root.GetComponentsInChildren<Collider>(false);
        foreach (var a in mine)
        {
            if (a == null || !a.gameObject.activeInHierarchy) continue;
            foreach (var b in theirs)
                if (b != null && b.gameObject.activeInHierarchy) Physics.IgnoreCollision(a, b, true);
        }
    }

    void Update()
    {
        if (Time.time - spawnTime > maxLifetime) Destroy(gameObject);
    }

    // -------------------------------------------------------
    //  Impact
    // -------------------------------------------------------

    /// <summary>Dies on the first contact, having applied whatever that contact deserves. Ordered
    /// most-specific first: the drone/obstacle checks look at the object itself, while the player check
    /// walks UP the hierarchy for a tag — so doing the player walk first would claim hits on anything
    /// that merely happens to be parented under a car.</summary>
    void OnCollisionEnter(Collision collision)
    {
        if (consumed) return;
        consumed = true;

        var hit = collision.collider;

        if (TryCancelProjectile(hit)) { Finish(true); return; }
        if (TryHitDronePlane(hit)) { Finish(true); return; }
        if (TryHitDroneCar(hit)) { Finish(true); return; }
        if (TryHitBoulder(hit)) { Finish(true); return; }
        Finish(TryHitPlayer(hit));
    }

    /// <summary>Impact sound, then despawn. <paramref name="effective"/> picks WHICH sound: a round that
    /// changed something gets the solid hit, everything else gets the dull environment tick. That
    /// distinction is the gunner's only feedback on whether a shot counted — they're flying from the
    /// hub with no hit markers — so it is worth getting right rather than playing one sound for both.
    ///
    /// A round ABSORBED by a car's invulnerability window counts as environment: nothing happened, and
    /// it should not sound like it did. Same convention DroneProjectile uses for its own absorbed hits.
    /// (One known imprecision: a hit routed to a REMOTE player is assumed to land, because their window
    /// lives on their machine and asking would cost a round trip. DroneProjectile has the same limit.)</summary>
    void Finish(bool effective)
    {
        if (effective) AudioManager.PlaySupportShipLaserHitEntity(transform.position, entityAudio3D);
        else AudioManager.PlaySupportShipLaserHitEnvironment(transform.position, environmentAudio3D);

        // Rounds only exist on the host, so this is the only machine that reaches here — everyone else
        // has a collider-less puppet that can never register a contact. Tell them, or the gunner (very
        // often a client) gets no impact feedback at all, which is the one thing this audio is FOR.
        SupportShipReplicator.ReportShotSound(
            transform.position,
            effective ? SupportShipReplicator.ShotSound.HitEntity
                      : SupportShipReplicator.ShotSound.HitEnvironment);

        Destroy(gameObject);
    }

    /// <summary>Mutual destruction with another projectile — the gunner shooting incoming drone fire out
    /// of the air before it reaches the racer.
    ///
    /// Checked FIRST, and by LAYER rather than by component, so it covers anything that flies: a
    /// DronePissBall, another gunner's round, or any future Projectile-layer object that has no script
    /// this file knows about.
    ///
    /// Only the OTHER object is destroyed here; this round dies on its own in Finish(). Note that a
    /// DroneProjectile would in fact remove itself anyway — it despawns on any collision at all — but
    /// relying on that would mean silent pass-through for anything that isn't one.</summary>
    bool TryCancelProjectile(Collider hit)
    {
        if (string.IsNullOrEmpty(cancelLayerName)) return false;

        int layer = LayerMask.NameToLayer(cancelLayerName);
        if (layer < 0 || hit.gameObject.layer != layer) return false;

        // The projectile's ROOT is its rigidbody's object — destroying the struck collider alone could
        // leave a headless husk on anything built as a parent with child colliders.
        GameObject other = hit.attachedRigidbody != null ? hit.attachedRigidbody.gameObject : hit.gameObject;
        if (other == gameObject) return false;   // never cancel ourselves

        Destroy(other);
        return true;
    }

    /// <summary>Reports a hit on a drone plane for the IMPACT SOUND only — it deliberately applies no
    /// damage.
    ///
    /// ⚠️ The plane counts this round itself, in its own OnCollisionEnter. Both objects receive the
    /// event for a single contact, so damaging it from here as well would spend TWO points of its
    /// health pool per round. That was harmless while planes died in one hit and would have silently
    /// halved every tougher plane's durability the moment one existed.</summary>
    bool TryHitDronePlane(Collider hit) => hit.GetComponentInParent<DronePlane>() != null;

    /// <summary>Drone cars and Challengers (the same script, different reward) need several rounds.
    /// The drone records the gunner as its last attacker, so if nobody else touches it before it
    /// reaches the kill floor the credits are theirs.</summary>
    bool TryHitDroneCar(Collider hit)
    {
        var drone = hit.GetComponentInParent<DroneCar>();
        if (drone == null) return false;
        drone.TakeLaserHit(pilotClientId, pilotIsLocal, droneHitsToDown);
        return true;
    }

    /// <summary>Boulders pop in one hit for a small bounty.</summary>
    bool TryHitBoulder(Collider hit)
    {
        var boulder = hit.GetComponentInParent<BoulderObstacle>();
        if (boulder == null) return false;

        SupportShipReplicator.AwardPilot(pilotClientId, pilotIsLocal, boulderBounty);
        Destroy(boulder.gameObject);
        return true;
    }

    /// <summary>Pops a player car — the gunner's own racer included, which is the point: the ship flies
    /// behind its car, so hitting your own teammate is entirely possible and entirely the pilot's
    /// responsibility.
    ///
    /// Returns whether anything actually HAPPENED, not whether a player was struck: a round absorbed by
    /// the invulnerability window returns false so it sounds like the nothing it was.</summary>
    bool TryHitPlayer(Collider hit)
    {
        Transform t = hit.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                // The local car's window is owned right here.
                // Rounds are on the Projectile layer, so an active shield ignores them too — the
                // rule is "shielded = immune to projectiles", not "immune to hostile ones". Carve an
                // exception here if a friendly boost should ever punch through.
                if (PlayerInvulnerable || ShieldAbility.LocalShieldUp) return false;

                ApplyPopUp(t.gameObject, popUpForce);
                BeginInvulnerability(hitInvulnerabilitySeconds);
                return true;
            }
            if (t.CompareTag("RemotePlayer"))
            {
                // Their car lives on their machine and their window is theirs to judge — pushing a
                // kinematic puppet here would be erased by its next state update anyway. We report it
                // as effective because we cannot know otherwise without a round trip.
                if (MultiplayerWorld.TryGetCarOwner(t, out ulong clientId, out bool isLocal) && !isLocal)
                {
                    SupportShipReplicator.SendLaserHitToOwner(clientId);
                    return true;
                }
                return false;
            }
            t = t.parent;
        }
        return false;
    }

    /// <summary>The pop itself: an upward impulse with the car's existing momentum left alone, plus the
    /// suspension trick that lets a grounded car actually leave the road (without it the wheels hold it
    /// down and the impulse does almost nothing).</summary>
    public static void ApplyPopUp(GameObject car, float force)
    {
        if (car == null) return;

        var rb = car.GetComponent<Rigidbody>();
        if (rb != null) rb.AddForce(Vector3.up * force, ForceMode.VelocityChange);

        var controller = car.GetComponent<CarController>();
        if (controller != null) controller.ShortenSuspensionRayForPopUp();
    }
}
