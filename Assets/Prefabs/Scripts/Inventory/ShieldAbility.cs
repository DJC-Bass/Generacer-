using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The SHIELD ability: press <b>L3</b> (left stick click) to summon the ellipsoid shield around the car
/// for <see cref="shieldDuration"/> seconds. Summoning CONSUMES one "Shield" from the inventory, so each
/// activation costs a shield and the player must craft another to use it again (Plasma → Shield at the
/// Upgrade Ramp; capped at 4 held).
///
/// The shield itself is a child object of the player-car prefab (named <see cref="shieldChildName"/>),
/// kept inactive until summoned — this controller just toggles it. That child must sit on the dedicated
/// <b>"Shield" layer</b>, which is what does the actual work:
///   • <see cref="DroneProjectile"/> treats ANY hit on that layer as blocked — the projectile dies with
///     no pop-up, no momentum loss (see its collision handler).
///   • Everything else the shield should or shouldn't touch (lightning / fans / boulders / other cars =
///     yes, the track and hub floor = no) is the Shield layer's COLLISION MATRIX, set in Project
///     Settings — deliberately not code, so it stays tunable without a rebuild.
///
/// Persistent + bootstrapped on the PlayerSystems object (like SDAbilityController), so it spans scenes
/// and re-finds the shield on a freshly spawned car. Multiplayer: the active state rides bit 5 of the
/// existing car-stream effect byte, so remote players SEE each other's shields — and, because projectile
/// hits are host-authoritative, the host's copy of a remote shield is what blocks their incoming fire.
/// </summary>
[DefaultExecutionOrder(1000)]
public class ShieldAbility : MonoBehaviour
{
    public static ShieldAbility Instance { get; private set; }

    [Tooltip("Inventory item consumed per activation.")]
    public string shieldItem = "Shield";
    [Tooltip("Seconds the shield stays up before vanishing. One activation = one shield spent.")]
    public float shieldDuration = 4f;
    [Tooltip("Name of the shield child object on the player-car prefab (the ellipsoid). Must be on the " +
             "'Shield' layer. Matched case-insensitively anywhere under the car root.")]
    public string shieldChildName = "Shield";

    /// <summary>True while the shield is up. Read by RemoteCarManager to replicate it to other players.</summary>
    public bool IsActive { get; private set; }

    private float activeUntil;
    private GameObject carGO;
    private GameObject shieldGO;
    private GameObject warnedCar;   // so the "no shield child" warning fires once per car, not per frame

    // 3D looping "shield up" sound on its own object, moved to the car each frame while active.
    // Tuned entirely from AudioLibrary.shieldAudio3D (shared with the activate/deactivate one-shots).
    private AudioSource shieldLoopSource;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Update()
    {
        EnsureShield();

        if (IsActive && Time.time >= activeUntil) Deactivate();

        // Keep the "while up" loop riding the car so its 3D falloff tracks the player.
        if (IsActive && shieldLoopSource != null && shieldLoopSource.isPlaying && carGO != null)
            shieldLoopSource.transform.position = carGO.transform.position;

        if (MenuState.AnyOpen) return;
        var gp = Gamepad.current;
        if (gp != null && gp.leftStickButton.wasPressedThisFrame) TryActivate();
    }

    /// <summary>Summons the shield if the player owns one and none is already up. A press with an empty
    /// inventory (or mid-shield) is simply ignored.</summary>
    public void TryActivate()
    {
        if (IsActive) return;                       // already shielded — don't burn a second one
        if (shieldGO == null) { EnsureShield(); if (shieldGO == null) return; }

        var inv = PlayerInventory.Instance;
        if (inv == null || !inv.Consume(shieldItem, 1)) return;   // none held — ignore the press

        IsActive = true;
        activeUntil = Time.time + Mathf.Max(0.1f, shieldDuration);
        shieldGO.SetActive(true);

        Vector3 at = carGO != null ? carGO.transform.position : transform.position;
        AudioManager.PlayShieldActivate(at);
        StartShieldLoop(at);

        Debug.Log($"[Shield] Summoned for {shieldDuration:0.#}s ({inv.GetCount(shieldItem)} left).");
    }

    void Deactivate()
    {
        IsActive = false;
        if (shieldGO != null) shieldGO.SetActive(false);
        StopShieldLoop();
        if (carGO != null) AudioManager.PlayShieldDeactivate(carGO.transform.position);
    }

    // -------------------------------------------------------
    //  "Shield up" loop (3D, follows the car while active)
    // -------------------------------------------------------

    void StartShieldLoop(Vector3 at)
    {
        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (lib == null || lib.shieldActiveLoop == null) return;

        EnsureShieldLoopSource();
        shieldLoopSource.clip = lib.shieldActiveLoop;
        shieldLoopSource.transform.position = at;
        // All 3D tuning (blend / volume / min+max distance / rolloff / doppler) comes from the library
        // block, folded in with the global SFX level — one place to tune all three shield sounds.
        lib.shieldAudio3D.ApplyTo(shieldLoopSource,
                                  AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f);
        shieldLoopSource.Play();
    }

    void StopShieldLoop()
    {
        if (shieldLoopSource != null) shieldLoopSource.Stop();
    }

    void EnsureShieldLoopSource()
    {
        if (shieldLoopSource != null) return;

        var go = new GameObject("ShieldLoopAudio");
        DontDestroyOnLoad(go);
        shieldLoopSource = go.AddComponent<AudioSource>();
        shieldLoopSource.loop = true;
        shieldLoopSource.playOnAwake = false;
        // Spatial values are set per-activation from shieldAudio3D (see StartShieldLoop).
    }

    /// <summary>(Re)finds the shield child on the current player car. Cheap once resolved; re-runs when
    /// the car is replaced (scene load / car swap), where the old reference goes null.</summary>
    void EnsureShield()
    {
        if (carGO != null && shieldGO != null) return;

        carGO = PlayerRegistry.LocalCar;
        if (carGO == null) { shieldGO = null; return; }

        Transform found = FindChildByName(carGO.transform, shieldChildName);
        shieldGO = found != null ? found.gameObject : null;

        if (shieldGO == null)
        {
            IsActive = false;
            // Say so LOUDLY, once per car. A car prefab that was never given the Shield child makes L3
            // silently do nothing, which is indistinguishable from a broken ability — the shield is
            // per-CAR wiring, so it has to be added to every car prefab, not just one.
            if (warnedCar != carGO)
            {
                warnedCar = carGO;
                Debug.LogWarning($"[Shield] Car '{carGO.name}' has no child named '{shieldChildName}' — " +
                                 "L3 cannot summon a shield on this car. Add the Shield prefab as a " +
                                 "child of THIS car prefab (on the Shield layer, left inactive).");
            }
            return;
        }

        // A fresh car starts unshielded — including the case where the prefab was authored with the
        // shield left visible. Re-show it only if an activation is still running.
        shieldGO.SetActive(IsActive);
    }

    /// <summary>Depth-first search for a child by name (case-insensitive), including inactive objects —
    /// the shield lives inactive on the prefab, so an active-only search would never find it.</summary>
    static Transform FindChildByName(Transform root, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (Transform child in root)
        {
            if (string.Equals(child.name, name, System.StringComparison.OrdinalIgnoreCase)) return child;
            var deeper = FindChildByName(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
