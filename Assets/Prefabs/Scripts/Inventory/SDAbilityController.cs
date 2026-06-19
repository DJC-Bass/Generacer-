using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Activates the equipped SD's ability on D-pad Up (toggle). While active, the player car's
/// colliders ignore one obstacle layer — Fire SD → "Boulder", Wind SD → "Fan", Lightning SD →
/// "Lightning" — using per-collider <see cref="Collider.excludeLayers"/>, so only the player
/// passes through (the global collision matrix is untouched).
///
/// Costs credits while active (default 50/sec). Pressing Up with no equipped SD or no credits
/// does nothing. Running out of credits while active auto-deactivates it. Switching the equipped
/// SD also cancels an active ability. Persistent + bootstrapped on the PlayerSystems object, so
/// it spans scenes; it re-applies the exclusion to a freshly-spawned car each scene.
/// </summary>
[DefaultExecutionOrder(1000)]
public class SDAbilityController : MonoBehaviour
{
    public static SDAbilityController Instance { get; private set; }

    [System.Serializable]
    public struct SDAbility { public string sdItemName; public string blockedLayer; }

    // Equipped SD -> the obstacle layer the car ignores while that SD's ability is active.
    private SDAbility[] abilities =
    {
        new SDAbility { sdItemName = "Fire SD",      blockedLayer = "Boulder" },
        new SDAbility { sdItemName = "Wind SD",      blockedLayer = "Fan" },
        new SDAbility { sdItemName = "Lightning SD", blockedLayer = "Lightning" },
    };

    [Tooltip("Credits drained per second while an SD ability is active.")]
    public float creditsPerSecond = 50f;
    [Tooltip("Tag on the player car whose colliders get the layer exclusion.")]
    public string playerTag = "Player";

    public bool IsActive { get; private set; }
    public string ActiveSD { get; private set; } = "";

    private int activeLayer = -1;          // layer currently excluded on the car (-1 = none)
    private float drainAccumulator;
    private GameObject carGO;
    private Collider[] carColliders;

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
        // since the active exclusion was tied to the old SD's layer.
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

        int layer = ResolveLayer(sd);
        if (layer < 0) return;                  // no mapping / layer missing — ignore

        activeLayer = layer;
        ActiveSD = sd;
        IsActive = true;
        drainAccumulator = 0f;
        carGO = null;                           // force a fresh find + apply
        EnsureCar();

        Debug.Log($"[SDAbility] {sd} active — car ignores layer '{LayerMask.LayerToName(layer)}'");
    }

    void Deactivate()
    {
        if (!IsActive) return;
        ApplyExclude(false);                    // restore collisions on the current car
        IsActive = false;
        ActiveSD = "";
        activeLayer = -1;
        carGO = null;
        carColliders = null;
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

    int ResolveLayer(string sd)
    {
        foreach (var a in abilities)
            if (a.sdItemName == sd)
            {
                int idx = LayerMask.NameToLayer(a.blockedLayer);
                if (idx < 0)
                    Debug.LogWarning($"[SDAbility] Layer '{a.blockedLayer}' not found in Tags and Layers.");
                return idx;
            }
        return -1;
    }

    /// <summary>(Re)finds the player car and applies the active exclusion to a new car instance.
    /// Cheap when the car reference is still valid — only searches when it's been lost.</summary>
    void EnsureCar()
    {
        if (carGO != null) return;
        carGO = GameObject.FindWithTag(playerTag);
        carColliders = carGO != null ? carGO.GetComponentsInChildren<Collider>(true) : null;
        if (IsActive) ApplyExclude(true);
    }

    void ApplyExclude(bool exclude)
    {
        if (activeLayer < 0 || carColliders == null) return;
        int mask = 1 << activeLayer;
        foreach (var col in carColliders)
        {
            if (col == null || col is WheelCollider) continue;   // wheels are disabled in the raycast controller
            if (exclude) col.excludeLayers |= mask;
            else col.excludeLayers &= ~mask;
        }
    }
}
