using UnityEngine;

/// <summary>
/// Handles a single boulder's launch and spin behaviour. The spawner
/// configures the launch parameters via SetUp() before instantiation
/// finishes — the boulder then takes off, arcs through the air, and
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
    [Tooltip("How long the boulder homes onto the player after it starts falling (seconds).")]
    public float homingDuration = 2f;
    [Tooltip("Strength of the homing force (m/s² applied as acceleration). " +
             "Higher = sharper tracking, lower = gentler correction.")]
    public float homingStrength = 60f;
    [Tooltip("Maximum sideways speed the boulder can gain from homing (m/s). " +
             "Caps how aggressively the boulder can swerve toward the player.")]
    public float maxHomingSpeed = 80f;

    private Rigidbody rb;
    private float spawnTime;
    private bool passedApex;
    private float homingTimer;
    private Transform playerTransform;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        spawnTime = Time.time;

        // Find the player once at spawn — homing checks this each frame.
        // If the player is destroyed/replaced (e.g. after a scene reload), the
        // reference becomes null and homing simply stops applying force.
        var playerObj = GameObject.FindWithTag(playerTag);
        if (playerObj != null) playerTransform = playerObj.transform;
    }

    /// <summary>
    /// Called by the spawner immediately after instantiation to assign
    /// physical properties and apply the initial launch impulse.
    /// </summary>
    public void Launch(float scale, float mass, Vector3 launchVelocity, Vector3 spinAxis, float spinSpeed)
    {
        transform.localScale = Vector3.one * scale;
        rb.mass = mass;

        // Apply the launch as a velocity assignment rather than AddForce —
        // we want the boulder to start with this exact velocity, no ramp-up.
        rb.linearVelocity = launchVelocity;

        // Spin via angular velocity — physics will preserve this naturally
        // and tumble the boulder during flight, with mass-based resistance
        // to angular impacts when it hits something.
        rb.angularVelocity = spinAxis.normalized * spinSpeed;
    }
    void FixedUpdate()
    {
        // The original gravityMultiplier extra gravity stays as before
        if (gravityMultiplier > 1f)
        {
            Vector3 extraGravity = Physics.gravity * (gravityMultiplier - 1f);
            rb.AddForce(extraGravity, ForceMode.Acceleration);
        }

        // Detect the moment the boulder transitions from rising to falling.
        // Once vertical velocity goes negative, start the homing window.
        if (!passedApex && rb.linearVelocity.y < 0f)
        {
            passedApex = true;
            homingTimer = homingDuration;
        }

        // Homing only runs after apex and only during the timed window
        if (passedApex && homingTimer > 0f)
        {
            ApplyHoming();
            homingTimer -= Time.fixedDeltaTime;
        }
    }

    /// <summary>
    /// Adjusts the boulder's horizontal velocity toward the player's current
    /// position. Vertical velocity is preserved so gravity continues pulling
    /// the boulder down at its natural rate — homing only steers, never slows
    /// or speeds the fall.
    /// </summary>
    void ApplyHoming()
    {
        if (playerTransform == null) return;

        // Direction from boulder to player, horizontal only — we don't want
        // homing to fight gravity by adjusting vertical velocity
        Vector3 toPlayer = playerTransform.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.01f) return;

        Vector3 homingDir = toPlayer.normalized;

        // Apply horizontal acceleration toward the player
        Vector3 horizontalAccel = homingDir * homingStrength;
        rb.AddForce(horizontalAccel, ForceMode.Acceleration);

        // Cap horizontal speed so the boulder can't accelerate indefinitely.
        // Vertical speed is untouched — gravity keeps doing its thing.
        Vector3 vel = rb.linearVelocity;
        Vector3 horizontalVel = new Vector3(vel.x, 0f, vel.z);
        if (horizontalVel.magnitude > maxHomingSpeed)
        {
            horizontalVel = horizontalVel.normalized * maxHomingSpeed;
            rb.linearVelocity = new Vector3(horizontalVel.x, vel.y, horizontalVel.z);
        }
    }

    void Update()
    {
        // Despawn when below kill height or after max lifetime
        if (transform.position.y < killHeight || Time.time - spawnTime > maxLifetime)
            Destroy(gameObject);
    }
}