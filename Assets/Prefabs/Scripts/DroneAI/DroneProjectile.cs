using UnityEngine;
using UnityEngine.SceneManagement;

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
    [Tooltip("During the hub Drone ending, a hit sends the player to THIS scene (game over) instead " +
             "of the normal pop-up. Normal track gameplay always uses the pop-up.")]
    public string mainMenuSceneName = "MainMenu";

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
        bool hitPlayer = false;
        Transform t = collision.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                HitPlayer(t.gameObject);
                hitPlayer = true;
                break;
            }
            t = t.parent;
        }

        // Impact SFX (3D): a distinct sound for striking the player vs the environment.
        if (hitPlayer) AudioManager.PlayProjectileHitPlayer(transform.position);
        else AudioManager.PlayProjectileHitEnvironment(transform.position);

        // Despawn on any collision regardless of what was hit
        Destroy(gameObject);
    }

    void HitPlayer(GameObject player)
    {
        // During the hub Drone ending a hit is game over: send the player back to the main menu
        // instead of the usual pop-up. (Normal track gameplay always gets the pop-up.)
        if (IsDroneEndingHub())
        {
            ReturnToMainMenu();
            return;
        }

        var prb = player.GetComponent<Rigidbody>();
        if (prb == null) return;

        // Halt forward momentum: zero out the horizontal velocity entirely so
        // the car loses all its speed, then pop it up. The car keeps no forward
        // motion until it lands and the player accelerates again.
        Vector3 vel = prb.linearVelocity;
        vel.x = 0f;
        vel.z = 0f;
        prb.linearVelocity = vel;

        // Pop up � same feel as the lightning strike hit
        prb.AddForce(Vector3.up * popUpForce, ForceMode.VelocityChange);
    }

    /// <summary>True only when the game-over Drone ending is active AND we're in the hub scene — the
    /// one situation where a projectile hit ends the game. The scene check keeps a track hit (even on
    /// the frame the ending flag flips during a failure transition) on the normal pop-up.</summary>
    bool IsDroneEndingHub()
    {
        var gm = GameLoopManager.Instance;
        return gm != null
            && gm.DroneEndingActive
            && SceneManager.GetActiveScene().name == gm.hubSceneName;
    }

    /// <summary>Game over during the Drone ending: tear down the run (so the next game starts fresh,
    /// like the QUIT button) and load the main menu.</summary>
    void ReturnToMainMenu()
    {
        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();

        if (!string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            SceneManager.LoadScene(mainMenuSceneName);
        else
            Debug.LogWarning($"[DroneProjectile] Main menu scene '{mainMenuSceneName}' isn't in Build Settings.");
    }
}
