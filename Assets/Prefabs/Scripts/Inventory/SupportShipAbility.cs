using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The RACER's half of the Support Ship: press <b>L3 + Y together</b> to summon or dismiss the escort
/// plane, provided a "Support Ship" is held. Summoning and dismissing are FREE — the item is only
/// spent when the ship is DOWNED. That is what makes it worth dismissing the ship whenever no teammate
/// is flying it: an unattended ship will eventually fly into something and cost the racer a copy.
///
/// The ship object itself is authored as an (inactive) child of the player-car prefab, named
/// <see cref="ShipChildName"/>. That child is only ever used as a TEMPLATE: summoning CLONES it and
/// cuts the clone loose from the car, so the wreck can be destroyed without permanently stripping the
/// prefab of its ship. Cutting it loose is also what lets it fly with lag — a rigidly parented child
/// would be welded to the car's rotation and could not trail it like a camera.
///
/// The chord matters: <b>L3 alone</b> already summons the Shield and breaks free of a grapple, so both
/// of those now ignore an L3 press made while Y is held (see ShieldAbility / GrappleHook). Y alone is
/// unbound while driving; RT+Y is the grapple reel, which needs the right trigger and so can't collide
/// with this.
///
/// Persistent + bootstrapped on the PlayerSystems object (like ShieldAbility and GrappleHook), so it
/// spans scenes and re-resolves the template on a freshly spawned car. Multiplayer: summon/dismiss is
/// POLLED off <see cref="IsActive"/> by <see cref="SupportShipReplicator"/> — level-triggered state
/// heals itself, so it needs no notification. Being DOWNED is an event rather than a state, so that
/// one is reported explicitly (the same shape as GrappleHook calling into GrappleReplicator).
/// </summary>
[DefaultExecutionOrder(1000)]
public class SupportShipAbility : MonoBehaviour
{
    public static SupportShipAbility Instance { get; private set; }

    /// <summary>Name of the ship object on the car prefab. Shared with RemoteCarManager (which hides it
    /// on puppets) and SupportShipReplicator (which clones it for remote players).</summary>
    public const string ShipChildName = "SupportShip";

    [Tooltip("Inventory item required to summon the ship. Consumed only when the ship is DESTROYED — " +
             "summoning and dismissing cost nothing.")]
    public string shipItem = "Support Ship";
    [Tooltip("Layer applied to the summoned ship and all its children. Its collision matrix is what " +
             "decides what can down the ship — see SupportShip. Blank = keep the prefab's layer.")]
    public string shipLayerName = "SupportShip";

    /// <summary>True while this player's ship is out. Read by SupportShipReplicator to broadcast it,
    /// and by PilotControlCenter to list flyable ships.</summary>
    public bool IsActive => ship != null && !ship.IsRagdolling;

    /// <summary>The live ship, or null. The hub pilot's camera and the replicator both drive this.</summary>
    public SupportShip Ship => ship;

    private GameObject carGO;
    private Transform template;      // the authored (inactive) child on the car — never flown itself
    private SupportShip ship;        // the live clone, cut loose from the car
    private GameObject warnedCar;    // so the "no ship child" warning fires once per car, not per frame
    private bool warnedMissingLayer;

    // 3D looping engine sound on its own object, moved to the ship each frame while it's out.
    // Tuned entirely from AudioLibrary.supportShipAudio3D (shared with the one-shots).
    private AudioSource loopSource;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Update()
    {
        EnsureTemplate();

        // Keep the engine loop riding the ship so its 3D falloff tracks where the ship actually is,
        // not where the car is — the pilot can slide it a long way off to the side.
        if (ship != null && loopSource != null && loopSource.isPlaying)
            loopSource.transform.position = ship.transform.position;

        if (MenuState.AnyOpen) return;

        var gp = Gamepad.current;
        if (gp == null) return;

        // Either half of the chord can land first, so accept both orderings — otherwise the toggle
        // would only fire when the player happened to press L3 last.
        bool chord = (gp.leftStickButton.wasPressedThisFrame && gp.buttonNorth.isPressed)
                  || (gp.buttonNorth.wasPressedThisFrame && gp.leftStickButton.isPressed);
        if (chord) Toggle();
    }

