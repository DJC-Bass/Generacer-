using UnityEngine;

/// <summary>
/// Handles a single boulder's launch and spin behaviour. The spawner
/// configures the launch parameters via SetUp() before instantiation
/// finishes � the boulder then takes off, arcs through the air, and
/// despawns once it falls below a kill plane.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BoulderObstacle : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("Y position below which the boulder despawns. Set well below the " +
             "ground floor so boulders can land naturally before being culled.")]
    public float killHeight = -500f;
    [Tooltip("Hard time limit before despawn, in case boulder gets stuck somewhere.")]
    public float maxLifetime = 30f;

    [Header("Air Speed")]
    [Tooltip("Multiplies effective gravity on this boulder. 2 = boulder falls and " +
         "arcs twice as fast as normal. Higher = quicker, more aggressive arcs.")]
    public float gravityMultiplier = 3f;

    [Header("Homing")]
    [Tooltip("Tag of the player car to home onto.")]
    public string playerTag = "Player";
    [Tooltip("How long the boulder homes onto the player after it starts falling, while the " +
             "player is on the GROUND (seconds).")]
    public float homingDuration = 2f;
    [Tooltip("Longer homing window used while the PLAYER is airborne, so the boulder keeps " +
             "tracking them through the air like an anti-air missile (seconds).")]
    public float airborneHomingDuration = 5f;
    [Tooltip("Strength of the homing force (m/s� applied as acceleration). " +
             "Higher = sharper tracking, lower = gentler correction.")]
    public float homingStrength = 60f;
    [Tooltip("Maximum total speed the boulder can reach while homing (m/s). " +
             "Caps how aggressively the missile can dive toward the player.")]
    public float maxHomingSpeed = 80f;

    [Header("Multiplayer")]
    [Tooltip("Ceiling (m/s) on the velocity change a boulder may hand a REMOTE player it rams. Only " +
             "used in multiplayer: the host computes the hit for a client, whose car it cannot " +
             "simulate. A safety valve, not the usual case - most hits land well under it.")]
    public float maxShoveSpeed = 150f;

    private Rigidbody rb;
    private float spawnTime;
    private bool passedApex;
    private float homingElapsed;            // seconds spent homing since apex
    private Transform playerTransform;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        spawnTime = Time.time;

        // The target is chosen at APEX, not here - see AcquireTarget.
    }

    /// <summary>
    /// Called by the spawner immediately after instantiation to assign
    /// physical properties and apply the initial launch impulse.
    /// </summary>
    public void Launch(float scale, float mass, Vector3 launchVelocity, Vector3 spinAxis, float spinSpeed)
    {
        transform.localScale = Vector3.one * scale;
        rb.mass = mass;

        // Apply the launch as a velocity assignment rather than AddForce �
        // we want the boulder to start with this exact velocity, no ramp-up.
        rb.linearVelocity = launchVelocity;

        // Spin via angular velocity � physics will preserve this naturally
        // and tumble the boulder during flight, with mass-based resistance
        // to angular impacts when it hits something.
        rb.angularVelocity = spinAxis.normalized * spinSpeed;
    }
    void FixedUpdate()
    {
        // Detect the moment the boulder transitions from rising to falling.
        // Once vertical velocity goes negative, open the homing window.
        if (!passedApex && rb.linearVelocity.y < 0f)
        {
            passedApex = true;
            AcquireTarget();
        }

        // The homing window lasts longer while the player is airborne, so the boulder keeps
        // chasing them through the air like an anti-air missile. Evaluated live: it extends the
        // moment the player leaves the ground and reverts to the ground window once they land.
        float maxHomingDuration = IsPlayerAirborne() ? airborneHomingDuration : homingDuration;
        bool homing = passedApex && homingElapsed < maxHomingDuration;

        // Heat-seeking-missile mode: while homing, gravity is switched OFF so the
        // boulder flies straight at the player instead of arcing down. The instant
        // the window closes, normal (multiplied) gravity resumes and it falls again.
        rb.useGravity = !homing;

        if (homing)
        {
            ApplyHoming();
            homingElapsed += Time.fixedDeltaTime;
        }
        else if (gravityMultiplier > 1f)
        {
            // Extra gravity on top of world gravity — applied before homing starts and
            // again after it ends, giving the assigned Gravity Multiplier fall speed.
            Vector3 extraGravity = Physics.gravity * (gravityMultiplier - 1f);
            rb.AddForce(extraGravity, ForceMode.Acceleration);
        }
    }

    /// <summary>Chooses the one player this boulder hunts, and keeps it for the rest of its life.
    ///
    /// Deliberately run at APEX rather than at spawn. The boulder launches from the ground and climbs
    /// for seconds before it can steer at anything, so a pick made at spawn asks "who is airborne?" at
    /// a moment when the answer cannot matter yet and will be stale by the time it does. Choosing as
    /// the homing window OPENS is what makes the boulder read as anti-air.
    ///
    /// preferAirborne narrows the draw to players actually in the air, then picks at RANDOM among them,
    /// so with two players mid-jump each is equally likely to be hunted. With nobody airborne the full
    /// in-track pool stands and a shower falls on a grounded field exactly as it always did. Single
    /// player is unaffected: the pool is one car either way.</summary>
    void AcquireTarget()
    {
        playerTransform = MultiplayerWorld.PickStickyTarget(anyArea: false, preferAirborne: true);
    }

    /// <summary>
    /// Steers the boulder toward the player's current position in full 3D. Gravity is
    /// switched off for the homing window (see FixedUpdate), so the boulder behaves like
    /// a heat-seeking missile — diving in from any angle rather than just arcing past.
    /// </summary>
    void ApplyHoming()
    {
        // A boulder is ballistic: if its player left the track mid-flight it does NOT retarget —
        // homing just cuts out and the arc continues naturally (drones are the retargeting entities).
        playerTransform = MultiplayerWorld.ValidateStickyTarget(playerTransform, anyArea: false);
        if (playerTransform == null) return;

        // Full 3D direction from boulder to player (vertical included). Gravity is off
        // during the homing window, so steering up/down no longer fights the fall —
        // the boulder can dive in from any angle like a real heat-seeking missile.
        Vector3 toPlayer = playerTransform.position - transform.position;
        if (toPlayer.sqrMagnitude < 0.01f) return;

        rb.AddForce(toPlayer.normalized * homingStrength, ForceMode.Acceleration);

        // Cap TOTAL speed so the boulder tracks hard without accelerating forever.
        if (rb.linearVelocity.magnitude > maxHomingSpeed)
            rb.linearVelocity = rb.linearVelocity.normalized * maxHomingSpeed;
    }

    /// <summary>True when the targeted player is off the ground - the local car or a remote one alike.
    ///
    /// ⚠️ This used to hold a cached CarController, which a stripped remote puppet does not have. The
    /// null meant "not airborne", so a boulder that drew a CLIENT never opened its longer airborne
    /// homing window: it dived at an airborne host and gave up on an airborne client at the same jump.
    /// MultiplayerWorld.IsPlayerAirborne answers for both.</summary>
    bool IsPlayerAirborne() => MultiplayerWorld.IsPlayerAirborne(playerTransform);

    void Update()
    {
        // Despawn when below kill height or after max lifetime
        if (transform.position.y < killHeight || Time.time - spawnTime > maxLifetime)
            Destroy(gameObject);
    }

    /// <summary>A boulder has no damage model - its entire effect on a player is MOMENTUM, and in
    /// single-player (and on the host's own car) the solver delivers that with no code at all. That is
    /// why this handler did not exist.
    ///
    /// ⚠️ It has to exist for multiplayer, and the reason is not obvious: on the host, a client's car is
    /// a stripped KINEMATIC puppet. A dynamic boulder striking a kinematic body does not move it, so the
    /// hit went nowhere - not to the puppet, and with no event to relay, not to the real car either.
    /// Boulders could hit the host and nobody else. Here the host works out the velocity change the
    /// collision would have caused and sends it to the owner to apply.</summary>
    void OnCollisionEnter(Collision collision)
    {
        if (!MultiplayerWorld.IsMultiplayerGame) return;

        Transform t = collision.transform;
        while (t != null)
        {
            // Our own car needs nothing: the solver just did the real thing to a real Rigidbody.
            if (t.CompareTag(playerTag)) return;
            if (t.CompareTag("RemotePlayer")) { ShoveRemotePlayer(t, collision); return; }
            t = t.parent;
        }
    }

    /// <summary>Works out what this collision should do to a remote player's car and sends it.
    ///
    /// The mass ratio is the point. Unity's default material is not bouncy, so a real hit is close to
    /// perfectly inelastic: the car comes away with m/(m+M) of the closing speed. A boulder is 1500-6000
    /// kg against a car of one or two tonnes, so a square hit hands over most of its speed - which is
    /// precisely why being hit by one on the host feels like being swatted, and why anything gentler
    /// would not read as the same event.</summary>
    void ShoveRemotePlayer(Transform carRoot, Collision collision)
    {
        if (!MultiplayerWorld.TryGetCarOwner(carRoot, out ulong clientId, out bool isLocal) || isLocal)
            return;

        // The puppet is kinematic, so its OWN Rigidbody reports no velocity - the replicated one does.
        // Its mass, though, is untouched by the strip and is the real car's.
        var carBody = carRoot.GetComponent<Rigidbody>();
        var sync = carRoot.GetComponent<RemoteCarPuppet>();
        float carMass = carBody != null ? Mathf.Max(1f, carBody.mass) : 1000f;
        Vector3 carVelocity = sync != null ? sync.CurrentVelocity : Vector3.zero;

        if (collision.contactCount == 0) return;
        // ContactPoint.normal points from the other collider toward US, so the car is pushed along -n.
        Vector3 n = collision.GetContact(0).normal;
        float closing = Vector3.Dot(rb.linearVelocity - carVelocity, -n);
        if (closing <= 0f) return;   // separating, or they ran into us - not a hit worth sending

        float dv = Mathf.Min(closing * (rb.mass / (rb.mass + carMass)), Mathf.Max(0f, maxShoveSpeed));
        NpcReplicator.SendShoveToClient(clientId, -n * dv);
    }
}