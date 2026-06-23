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

    private Rigidbody rb;
    private float spawnTime;
    private bool passedApex;
    private float homingElapsed;            // seconds spent homing since apex
    private Transform playerTransform;
    private CarController playerCar;        // player's controller, for its airborne state

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        spawnTime = Time.time;

        // Find the player once at spawn � homing checks this each frame.
        // If the player is destroyed/replaced (e.g. after a scene reload), the
        // reference becomes null and homing simply stops applying force.
        var playerObj = GameObject.FindWithTag(playerTag);
        if (playerObj != null)
        {
            playerTransform = playerObj.transform;
            playerCar = playerObj.GetComponent<CarController>();
        }
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
            passedApex = true;

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

    /// <summary>
    /// Steers the boulder toward the player's current position in full 3D. Gravity is
    /// switched off for the homing window (see FixedUpdate), so the boulder behaves like
    /// a heat-seeking missile — diving in from any angle rather than just arcing past.
    /// </summary>
    void ApplyHoming()
    {
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

    /// <summary>True when the player car reports it's airborne. False if there's no player or it
    /// has no CarController — then only the ground homing duration applies.</summary>
    bool IsPlayerAirborne() => playerCar != null && playerCar.IsAirborne;

    void Update()
    {
        // Despawn when below kill height or after max lifetime
        if (transform.position.y < killHeight || Time.time - spawnTime > maxLifetime)
            Destroy(gameObject);
    }
}