    // -------------------------------------------------------
    //  Summon / dismiss
    // -------------------------------------------------------

    void Toggle()
    {
        if (IsActive) Dismiss();
        else Summon();
    }

    /// <summary>Clones the template off the car and sets it flying. Ignored (with a log) when the
    /// player owns no Support Ship or the car prefab was never given one.</summary>
    public void Summon()
    {
        if (IsActive) return;
        if (template == null) { EnsureTemplate(); if (template == null) return; }

        var inv = PlayerInventory.Instance;
        if (inv == null || inv.GetCount(shipItem) <= 0)
        {
            Debug.Log($"[SupportShip] No '{shipItem}' held — nothing to summon.");
            return;
        }

        ship = BuildShip(template, carGO.transform, shipLayerName, ref warnedMissingLayer);
        if (ship == null) return;

        // The ship is CONSUMED BY DEATH, not by use. Deducting here instead would make dismissing the
        // ship a pure loss and remove any reason to ever put it away.
        ship.onCrashed += OnShipCrashed;

        AudioManager.PlaySupportShipActivate(ship.transform.position);
        StartLoop(ship.transform.position);
        Debug.Log($"[SupportShip] Summoned ({inv.GetCount(shipItem)} held).");
    }

    /// <summary>Puts the ship away with no cost. The clone is simply destroyed — the template on the
    /// car is untouched, so it can be summoned again immediately.</summary>
    public void Dismiss()
    {
        if (ship == null) return;

        Vector3 at = ship.transform.position;
        ship.onCrashed -= OnShipCrashed;
        Destroy(ship.gameObject);
        ship = null;

        StopLoop();
        AudioManager.PlaySupportShipDeactivate(at);
        Debug.Log("[SupportShip] Dismissed.");
    }

    /// <summary>Downed — by scenery, an obstacle, or an enemy's fire. THIS is where the item is spent.
    /// The wreck destroys itself after its ragdoll, so there's nothing to clean up here beyond letting
    /// go of it.</summary>
    void OnShipCrashed(SupportShip crashed)
    {
        if (crashed != null) crashed.onCrashed -= OnShipCrashed;
        ship = null;
        StopLoop();

        var inv = PlayerInventory.Instance;
        if (inv != null && inv.Consume(shipItem, 1))
            Debug.Log($"[SupportShip] Destroyed — one '{shipItem}' spent ({inv.GetCount(shipItem)} left).");
        else
            Debug.Log("[SupportShip] Destroyed.");

        // Tell everyone else so their copy TUMBLES rather than just vanishing on the next state
        // heartbeat. Harmless (and a no-op) when the wreck was called by the server in the first place.
        if (MultiplayerWorld.IsMultiplayerGame)
            SupportShipReplicator.ReportDown(NetworkIdentity);
    }

    /// <summary>Our own client id, or 0 outside a session. Only used to name our ship on the wire.</summary>
    static ulong NetworkIdentity =>
        Unity.Netcode.NetworkManager.Singleton != null
            ? Unity.Netcode.NetworkManager.Singleton.LocalClientId : 0;

