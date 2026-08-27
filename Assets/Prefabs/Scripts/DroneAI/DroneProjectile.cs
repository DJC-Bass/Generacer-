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

    [Header("Audio (3D)")]
    [Tooltip("3D playback settings — Min/Max Distance, rolloff, spatial blend, volume — shared by all " +
             "three DronePissBall sounds: the drone firing it, and its environment / player impacts.")]
    public Spatial3DSettings audio3D = new Spatial3DSettings();

    [Header("Hit Invulnerability (anti-stunlock)")]
    [Tooltip("Seconds of immunity to ALL drone projectiles after one lands. Shared across every " +
             "projectile from every drone car and plane, so a pack can't chain-pop the player: the " +
             "first hit lands, and everything else is ignored until the window closes.")]
    public float hitInvulnerabilitySeconds = 2f;

    private Rigidbody rb;
    private float spawnTime;
    private bool consumed;

    // ---- Local player's invulnerability window (STATIC = shared by every projectile in the scene,
    //      which is the whole point: the window belongs to the PLAYER, not to any one projectile).
    //      Each machine owns its own car's window — the host tracks its own, and a client applies the
    //      same test to host-reported hits — so nothing has to be replicated. ----
    static float invulnerableUntil = -999f;
    // Instances publish their inspector value here so the STATIC remote-hit path (which has no
    // instance) uses the same tuned duration instead of a second hardcoded number.
    static float lastKnownInvulnSeconds = 2f;

    /// <summary>True while the local player is still immune from a recent projectile hit.</summary>
    public static bool PlayerInvulnerable => Time.time < invulnerableUntil;

    /// <summary>Opens (or extends) the local player's immunity window.</summary>
    static void BeginInvulnerability(float seconds)
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
        rb.useGravity = false;          // travels in a straight line, not an arc
        spawnTime = Time.time;
        lastKnownInvulnSeconds = hitInvulnerabilitySeconds;   // keep the static path in sync with the prefab
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

    [Header("Shield")]
    [Tooltip("Layer of the player's summoned Shield. A hit on this layer is BLOCKED: the projectile " +
             "dies with no pop-up and no momentum loss. Works for the local car and for remote puppets " +
             "alike (the shield's active state replicates), which matters because hits are " +
             "host-authoritative in multiplayer.")]
    public string shieldLayerName = "Shield";

    void OnCollisionEnter(Collision collision)
    {
        if (consumed) return;
        consumed = true;

        // SHIELD CHECK FIRST. The shield is a CHILD of the car root, so the player-tag walk below would
        // otherwise find the tag on its parent and pop the player anyway — the whole point of the
        // shield is that it eats the shot before that.
        if (IsShield(collision.collider))
        {
            AudioManager.PlayProjectileHitEnvironment(transform.position, audio3D);
            NpcReplicator.ReportNpcSound(gameObject, transform.position,
                                         NpcReplicator.NpcSound.HitEnvironment);
            Destroy(gameObject);
            return;
        }

        // Check if we hit a player (walk up hierarchy for sub-colliders). On the multiplayer host
        // (the only place projectiles simulate) a REMOTE player's solid puppet counts too — the hit
        // is routed to the victim's machine, where the pop-up lands on their real car.
        bool hitPlayer = false;
        ulong victimClientId = ulong.MaxValue;
        Transform t = collision.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag))
            {
                // ABSORBED if the shield is up (a backstop behind the shield COLLIDER, which a big or
                // fast projectile can tunnel past) or if the anti-stunlock window from a previous hit
                // is still open. Either way the projectile still dies, but nothing is applied —
                // and hitPlayer stays false, so it reads as a dull environment impact rather than a
                // player hit.
                if (!PlayerInvulnerable && !ShieldAbility.LocalShieldUp)
                {
                    HitPlayer(t.gameObject);
                    BeginInvulnerability(hitInvulnerabilitySeconds);
                    hitPlayer = true;
                }
                break;
            }
            if (t.CompareTag("RemotePlayer"))
            {
                if (MultiplayerWorld.TryGetCarOwner(t, out ulong clientId, out bool isLocal) && !isLocal)
                {
                    NpcReplicator.SendHitToClient(clientId);
                    victimClientId = clientId;   // they play their OWN impact — don't send them a second
                }
                hitPlayer = true;
                break;
            }
            t = t.parent;
        }

        // Impact SFX (3D): a distinct sound for striking the player vs the environment.
        if (hitPlayer) AudioManager.PlayProjectileHitPlayer(transform.position, audio3D);
        else AudioManager.PlayProjectileHitEnvironment(transform.position, audio3D);

        // Only the HOST simulates projectiles, so this is the only machine that reaches here — relay it
        // or the impact is silent everywhere else. The VICTIM is excluded: GNRC_NPC_HIT already makes
        // them play it locally, instantly, and hearing it twice is an audible double-tap.
        NpcReplicator.ReportNpcSound(gameObject, transform.position,
                                     hitPlayer ? NpcReplicator.NpcSound.HitPlayer
                                               : NpcReplicator.NpcSound.HitEnvironment,
                                     victimClientId);

        // Despawn on any collision regardless of what was hit
        Destroy(gameObject);
    }

    /// <summary>True when the struck collider is a summoned player shield (matched by LAYER, so it works
    /// on remote puppets too — their shield's script is stripped, but its layer survives).</summary>
    bool IsShield(Collider hit)
    {
        if (hit == null || string.IsNullOrEmpty(shieldLayerName)) return false;
        int layer = LayerMask.NameToLayer(shieldLayerName);
        return layer >= 0 && hit.gameObject.layer == layer;
    }

    /// <summary>Applies a host-reported projectile hit to THIS machine's own car (the host detected
    /// the contact against our puppet). Mirrors <see cref="HitPlayer"/> with the default tuning:
    /// pop-up + momentum halt in normal play, the game-over exit during the hub Drone ending.</summary>
    public static void ApplyRemoteHitToLocalPlayer()
    {
        var car = PlayerRegistry.LocalCar;
        if (car == null) return;

        // ANTI-STUNLOCK, victim side. The HOST can't know our invulnerability window, so it reports
        // every contact against our puppet and WE decide — the same test the local path uses, which is
        // what makes the window cover projectiles from every drone and plane regardless of who saw the
        // hit. Dropping it here costs only a wasted message.
        // Our SHIELD is judged here too, for the same reason: the host saw the contact against our
        // PUPPET, whose shield is a replicated visual, so the authoritative answer is on this machine.
        if (PlayerInvulnerable || ShieldAbility.LocalShieldUp) return;
        BeginInvulnerability(lastKnownInvulnSeconds);

        AudioManager.PlayProjectileHitPlayer(car.transform.position, null);

        var gm = GameLoopManager.Instance;
        bool droneEndingHub = gm != null && gm.DroneEndingActive
            && SceneManager.GetActiveScene().name == gm.hubSceneName;
        if (droneEndingHub)
        {
            // Same multiplayer-aware game-over exit the local-hit path uses.
            if (MultiplayerWorld.IsMultiplayerGame)
                MultiplayerWorld.Instance.TeardownToLobby("HIT IN THE DRONE ENDING");
            return;
        }

        var prb = car.GetComponent<Rigidbody>();
        if (prb == null) return;
        Vector3 vel = prb.linearVelocity;
        vel.x = 0f;
        vel.z = 0f;
        prb.linearVelocity = vel;
        prb.AddForce(Vector3.up * 80f, ForceMode.VelocityChange);   // prefab-default pop-up force

        var controller = car.GetComponent<CarController>();
        if (controller != null) controller.ShortenSuspensionRayForPopUp();
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

        // Briefly shorten the car's suspension ray so the pop-up actually launches it (like a jump).
        var car = player.GetComponent<CarController>();
        if (car != null) car.ShortenSuspensionRayForPopUp();
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
    /// like the QUIT button) and leave — to the LOBBY in multiplayer, to the main menu solo.</summary>
    void ReturnToMainMenu()
    {
        // Multiplayer: back to the lobby ROOM, still in the session (2026-08-27). Losing a run should
        // not cost everyone their room — the same players, on the same teams, can go again immediately.
        // Deliberately no LeaveSessionAsync here: for a HOST that deletes the session for everyone, and
        // players are picked off one at a time, so the first casualty would end the game for the rest.
        // The world teardown still resets the run/inventory and clears the puppet statics, so the menu
        // does not inherit a track generated out at the multiplayer area offset.
        if (MultiplayerWorld.IsMultiplayerGame)
        {
            MultiplayerWorld.Instance.TeardownToLobby("HIT IN THE DRONE ENDING");
            return;
        }

        GameLoopManager.EndRun();
        if (PlayerInventory.Instance != null) PlayerInventory.Instance.ResetToStarting();

        if (!string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            SceneManager.LoadScene(mainMenuSceneName);
        else
            Debug.LogWarning($"[DroneProjectile] Main menu scene '{mainMenuSceneName}' isn't in Build Settings.");
    }
}
