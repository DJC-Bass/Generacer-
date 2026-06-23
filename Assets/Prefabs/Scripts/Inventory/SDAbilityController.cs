using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Activates the equipped SD's ability on D-pad Up (toggle). Each SD has its own effect:
///   • Fire SD      — multiplies the player car's MASS (default 100x), so it barrels straight
///                    through obstacles like a battering ram instead of being knocked around.
///   • Wind SD      — the car collides ONLY with the "Track" and "Default" layers (Default is
///                    where the key triggers live), phasing through every obstacle (boulders,
///                    fans, lightning, drones) while still driving the road and hitting triggers.
///   • Lightning SD — the car's colliders ignore the "Lightning" layer.
///
/// Layer effects use per-collider <see cref="Collider.excludeLayers"/> so only the player passes
/// through (the global collision matrix is untouched); the mass effect scales the car's
/// <see cref="Rigidbody.mass"/> and restores it on deactivate.
///
/// Costs credits while active (default 50/sec). Pressing Up with no equipped SD or no credits
/// does nothing. Running out of credits while active auto-deactivates it. Switching the equipped
/// SD also cancels an active ability. Persistent + bootstrapped on the PlayerSystems object, so
/// it spans scenes; it re-applies the active effect to a freshly-spawned car each scene.
/// </summary>
[DefaultExecutionOrder(1000)]
public class SDAbilityController : MonoBehaviour
{
    public static SDAbilityController Instance { get; private set; }

    /// <summary>What an SD does while active.</summary>
    public enum SDEffectType
    {
        LayerExclude,   // car ignores collisions with ONE layer (layerName)
        CollideOnly,    // car ignores every layer EXCEPT the ones in collideLayers
        MassMultiply,   // car's mass is scaled by massMultiplier
    }

    [System.Serializable]
    public struct SDAbility
    {
        public string sdItemName;
        public SDEffectType effect;
        public string layerName;        // LayerExclude: the single layer to ignore.
        public string[] collideLayers;  // CollideOnly: the layers the car STAYS solid against (all others ignored).
        public float massMultiplier;    // MassMultiply.
    }

    // Equipped SD -> the effect applied to the player car while that SD's ability is active.
    private SDAbility[] abilities =
    {
        new SDAbility { sdItemName = "Fire SD",      effect = SDEffectType.MassMultiply, massMultiplier = 100f },
        new SDAbility { sdItemName = "Wind SD",      effect = SDEffectType.CollideOnly,  collideLayers = new[] { "Track", "Default" } },
        new SDAbility { sdItemName = "Lightning SD", effect = SDEffectType.LayerExclude, layerName = "Lightning" },
    };

    [Tooltip("Credits drained per second while an SD ability is active.")]
    public float creditsPerSecond = 50f;
    [Tooltip("Tag on the player car the active effect is applied to.")]
    public string playerTag = "Player";

    public bool IsActive { get; private set; }
    public string ActiveSD { get; private set; } = "";

    private SDEffectType activeEffect;
    private int activeExcludeMask = 0;        // layers excluded on the car while active (0 = none)
    private float activeMassMultiplier = 1f;  // MassMultiply: factor applied to the car's mass
    private float baselineMass = -1f;         // MassMultiply: the car's real mass before scaling
    private float drainAccumulator;
    private GameObject carGO;
    private Collider[] carColliders;
    private Rigidbody carRb;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Update()
    {
        var gp = Gamepad.current;
        if (gp != null && gp.dpad.up.wasPressedThisFrame && !MenuState.AnyOpen)
        {
            if (IsActive) Deactivate();
            else TryActivate();
        }

        if (!IsActive) return;

        // Switching the equipped SD (or losing it on a failure reset) cancels the ability,
        // since the active effect was tied to the old SD.
        var inv = PlayerInventory.Instance;
        if (inv == null || inv.EquippedSD != ActiveSD) { Deactivate(); return; }

        EnsureCar();        // re-acquire + re-apply to a freshly spawned car after a scene load
        DrainCredits();     // may auto-deactivate when credits hit zero
    }