    /// <summary>Clones a ship template and cuts it loose, ready to fly. Shared with
    /// <see cref="SupportShipReplicator"/>, which does exactly the same thing with a remote player's
    /// puppet. Everything is taken from the template's WORLD pose rather than its local one, so it
    /// doesn't matter how deeply the ship is nested on the car prefab (directly under the root, or
    /// tucked inside an accessories group) — the clone lands exactly where the authored one sat.</summary>
    internal static SupportShip BuildShip(Transform template, Transform car, string layerName,
                                          ref bool warnedMissingLayer)
    {
        if (template == null || car == null) return null;

        var go = Instantiate(template.gameObject);
        go.name = "SupportShip_Live";
        go.transform.SetPositionAndRotation(template.position, template.rotation);
        go.transform.localScale = template.lossyScale;   // root object, so local scale IS world scale
        go.SetActive(true);
        DontDestroyOnLoad(go);   // follows its car across the hub/track areas

        ApplyLayer(go, layerName, ref warnedMissingLayer);

        var ship = go.GetComponent<SupportShip>();
        if (ship == null) ship = go.AddComponent<SupportShip>();
        // Where the designer parked the template IS the resting offset. Read it back through the car's
        // frame (so nesting depth is irrelevant) and re-scale to world units, since the ship applies it
        // as `car.position + lookRotation * offset` with the car's scale already out of the picture.
        ship.defaultOffset = Vector3.Scale(car.InverseTransformPoint(template.position), car.lossyScale);
        ship.Attach(car);
        return ship;
    }

    static void ApplyLayer(GameObject go, string layerName, ref bool warned)
    {
        if (string.IsNullOrEmpty(layerName)) return;
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
        {
            if (!warned)
            {
                warned = true;
                Debug.LogWarning($"[SupportShip] Layer '{layerName}' not found in Tags and Layers — the " +
                                 "ship keeps the prefab's layer, so its collision matrix (what can down " +
                                 "it, and that it must NOT collide with the track) is not in effect.");
            }
            return;
        }
        SetLayerRecursively(go, layer);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // -------------------------------------------------------
    //  Engine loop (3D, follows the ship while it's out)
    // -------------------------------------------------------

    void StartLoop(Vector3 at)
    {
        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (lib == null || lib.supportShipLoop == null) return;

        EnsureLoopSource();
        loopSource.clip = lib.supportShipLoop;
        loopSource.transform.position = at;
        lib.supportShipAudio3D.ApplyTo(loopSource,
                                       AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f);
        loopSource.Play();
    }

    void StopLoop()
    {
        if (loopSource != null) loopSource.Stop();
    }

    void EnsureLoopSource()
    {
        if (loopSource != null) return;

        var go = new GameObject("SupportShipLoopAudio");
        DontDestroyOnLoad(go);
        loopSource = go.AddComponent<AudioSource>();
        loopSource.loop = true;
        loopSource.playOnAwake = false;
        // Spatial values are set per-summon from supportShipAudio3D (see StartLoop).
    }

    // -------------------------------------------------------
    //  Template resolution
    // -------------------------------------------------------

    /// <summary>(Re)finds the ship template on the current player car, and makes sure it stays hidden —
    /// it's authored ACTIVE on the prefab (it's what the designer positions by eye), so it has to be
    /// switched off or every car would drive around with a permanently welded ship. Re-runs when the
    /// car is replaced (scene load / car swap), where the old reference goes null.</summary>
    void EnsureTemplate()
    {
        GameObject car = PlayerRegistry.LocalCar;

        // Car gone (menu scene, car swap, teardown): the ship can't outlive it.
        if (car == null)
        {
            if (ship != null) { Destroy(ship.gameObject); ship = null; StopLoop(); }
            carGO = null;
            template = null;
            return;
        }

        if (car == carGO && template != null) return;

        carGO = car;
        template = FindChildByName(car.transform, ShipChildName);

        if (template == null)
        {
            if (warnedCar != car)
            {
                warnedCar = car;
                Debug.LogWarning($"[SupportShip] Car '{car.name}' has no child named '{ShipChildName}' — " +
                                 "L3+Y cannot summon a ship on this car. Add the SupportShip prefab as a " +
                                 "child of THIS car prefab, positioned where the ship should rest.");
            }
            return;
        }

        template.gameObject.SetActive(false);   // template only — the flying ship is always a clone
    }

    /// <summary>Depth-first search for a child by name (case-insensitive), including inactive objects —
    /// the template is kept inactive, so an active-only search would never find it.</summary>
    internal static Transform FindChildByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name)) return null;
        foreach (Transform child in root)
        {
            if (string.Equals(child.name, name, System.StringComparison.OrdinalIgnoreCase)) return child;
            var deeper = FindChildByName(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
