using UnityEngine;

/// <summary>
/// A spherical projectile fired by drone cars. Travels forward at high speed,
/// despawns on any collision. If it hits the player, it pops them into the air
/// and halts their forward momentum.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DroneProjectile : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("Max seconds before auto-despawn if it never hits anything.")]
    public float maxLifetime = 4f;

    [Header("Player Hit Effect")]
    [Tooltip("Upward impulse applied to the player on hit (velocity change, m/s).")]
    public float popUpForce = 80f;
    [Tooltip("Tag identifying the player car.")]
    public string playerTag = "Player";

    private Rigidbody rb;
    private float spawnTime;
    private bool consumed;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;          // travels in a straight line, not an arc
        spawnTime = Time.time;
    }

    /// <summary>Launch the projectile in a direction at a given speed (m/s).</summary>
    public void Launch(Vector3 direction, float speedMs)
    {
        rb.linearVelocity = direction.normalized * speedMs;
    }

    void Update()
    {
        if (Time.time - spawnTime > maxLifetime)
            Destroy(gameObject);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (consumed) return;
        consumed = true;

        // Check if we hit the player (walk up hierarchy for sub-colliders)
        Transform t = collision.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                HitPlayer(t.gameObject);
                break;
            }
            t = t.parent;
        }

        // Despawn on any collision regardless of what was hit
        Destroy(gameObject);
    }

    void HitPlayer(GameObject player)
    {
        var prb = player.GetComponent<Rigidbody>();
        if (prb == null) return;

        // Halt forward momentum: zero out the horizontal velocity entirely so
        // the car loses all its speed, then pop it up. The car keeps no forward
        // motion until it lands and the player accelerates again.
        Vector3 vel = prb.linearVelocity;
        vel.x = 0f;
        vel.z = 0f;
        prb.linearVelocity = vel;

        // Pop up — same feel as the lightning strike hit
        prb.AddForce(Vector3.up * popUpForce, ForceMode.VelocityChange);
    }
}