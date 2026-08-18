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
/// It deliberately ignores TWO things (see <see cref="IgnoreCollisionsWith"/>):
///  • the ship that fired it — a round spawning inside its own muzzle would die instantly;
///  • the racer that ship is escorting — the ship flies BEHIND its car (its resting offset is the chase
///    camera's), so "fire straight ahead" points directly at the car's back bumper. Without this every
///    single shot would detonate on the teammate you're supporting.
///
/// Multiplayer: spawned only by the HOST (routed there by SupportShipReplicator) and streamed to
/// clients as a collider-less visual via <see cref="NpcReplicator"/>, exactly like drone fire — so
/// contact is resolved once, on the machine that owns the drones.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SupportShipLaser : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("Max seconds before the round despawns if it never hits anything. At the default speed " +
             "this is also what sets its effective range.")]
    public float maxLifetime = 3f;

    [Header("Audio (3D)")]
    [Tooltip("3D playback settings for the impact sound. The FIRING sound is played by the ship and " +
             "tuned from AudioLibrary.supportShipAudio3D with the rest of the ship's audio.")]
    public Spatial3DSettings audio3D = new Spatial3DSettings();

    private Rigidbody rb;
    private float spawnTime;
    private bool consumed;

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

    /// <summary>Send it on its way at a given speed (m/s).</summary>
    public void Launch(Vector3 direction, float speedMs)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        rb.linearVelocity = direction.normalized * speedMs;
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

    /// <summary>Dies on the first contact. What it does TO that thing is whatever that thing does about
    /// being hit — a DronePlane, for instance, already ragdolls on any collision, so it falls out of the
    /// sky for free. Nothing is applied from this side yet.</summary>
    void OnCollisionEnter(Collision collision)
    {
        if (consumed) return;
        consumed = true;

        AudioManager.PlaySupportShipLaserHit(transform.position, audio3D);
        Destroy(gameObject);
    }
}