    void TryActivate()
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) return;

        string sd = inv.EquippedSD;
        if (string.IsNullOrEmpty(sd)) return;   // no SD equipped — ignore the press
        if (inv.Credits <= 0) return;           // no credits — ignore the press

        if (!ResolveAbility(sd, out SDAbility ability)) return;   // no mapping — ignore

        // Resolve the excludeLayers mask the effect needs. Bail (ignore the press) if a named
        // layer is missing, so we never silently apply a wrong/empty exclusion.
        if (!ResolveExcludeMask(ability, out activeExcludeMask)) return;

        activeEffect = ability.effect;
        activeMassMultiplier = ability.massMultiplier;
        ActiveSD = sd;
        IsActive = true;
        drainAccumulator = 0f;
        carGO = null;                           // force a fresh find + apply
        EnsureCar();

        Debug.Log($"[SDAbility] {sd} active ({ability.effect}).");
    }

    void Deactivate()
    {
        if (!IsActive) return;
        ApplyEffect(false);                     // restore the current car (collisions / mass)
        IsActive = false;
        ActiveSD = "";
        activeExcludeMask = 0;
        activeMassMultiplier = 1f;
        baselineMass = -1f;
        carGO = null;
        carColliders = null;
        carRb = null;
        Debug.Log("[SDAbility] Deactivated");
    }

    void DrainCredits()
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) { Deactivate(); return; }

        drainAccumulator += creditsPerSecond * Time.deltaTime;
        int whole = Mathf.FloorToInt(drainAccumulator);
        if (whole <= 0) return;

        drainAccumulator -= whole;
        int spend = Mathf.Min(whole, inv.Credits);
        if (spend > 0) inv.AddCredits(-spend);
        if (inv.Credits <= 0) Deactivate();     // out of credits — auto-off
    }

    bool ResolveAbility(string sd, out SDAbility ability)
    {
        foreach (var a in abilities)
            if (a.sdItemName == sd) { ability = a; return true; }
        ability = default;
        return false;
    }

    /// <summary>
    /// Builds the excludeLayers mask for an ability. LayerExclude excludes its one layer;
    /// CollideOnly excludes everything EXCEPT its collideLayers (so the car stays solid only
    /// against those); other effects exclude nothing. Returns false if a named layer is missing.
    /// </summary>
    bool ResolveExcludeMask(SDAbility ability, out int excludeMask)
    {
        excludeMask = 0;

        switch (ability.effect)
        {
            case SDEffectType.LayerExclude:
            {
                int layer = LayerMask.NameToLayer(ability.layerName);
                if (layer < 0) { WarnMissingLayer(ability.layerName); return false; }
                excludeMask = 1 << layer;
                return true;
            }
            case SDEffectType.CollideOnly:
            {
                int keepMask = 0;
                if (ability.collideLayers != null)
                    foreach (var name in ability.collideLayers)
                    {
                        int layer = LayerMask.NameToLayer(name);
                        if (layer < 0) { WarnMissingLayer(name); return false; }
                        keepMask |= 1 << layer;
                    }
                if (keepMask == 0) return false;   // nothing kept would exclude everything — refuse
                excludeMask = ~keepMask;           // exclude all layers except the kept ones
                return true;
            }
            default:
                return true;                       // MassMultiply etc. — no layer exclusion
        }
    }

    static void WarnMissingLayer(string name) =>
        Debug.LogWarning($"[SDAbility] Layer '{name}' not found in Tags and Layers.");

    /// <summary>(Re)finds the player car and applies the active effect to a new car instance.
    /// Cheap when the car reference is still valid — only searches when it's been lost (e.g.
    /// the car was destroyed and respawned on a scene load), at which point the per-car mass
    /// baseline is reset so the fresh car's real mass is recaptured.</summary>
    void EnsureCar()
    {
        if (carGO != null) return;

        carGO = GameObject.FindWithTag(playerTag);
        carColliders = carGO != null ? carGO.GetComponentsInChildren<Collider>(true) : null;
        carRb = carGO != null ? carGO.GetComponent<Rigidbody>() : null;
        baselineMass = -1f;                     // fresh car — recapture its mass when applied

        if (IsActive) ApplyEffect(true);
    }

    /// <summary>Turns the active SD's effect on or off on the current car.</summary>
    void ApplyEffect(bool on)
    {
        switch (activeEffect)
        {
            case SDEffectType.LayerExclude:
            case SDEffectType.CollideOnly:  ApplyExclude(on); break;
            case SDEffectType.MassMultiply: ApplyMass(on);    break;
        }
    }

    void ApplyExclude(bool exclude)
    {
        if (activeExcludeMask == 0 || carColliders == null) return;

        int mask = activeExcludeMask;   // precomputed at activation (see ResolveExcludeMask)
        foreach (var col in carColliders)
        {
            if (col == null || col is WheelCollider) continue;   // wheels are disabled in the raycast controller
            if (exclude) col.excludeLayers |= mask;
            else col.excludeLayers &= ~mask;
        }
    }

    void ApplyMass(bool on)
    {
        if (carRb == null) return;

        if (on)
        {
            // Capture the car's real mass once (EnsureCar resets the baseline for a fresh car,
            // and never re-applies to a car that's still valid), then scale it.
            if (baselineMass < 0f) baselineMass = carRb.mass;
            carRb.mass = baselineMass * activeMassMultiplier;
        }
        else
        {
            if (baselineMass > 0f) carRb.mass = baselineMass;   // restore the real mass
            baselineMass = -1f;
        }
    }
}
