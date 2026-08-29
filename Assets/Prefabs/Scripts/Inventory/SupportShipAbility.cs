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
             "summoning and dismissing cost nothing. MUST match the store row's granted name EXACTLY " +
             "(inventory keys are only trimmed, not space- or case-folded), so 'SupportShip' and " +
             "'Support Ship' are two different items as far as the inventory is concerned.")]
    public string shipItem = "Support Ship";
    [Tooltip("Layer applied to the summoned ship and all its children. Its collision matrix is what " +
             "decides what can down the ship — see SupportShip. Blank = keep the prefab's layer.")]
    public string shipLayerName = "SupportShip";
    [Tooltip("How long the local player's car may be MISSING before a summoned ship is put away. A " +
             "scene change leaves no car for a frame or two, and the ship is supposed to survive that " +
             "and follow the player from the hub to the track and back — so this only needs to be long " +
             "enough to outlast a load, and short enough that quitting to the menu doesn't strand a " +
             "ship in the menu scene.")]
    public float carLostGrace = 10f;

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
    private float carLostSince = -1f;   // when the local car went missing (-1 = it's here)

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
            // Named loudly, because the overwhelmingly likely cause is a spelling mismatch between the
            // store row and this field rather than an genuinely empty inventory — and the two look
            // identical from the player's side (they bought the thing, and L3+Y does nothing).
            Debug.LogWarning($"[SupportShip] No '{shipItem}' held — nothing to summon. If you DID buy one, " +
                             "check the store row grants this name character-for-character.");
            return;
        }

        ship = BuildShip(template, carGO.transform, shipLayerName, ref warnedMissingLayer);
        if (ship == null) return;

        // EXACTLY ONE machine may count hits on a ship, and it is the HOST. Our own copy therefore
        // stops detecting the moment we are a client: the host already has a copy of this ship (built
        // from our puppet), and it is the only machine with real projectiles.
        //
        // With the old one-hit pool a second detector was harmless — either one killing the ship gave
        // the same result. A pool of several makes it a bug: the host and the owner see overlapping but
        // DIFFERENT hit sets (the host alone sees projectiles; both see scenery), so a shared scrape
        // would spend a point on each machine's own private counter and the two could never agree on
        // when the ship should die. The host counts, the host declares it down, everyone obeys the
        // verdict — which is the path GNRC_SHIP_DOWN already provides.
        ship.ownerClientId = NetworkIdentity;   // it is OUR ship
        ship.detectCrashes = !MultiplayerWorld.IsClientOnly;

        // The ship is CONSUMED BY DEATH, not by use. Deducting here instead would make dismissing the
        // ship a pure loss and remove any reason to ever put it away.
        ship.onCrashed += OnShipCrashed;

        AudioManager.PlaySupportShipActivate(ship.transform.position);
        RemoteCarManager.ReportCarSound(RemoteCarManager.CarSound.ShipSummon, ship.transform.position);
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
        RemoteCarManager.ReportCarSound(RemoteCarManager.CarSound.ShipDismiss, at);
        Debug.Log("[SupportShip] Dismissed.");
    }

    /// <summary>Wrecks the ship on purpose, exactly as if it had been shot down: it ragdolls, plays the
    /// destroyed sound, paints red, spends the item and tells every other machine.
    ///
    /// Deliberately Crash() and not Dismiss(). Dismiss is the free "put it away" the owner gets for
    /// L3+Y; this is a LOSS, and routing it through the same path a real kill takes means every piece
    /// of that — the item, the replication, the wreck a watching pilot sees — is handled already.</summary>
    public void DestroyShip()
    {
        if (ship == null || ship.IsRagdolling) return;
        ship.Crash();   // onCrashed does the rest (see below)
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

        // So NpcReplicator can build puppets for the rounds this ship fires — the laser prefab is only
        // ever referenced from a car prefab's SupportShip component, so nothing else would find it.
        // (No-op for a remote ship, whose prefab reference arrives a moment later via CopyTuningFrom;
        // SupportShipReplicator registers it there.)
        NpcReplicator.RegisterPrefab(ship.laserPrefab);
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

    /// <summary>(Re)finds the ship template on the current player car, keeps it hidden, and — the part
    /// that matters — keeps a SUMMONED ship alive across scene changes and car swaps.
    ///
    /// A summoned ship is meant to persist for as long as the player leaves it out: hub → track → hub,
    /// all session, so a teammate can fly it the whole time without ever racing themselves. Two things
    /// get in the way of that and are handled here:
    ///  • During a scene load there is briefly NO local car at all (the old one is gone, PlayerCarSwapper
    ///    hasn't spawned the replacement). Tearing the ship down on sight of a null car would kill it on
    ///    every single transition, so a missing car is TOLERATED for <see cref="carLostGrace"/> seconds.
    ///    Only a car that stays gone — quitting to the menu, session teardown — actually ends the ship.
    ///  • When the car is REPLACED rather than teleported, the live ship is still escorting a destroyed
    ///    transform and would simply freeze in place. It gets re-bound to the new car instead.
    ///    (Multiplayer teleports the same car between areas, so this mostly bites single-player, where
    ///    PlayerCarSwapper spawns a fresh car in each gameplay scene.)</summary>
    void EnsureTemplate()
    {
        GameObject car = PlayerRegistry.LocalCar;

        if (car == null)
        {
            if (carLostSince < 0f) carLostSince = Time.unscaledTime;
            else if (ship != null && Time.unscaledTime - carLostSince > carLostGrace)
            {
                Debug.Log("[SupportShip] No player car for a while — putting the ship away.");
                Dismiss();
            }
            carGO = null;
            template = null;
            return;
        }

        carLostSince = -1f;
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

        // Hand a ship that's already out over to the new car.
        if (ship != null)
        {
            ship.defaultOffset = Vector3.Scale(car.transform.InverseTransformPoint(template.position),
                                               car.transform.lossyScale);
            ship.Attach(car.transform);
        }
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
