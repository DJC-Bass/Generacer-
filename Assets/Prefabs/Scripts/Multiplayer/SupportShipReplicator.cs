using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Multiplayer layer for the Support Ship. This is the first entity in the project whose INPUT and
/// whose SUBJECT live on different machines — a hub player flies a ship that escorts someone else's
/// car — so it splits authority three ways, each along the line of who actually knows the answer:
///
///  • WHOSE SHIP IT IS — the racer. They broadcast "my ship is out / put away" (<c>GNRC_SHIP</c>),
///    level-triggered with a slow heartbeat exactly like the car-effect flags, so a dropped packet or
///    a late joiner self-heals on the next tick.
///  • WHERE IN ITS BOX IT SITS — the PILOT. They broadcast the stick offset (<c>GNRC_SHIP_AIM</c>) and
///    everyone else, the racer included, applies it. Routing it through the racer instead would put a
///    full round trip between the pilot's stick and their own screen, which is unflyable.
///  • WHETHER IT IS STILL ALIVE — the server. Any machine entitled to notice a crash REPORTS it and
///    the server hands down the verdict (<c>GNRC_SHIP_DOWN</c>), so the ship dies once, everywhere,
///    and exactly one Support Ship is deducted.
///
/// Nothing about the FLIGHT is streamed. Every machine runs its own <see cref="SupportShip"/> glued to
/// its own copy of the owner's car — the same "replicate the attachment, not the position" trick the
/// grappling hook uses, and for the same reason: a streamed world position on a fast-moving parent
/// arrives stale and reads as lag and teleporting, whereas an offset from a car everyone already
/// interpolates is rock solid on every screen.
///
/// Added to the MultiplayerWorld object at session begin, alongside RemoteCarManager / NpcReplicator.
/// </summary>
public class SupportShipReplicator : MonoBehaviour
{
    const string MsgShip = "GNRC_SHIP";         // owner  → all: {ownerId, active, ownerInTrack, repairs}
    const string MsgAim = "GNRC_SHIP_AIM";      // pilot  → all: {ownerId, offset(Vec3), look(Vec3)}
    const string MsgPilot = "GNRC_SHIP_PILOT";  // client → server (request) / server → all (verdict)
    const string MsgDown = "GNRC_SHIP_DOWN";    // any    → server (report)  / server → all (verdict)
    const string MsgFire = "GNRC_SHIP_FIRE";    // pilot  → server: fire owner X's guns, once, from
                                                //          {offset, look} AS OF THE PRESS (see RequestFire)
    const string MsgLaserHit = "GNRC_SHIP_LHIT"; // server → victim: a Support Ship round popped YOUR car
    const string MsgShotSfx = "GNRC_SHIP_SFX";  // host   → all: a laser was fired / landed, so PLAY it
    const string MsgShipDmg = "GNRC_SHIP_DMG";  // host → all: ship X took a hit — flash + tint, EVENT
    const string MsgRepair = "GNRC_SHIP_REPAIR";// the 3-hop repair handshake, phase byte says which leg
    const string MsgHealth = "GNRC_SHIP_HP";    // host → all: ship X's health pool, ±the repair flourish

    // The aim stream is the only fast one, and only while somebody is actually flying.
    const float AimRate = 20f;
    const float HeartbeatRate = 2f;
    // Eases an incoming offset so a 20 Hz stream glides instead of stepping. Small — this is a slow,
    // deliberate slide inside a box a few tens of metres across, not a car at 600 mph.
    const float AimSmoothTau = 0.07f;
    // How far a received aim may be projected forward before the lead is abandoned. A pilot who stops
    // sending must not have their ship sail off across the movement box on the last known stick.
    const float MaxAimExtrapolation = 0.25f;
    // Smoothing on the DIFFERENTIATED velocity. Differentiating a 20 Hz stream is noisy, and the noise
    // is multiplied by the lead, so it is filtered before it gets there.
    const float AimVelocityTau = 0.10f;

    /// <summary>Sentinel for "no pilot" / "no owner". A real client id can never be this.</summary>
    public const ulong NoClient = ulong.MaxValue;

    public static SupportShipReplicator Instance { get; private set; }

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    /// <summary>What everyone knows about one player's ship. The <see cref="ship"/> is OUR OWN copy,
    /// cloned off our copy of that player's car — it is not, and never needs to be, the same object
    /// the owner is flying.</summary>
    private class ShipEntry
    {
        public bool active;
        public bool inTrack;        // owner is racing — the ship may be piloted from the hub pad
        public byte repairs;        // Support Ship Repairs the OWNER is carrying (see RepairsFor)
        public ulong pilotId = NoClient;
        public Vector3 offset;          // latest received (or locally flown) pilot offset
        public Vector3 smoothedOffset;
        public Vector3 look;            // aim angles: x = yaw, y = pitch, z = roll (degrees)
        public Vector3 smoothedLook;
        public bool hasSmoothed;
        public Vector3 offsetVelocity;  // units/s, differentiated from consecutive aim packets
        public Vector3 lookVelocity;    // deg/s, likewise
        public float aimTime = -1f;     // local time the last aim packet landed (-1 = none yet)
        public SupportShip ship;        // our local visual, null until their puppet exists
        public bool warnedLayer;
    }
    private readonly Dictionary<ulong, ShipEntry> ships = new Dictionary<ulong, ShipEntry>();

    /// <summary>The ship WE are currently flying from the hub, or <see cref="NoClient"/>. Set only by
    /// the server's verdict, never optimistically — two hub players can reach for the same ship in the
    /// same frame and exactly one of them must get it.</summary>
    public static ulong LocalPilotOf { get; private set; } = NoClient;

    /// <summary>Can this ship be taken over from the pilot pad right now?
    ///
    /// A ship exists as soon as its owner summons it, which they may do in the HUB — but there is
    /// nothing to fly over until they are actually racing, so the pad only offers ships whose owner
    /// is in the TrackScene. The owner's own machine is the authority on that (it is the one doing
    /// the travelling) and reports it on the existing ship heartbeat.
    ///
    /// Our OWN ship answers from MultiplayerWorld directly rather than from the table: we never
    /// receive our own heartbeat, so the entry's flag would be whatever we last wrote.</summary>
    static byte LocalRepairStock()
    {
        var inv = PlayerInventory.Instance;
        return inv == null ? (byte)0 : (byte)Mathf.Clamp(inv.GetCount(RepairItem), 0, 255);
    }

    /// <summary>How many Support Ship Repairs are available to whoever is flying this ship.
    ///
    /// ⚠️ It is the OWNER's stock, not the pilot's, because that is whose inventory Y actually spends
    /// from. Showing the pilot their OWN count would be worse than showing nothing: it would be a
    /// confident number that has no bearing on whether the next press does anything.
    ///
    /// The owner is the only machine that can read it - `PlayerInventory.Instance` is a local singleton -
    /// so it rides their existing twice-a-second ship heartbeat. Level-triggered like the rest of that
    /// message, so a dropped packet heals on the next one rather than latching a stale count.</summary>
    public static int RepairsFor(ulong ownerId)
    {
        // Our own ship: read the inventory directly rather than waiting for our own heartbeat to come
        // back around, so spending one updates the readout on the very next frame.
        if (ownerId == LocalClientId) return LocalRepairStock();
        return Instance != null && Instance.ships.TryGetValue(ownerId, out var entry) ? entry.repairs : 0;
    }

    public static bool IsPilotable(ulong ownerId)
    {
        if (ownerId == LocalClientId)
        {
            var ability = SupportShipAbility.Instance;
            return ability != null && ability.IsActive && MultiplayerWorld.LocalInTrackArea;
        }
        return Instance != null && Instance.ships.TryGetValue(ownerId, out var entry)
            && entry.active && entry.inTrack;
    }

    private float nextShipSend;
    private float nextAimSend;
    private bool lastSentActive;
    private bool lastSentInTrack;
    private bool hasSentOnce;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        RegisterHandlers();

        // A hub pilot who drops mid-flight would otherwise leave that ship locked to a client id that
        // no longer exists, and nobody could ever take the controls again.
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnClientDisconnected(ulong clientId)
    {
        ReleaseClaimsHeldBy(clientId);   // a disconnect DOES cost them everything, both ways
        if (ships.TryGetValue(clientId, out var gone)) gone.active = false;   // their ship goes with them
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

        var msg = Msg;
        if (msg != null)
        {
            msg.UnregisterNamedMessageHandler(MsgShip);
            msg.UnregisterNamedMessageHandler(MsgAim);
            msg.UnregisterNamedMessageHandler(MsgPilot);
            msg.UnregisterNamedMessageHandler(MsgDown);
            msg.UnregisterNamedMessageHandler(MsgFire);
            msg.UnregisterNamedMessageHandler(MsgLaserHit);
            msg.UnregisterNamedMessageHandler(MsgShipDmg);
            msg.UnregisterNamedMessageHandler(MsgRepair);
            msg.UnregisterNamedMessageHandler(MsgHealth);
        msg.UnregisterNamedMessageHandler(MsgShotSfx);
        }
        foreach (var kv in ships)
            if (kv.Value.ship != null) Destroy(kv.Value.ship.gameObject);
        ships.Clear();
        LocalPilotOf = NoClient;
        Instance = null;
    }

    void Update()
    {
        BroadcastLocalShipState();
        BroadcastPilotAim();
        SyncShips();
    }

    // -------------------------------------------------------
    //  Queries (PilotControlCenter's data source)
    // -------------------------------------------------------

    /// <summary>Every ship currently out, appended to <paramref name="into"/> as (ownerId, pilotId).
    /// Includes ships already claimed by someone else so the pad can show them as busy rather than
    /// silently hiding them, and our OWN ship — which is what makes the whole feature testable solo:
    /// summon a ship, park on the pad, and fly it.</summary>
    public static void ListActiveShips(List<KeyValuePair<ulong, ulong>> into)
    {
        into.Clear();

        var ability = SupportShipAbility.Instance;
        if (ability != null && ability.IsActive)
            into.Add(new KeyValuePair<ulong, ulong>(LocalClientId, PilotOf(LocalClientId)));

        if (Instance == null) return;
        foreach (var kv in Instance.ships)
            if (kv.Value.active && kv.Key != LocalClientId)
                into.Add(new KeyValuePair<ulong, ulong>(kv.Key, kv.Value.pilotId));
    }

    /// <summary>OUR copy of a given player's ship — what the hub pilot's camera follows. Null while
    /// their car/puppet hasn't arrived yet, or once the ship is gone.
    ///
    /// The local branch deliberately does NOT require <see cref="Instance"/>: this replicator only
    /// exists inside a multiplayer session, but our own ship exists in single-player too, and the pad
    /// is meant to be usable solo for testing.</summary>
    public static SupportShip GetShip(ulong ownerId)
    {
        if (ownerId == LocalClientId)
            return SupportShipAbility.Instance != null ? SupportShipAbility.Instance.Ship : null;
        return Instance != null && Instance.ships.TryGetValue(ownerId, out var entry) ? entry.ship : null;
    }

    /// <summary>Who is flying a given player's ship, or <see cref="NoClient"/>.</summary>
    public static ulong PilotOf(ulong ownerId) =>
        Instance != null && Instance.ships.TryGetValue(ownerId, out var entry) ? entry.pilotId : NoClient;

    // -------------------------------------------------------
    //  Outgoing: our own ship's existence
    // -------------------------------------------------------

    void BroadcastLocalShipState()
    {
        var ability = SupportShipAbility.Instance;
        if (ability == null) return;

        bool active = ability.IsActive;
        // Crossing the portal decides whether a teammate may fly this ship AT ALL, so it counts as a
        // change: it goes out reliably and immediately rather than waiting up to half a second for the
        // next heartbeat, which would leave the pad offering a ship that is already back in the hub.
        bool inTrack = MultiplayerWorld.LocalInTrackArea;
        bool changed = !hasSentOnce || active != lastSentActive || inTrack != lastSentInTrack;

        if (!changed && Time.unscaledTime < nextShipSend) return;
        nextShipSend = Time.unscaledTime + 1f / HeartbeatRate;
        lastSentActive = active;
        lastSentInTrack = inTrack;
        hasSentOnce = true;

        // Record our OWN ship in the same table everyone else's lives in. Without this the host's
        // table has no entry for the host's ship, and ResolvePilotRequest — which runs on the host and
        // checks `active` before granting — would refuse every attempt to fly it.
        byte repairs = LocalRepairStock();

        var mine = GetOrCreate(LocalClientId);
        mine.active = active;
        mine.inTrack = inTrack;
        mine.repairs = repairs;

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(24, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(active);
        writer.WriteValueSafe(inTrack);
        writer.WriteValueSafe(repairs);

        // Summoning and dismissing must never be the packet that goes missing — a lost "dismissed"
        // would leave a ghost ship escorting everyone else's view of this player forever.
        var delivery = changed ? NetworkDelivery.ReliableSequenced : NetworkDelivery.Unreliable;
        if (IsServer) SendToRemoteClients(MsgShip, writer, delivery);
        else msg.SendNamedMessage(MsgShip, NetworkManager.ServerClientId, writer, delivery);
    }

    // -------------------------------------------------------
    //  Outgoing: the offset we're flying
    // -------------------------------------------------------

    void BroadcastPilotAim()
    {
        if (LocalPilotOf == NoClient) return;

        var ship = GetShip(LocalPilotOf);
        if (ship == null) return;

        if (Time.unscaledTime < nextAimSend) return;
        nextAimSend = Time.unscaledTime + 1f / AimRate;

        var msg = Msg;
        if (msg == null) return;
        Vector3 offset = ship.PilotOffset;
        Vector3 look = ship.PilotLook;

        using var writer = new FastBufferWriter(48, Allocator.Temp);
        writer.WriteValueSafe(LocalPilotOf);
        writer.WriteValueSafe(offset);
        writer.WriteValueSafe(look);

        // Lossy on purpose: the next tick is 50 ms away and carries the absolute offset, so a dropped
        // packet costs one frame of staleness and heals itself. Same reasoning as the car stream.
        if (IsServer) SendToRemoteClients(MsgAim, writer, NetworkDelivery.Unreliable);
        else msg.SendNamedMessage(MsgAim, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
    }

    // -------------------------------------------------------
    //  Claiming / releasing the controls
    // -------------------------------------------------------

    /// <summary>Ask the server for (or hand back) the controls of a given player's ship. The answer
    /// arrives asynchronously as a verdict — callers must react to <see cref="LocalPilotOf"/> changing
    /// rather than assuming the request succeeded.</summary>
    public static void RequestPilot(ulong ownerId, bool claim)
    {
        // Single-player / no session: nobody to arbitrate with, so take it directly. This is what lets
        // the whole feature be exercised solo in the editor.
        if (Msg == null)
        {
            LocalPilotOf = claim ? ownerId : NoClient;
            return;
        }

        if (IsServer) { ResolvePilotRequest(ownerId, LocalClientId, claim); return; }

        using var writer = new FastBufferWriter(48, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(claim);
        Msg.SendNamedMessage(MsgPilot, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>SERVER ONLY. Grants the controls if the ship is free, or releases them if the asker is
    /// the one holding them, then tells everybody the outcome. A refusal is deliberately still
    /// broadcast: it re-states who really holds the ship, which corrects the asker's UI.</summary>
    static void ResolvePilotRequest(ulong ownerId, ulong requesterId, bool claim)
    {
        if (Instance == null) return;
        var entry = Instance.GetOrCreate(ownerId);

        if (claim)
        {
            // `inTrack` is checked HERE and not only in the pad's UI. The list can be a heartbeat
            // stale, and a player pressing A on the frame their teammate crosses the return portal
            // must not end up flying a ship parked back in the hub.
            if (entry.active && entry.inTrack && entry.pilotId == NoClient) entry.pilotId = requesterId;
        }
        else if (entry.pilotId == requesterId)
        {
            entry.pilotId = NoClient;
        }

        BroadcastPilotVerdict(ownerId, entry.pilotId);

        // A new pilot inherits a ship that may already be damaged, and their copy of the pool is only
        // as current as the damage events they happened to be present for. Re-state it.
        if (entry.pilotId != NoClient) ReportShipHealth(ownerId, repaired: false);
    }

    static void BroadcastPilotVerdict(ulong ownerId, ulong pilotId)
    {
        if (Instance != null) Instance.ApplyPilotVerdict(ownerId, pilotId);

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(48, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe(pilotId);
        writer.WriteValueSafe(true);   // padding: verdicts reuse the request layout so the size is fixed
        SendToRemoteClients(MsgPilot, writer, NetworkDelivery.ReliableSequenced);
    }

    void ApplyPilotVerdict(ulong ownerId, ulong pilotId)
    {
        GetOrCreate(ownerId).pilotId = pilotId;

        if (pilotId == LocalClientId) LocalPilotOf = ownerId;
        else if (LocalPilotOf == ownerId) LocalPilotOf = NoClient;   // someone else got it, or it was freed
    }

    /// <summary>SERVER ONLY. Frees the controls of ONE ship, because that ship is gone (dismissed by its
    /// owner, or destroyed).
    ///
    /// ⚠️ Kept strictly separate from <see cref="ReleaseClaimsHeldBy"/>, and the distinction is not
    /// cosmetic — conflating the two was a real bug (2026-08-16). Every client heartbeats its OWN ship
    /// state twice a second, so a pilot who owns no ship of their own continuously announces
    /// `{theirId, active:false}`. When "this player's ship is inactive" also released claims HELD BY
    /// that player, a hub pilot's own routine heartbeat revoked the claim they had just made — they got
    /// booted out of the cockpit within half a second, every single time.</summary>
    void ReleaseClaimsOnShip(ulong ownerId)
    {
        if (!IsServer) return;
        if (!ships.TryGetValue(ownerId, out var entry) || entry.pilotId == NoClient) return;

        entry.pilotId = NoClient;
        BroadcastPilotVerdict(ownerId, NoClient);
    }

    /// <summary>SERVER ONLY. Frees every ship a departing client was flying, AND their own ship's
    /// controls. Only correct on DISCONNECT — see the warning above.</summary>
    void ReleaseClaimsHeldBy(ulong clientId)
    {
        if (!IsServer) return;

        // Snapshot first: broadcasting a verdict reaches back into `ships`, and mutating a dictionary
        // mid-enumeration would throw.
        releaseScratch.Clear();
        foreach (var kv in ships)
            if (kv.Value.pilotId != NoClient && (kv.Value.pilotId == clientId || kv.Key == clientId))
                releaseScratch.Add(kv.Key);

        foreach (var ownerId in releaseScratch)
        {
            ships[ownerId].pilotId = NoClient;
            BroadcastPilotVerdict(ownerId, NoClient);
        }
    }
    private readonly List<ulong> releaseScratch = new List<ulong>();

    // -------------------------------------------------------
    //  Firing
    // -------------------------------------------------------

    /// <summary>The pilot pulled the trigger. Unlike the AIM offset — which the pilot owns outright,
    /// because it is pure presentation — a round that can knock a drone out of the sky is game state,
    /// so it is spawned on the HOST and streamed back like every other projectile in the game. The
    /// pilot therefore sees their own shot a round trip late; that is the price of the round existing
    /// in one place, and it is nil when the pilot happens to be the host.
    ///
    /// The request carries the ship's POSE AT THE PRESS, and that is the whole point of it. The host's
    /// copy of a client-piloted ship trails the truth by the aim interval + latency + AimSmoothTau +
    /// the request's own flight time, so spawning off the host's copy put rounds visibly behind and
    /// beside the ship while strafing. The pilot knows exactly where they were, so they say so; the
    /// host rebuilds the muzzle from it in <see cref="SupportShip.FireLaserAt"/> and clamps it there.
    /// Nothing new is trusted — the pilot already dictates this same offset outright via GNRC_SHIP_AIM.</summary>
    public static void RequestFire(ulong ownerId)
    {
        // No session (single-player, or the pad used solo for testing): there IS no host, so the only
        // copy of the world is this one, it is already at the true pose, and there is nothing to send.
        if (Msg == null) { FireOnAuthority(ownerId, LocalClientId); return; }
        if (IsServer) { FireOnAuthority(ownerId, LocalClientId); return; }

        // OUR copy of the ship we are flying is the authority on where it is: SyncShips deliberately
        // leaves a local pilot's own ship on their live stick rather than on anything received.
        var ship = GetShip(ownerId);
        Vector3 offset = ship != null ? ship.PilotOffset : Vector3.zero;
        Vector3 look = ship != null ? ship.PilotLook : Vector3.zero;

        using var writer = new FastBufferWriter(48, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe(offset);
        writer.WriteValueSafe(look);
        Msg.SendNamedMessage(MsgFire, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Spawns the round off this machine's copy of that player's ship. `FireLaser` calls
    /// `NpcReplicator.Track`, which is a host-only no-op elsewhere, so the same call does the right
    /// thing whether this is a real host or a solo session.</summary>
    static void FireOnAuthority(ulong ownerId, ulong pilotClientId)
    {
        var ship = GetShip(ownerId);
        if (ship != null) ship.FireLaser(pilotClientId, pilotClientId == LocalClientId);
    }

    /// <summary>As above, but from the pose the pilot reported at the moment of the press rather than
    /// from this machine's (necessarily stale) copy of their ship. See RequestFire.</summary>
    static void FireOnAuthority(ulong ownerId, ulong pilotClientId, Vector3 offset, Vector3 look)
    {
        var ship = GetShip(ownerId);
        if (ship != null) ship.FireLaserAt(offset, look, pilotClientId, pilotClientId == LocalClientId);
    }

    // -------------------------------------------------------
    //  Repair: one item, spent on one machine, healing state held on another
    // -------------------------------------------------------

    /// <summary>The inventory item a repair spends. It sits in the SHIP OWNER's inventory, not the
    /// pilot's - Player A buys the repair, Player B flies the ship and decides when to burn it.</summary>
    public const string RepairItem = "Support Ship Repair";

    /// <summary>Which leg of the handshake a GNRC_SHIP_REPAIR message is.
    ///
    /// ⚠️ Three hops, because the two halves of a repair live on two DIFFERENT machines and neither can
    /// do the other's job. The health pool is host-side (only the host counts a ship's hits), while the
    /// item is in the owner's inventory - and `PlayerInventory.Instance` is a purely LOCAL singleton, so
    /// no other machine can even read it, let alone spend from it. The pilot, meanwhile, may be a third
    /// machine again. So: the pilot asks the host, the host checks the damage and asks the owner to
    /// spend, and the owner reports back that it did.
    ///
    /// The host still gates the chain, but only on whether the ship can take a repair AT ALL - not on
    /// whether it needs one. Spending an item on a full-health ship is allowed and burns it: deciding
    /// when to patch up is the pilot's call to get right.</summary>
    enum RepairPhase : byte { PilotAsks = 0, OwnerSpends = 1, OwnerSpent = 2 }

    /// <summary>Pilot side: "give the ship I am flying some health back". The verdict is asynchronous
    /// and may simply not come - the ship may be undamaged, or the owner may hold no repair.</summary>
    public static void RequestRepair(ulong ownerId)
    {
        // Solo (no session, or the pad used alone for testing): one machine holds the ship, the health
        // and the inventory, so the whole handshake collapses to doing it.
        if (Msg == null)
        {
            var ship = GetShip(ownerId);
            if (ship == null || !ship.Repairable) return;
            if (!SpendRepairLocally()) return;
            ship.TryRepair();
            ShowRepairAt(ship);
            return;
        }

        if (IsServer) { BeginRepair(ownerId, LocalClientId); return; }
        SendRepairPhase(ownerId, RepairPhase.PilotAsks, NetworkManager.ServerClientId);
    }

    static void SendRepairPhase(ulong ownerId, RepairPhase phase, ulong to)
    {
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe((byte)phase);
        msg.SendNamedMessage(MsgRepair, to, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>HOST: a pilot asked to repair the ship they are flying. Validate, then find the item.</summary>
    static void BeginRepair(ulong ownerId, ulong pilotId)
    {
        // Only whoever actually holds the controls, exactly as with the guns.
        if (PilotOf(ownerId) != pilotId) return;

        var ship = GetShip(ownerId);
        if (ship == null || !ship.Repairable) return;   // a wreck cannot be patched; a healthy ship can

        if (ownerId == LocalClientId)
        {
            // We ARE the owner: the inventory is right here.
            if (SpendRepairLocally()) RepairOnAuthority(ownerId);
            return;
        }
        SendRepairPhase(ownerId, RepairPhase.OwnerSpends, ownerId);
    }

    /// <summary>OWNER: the host says our ship needs a repair and someone is flying it. Spend one if we
    /// have it, and only then tell the host to heal.</summary>
    static void OwnerSpendRepair(ulong ownerId)
    {
        if (SpendRepairLocally()) SendRepairPhase(ownerId, RepairPhase.OwnerSpent, NetworkManager.ServerClientId);
    }

    static bool SpendRepairLocally()
    {
        var inv = PlayerInventory.Instance;
        if (inv == null || !inv.Consume(RepairItem, 1)) return false;
        Debug.Log($"[SupportShip] Repair spent ({inv.GetCount(RepairItem)} left).");
        return true;
    }

    /// <summary>HOST: the owner paid, so give the health back and tell everyone to sound it.</summary>
    static void RepairOnAuthority(ulong ownerId)
    {
        var ship = GetShip(ownerId);
        if (ship == null) return;

        // Not gated on the return value: at full health this heals nothing, and the item is spent all
        // the same. The sound still plays either way, and that matters - the pilot cannot see the
        // OWNER's inventory, so silence here would read as a dropped input and they would press again.
        ship.TryRepair();
        ShowRepairAt(ship);
        ReportShipHealth(ownerId, repaired: true);
    }

    /// <summary>The repair's outward sign on THIS machine: a blue flash and the repair sound.
    ///
    /// It needs one, and pointedly so: since the damage tint became flash-only, a damaged ship looks
    /// exactly like a healthy one, so without this the pilot presses Y and nothing whatsoever happens
    /// on screen. Runs on every copy - the escorted racer sees their own ship patched up, and anyone
    /// nearby sees it too.</summary>
    static void ShowRepairAt(SupportShip ship)
    {
        if (ship != null) ship.ApplyRepairFeedback(ship.HitsTaken, ship.maxHits);
    }

    /// <summary>HOST → everyone: what a ship's health pool now reads.
    ///
    /// ⚠️ The pilot's health bar is the reason this exists. Only the host counts a ship's hits, so on
    /// every other machine the pool was a number nobody maintained - and the pilot is very often a
    /// client, looking at the one readout that has to be right. Damage already replicated (GNRC_SHIP_DMG
    /// drives the flash); this covers the two moments that did not: a repair, and the instant someone
    /// TAKES the controls, when their bar has to start off correct rather than at whatever their copy
    /// last happened to hear.
    ///
    /// <paramref name="repaired"/> distinguishes the two: true also flashes blue and sounds, false just
    /// sets the number.</summary>
    public static void ReportShipHealth(ulong ownerId, bool repaired)
    {
        var msg = Msg;
        if (msg == null || !IsServer) return;

        var ship = GetShip(ownerId);
        if (ship == null) return;

        using var writer = new FastBufferWriter(24, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe((byte)Mathf.Clamp(ship.HitsTaken, 0, 255));
        writer.WriteValueSafe((byte)Mathf.Clamp(ship.maxHits, 1, 255));
        writer.WriteValueSafe((byte)(repaired ? 1 : 0));
        SendToRemoteClients(MsgHealth, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Pays a Support Ship gunner for something their round destroyed. Called from the HOST
    /// (the only machine lasers exist on), so the remote branch is always available.</summary>
    public static void AwardPilot(ulong pilotClientId, bool pilotIsLocal, int credits)
    {
        if (credits <= 0) return;

        if (!pilotIsLocal)
        {
            NpcReplicator.SendBounty(pilotClientId, credits);
            Debug.Log($"[SupportShip] Gunner (client {pilotClientId}) paid {credits} for a laser kill.");
            return;
        }

        if (PlayerInventory.Instance == null) return;
        PlayerInventory.Instance.AddCredits(credits);
        AudioManager.PlayKnockoffBounty();
        Debug.Log($"[SupportShip] Local gunner awarded {credits} for a laser kill.");
    }

    /// <summary>HOST — everyone: a Support Ship round was fired, or landed. Sound only.
    ///
    /// ⚠️ Needed because the ROUNDS ONLY EXIST ON THE HOST. Clients receive them as NpcReplicator
    /// puppets, and StripPuppet destroys every MonoBehaviour, AudioSource and collider on a puppet — so
    /// no client ever ran the code that plays the muzzle or the impact. A CLIENT flying a Support Ship
    /// therefore watched their own guns fire in complete silence, which is exactly the feedback the
    /// two-flavour impact audio exists to give them.
    ///
    /// An EVENT, not a puppet property: these are one-shots at a world position, there is nothing to
    /// heal on a later tick, and the impact point is nowhere near the ship by the time it happens.
    /// Unreliable — a dropped gunshot is a missing tick, not a broken state, and they come thick and
    /// fast in a burst.</summary>
    public static void ReportShotSound(Vector3 position, ShotSound kind)
    {
        var msg = Msg;
        if (msg == null || !IsServer) return;

        using var writer = new FastBufferWriter(32, Allocator.Temp);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe((byte)kind);
        SendToRemoteClients(MsgShotSfx, writer, NetworkDelivery.Unreliable);
    }

    /// <summary>Which of the gun's three sounds to play. The two IMPACT flavours are the gunner's only
    /// hit feedback, so they must stay distinguishable across the wire too.</summary>
    public enum ShotSound : byte { Fire = 0, HitEnvironment = 1, HitEntity = 2 }

    /// <summary>Called by the ONE machine that counts a ship's hits (the host in a session) so every
    /// other copy can flash and sound the hit. A Support Ship is not an NpcReplicator entity — it's keyed on its
    /// owner's client id, not a spawn id — so `GNRC_NPC_DMG` can't carry this and it needs its own
    /// event. Reliable and damage-only: a missed flash cannot be recovered, since there is no
    /// level-triggered state to heal it on a later tick.</summary>
    public static void ReportShipDamage(ulong ownerId, int hitsTaken, int maxHits)
    {
        var msg = Msg;
        if (msg == null || !IsServer) return;   // offline: the only copy already flashed locally

        using var writer = new FastBufferWriter(24, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        writer.WriteValueSafe((byte)Mathf.Clamp(hitsTaken, 0, 255));
        writer.WriteValueSafe((byte)Mathf.Clamp(maxHits, 1, 255));
        SendToRemoteClients(MsgShipDmg, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>HOST → the machine that owns a car a laser round just hit. Movement is
    /// owner-authoritative, so the pop-up has to be applied over there on the real car rather than to
    /// the kinematic puppet the host was actually shooting at — the same routing
    /// <see cref="GrappleReplicator.SendPullToOwner"/> and <c>GNRC_NPC_HIT</c> use. The victim also
    /// judges their OWN invulnerability window, which is where that state lives.</summary>
    public static void SendLaserHitToOwner(ulong targetClientId)
    {
        var msg = Msg;
        if (msg == null || !IsServer) return;
        if (targetClientId == LocalClientId) return;   // our own car is popped directly, not messaged

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(targetClientId);
        msg.SendNamedMessage(MsgLaserHit, targetClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>A Support Ship round hit OUR car — apply it to the real thing on this machine, gated by
    /// our own laser window (which is deliberately separate from the DronePissBall one).</summary>
    static void ApplyLaserHitLocally()
    {
        // Same pair of gates the local path uses — our window and our shield, both judged here because
        // this is the machine that owns the car.
        if (SupportShipLaser.PlayerInvulnerable || ShieldAbility.LocalShieldUp) return;

        var car = PlayerRegistry.LocalCar;
        if (car == null) return;

        // Read the tuned values off the prefab where possible so the felt effect is identical wherever
        // the round happened to be simulated.
        var ship = SupportShipAbility.Instance != null ? SupportShipAbility.Instance.Ship : null;
        var template = ship != null && ship.laserPrefab != null
            ? ship.laserPrefab.GetComponent<SupportShipLaser>() : null;
        float force = template != null ? template.popUpForce : 40f;
        float seconds = template != null ? template.hitInvulnerabilitySeconds : 2f;

        SupportShipLaser.ApplyPopUp(car, force);
        SupportShipLaser.BeginInvulnerability(seconds);
    }

    // -------------------------------------------------------
    //  Downing a ship
    // -------------------------------------------------------

    /// <summary>Report that a ship has been downed. On the server this IS the verdict; on a client it's
    /// a report the server will confirm. Either way every machine ends up calling Crash() on its own
    /// copy, and the owner's <see cref="SupportShipAbility"/> spends the item exactly once.</summary>
    public static void ReportDown(ulong ownerId)
    {
        if (Msg == null) { ApplyDownLocally(ownerId); return; }
        if (IsServer) { BroadcastDownVerdict(ownerId); return; }

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        Msg.SendNamedMessage(MsgDown, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    static void BroadcastDownVerdict(ulong ownerId)
    {
        // Two machines can legitimately report the same wreck (the owner felt the hit, the host saw
        // the projectile land). The first verdict clears the entry, so the second is dropped here
        // rather than being fanned out to everyone a second time.
        if (Instance != null && Instance.ships.TryGetValue(ownerId, out var known) && !known.active
            && ownerId != LocalClientId)
            return;

        ApplyDownLocally(ownerId);
        if (Instance != null) Instance.ReleaseClaimsOnShip(ownerId);   // the wreck, not its pilot's other claims

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        SendToRemoteClients(MsgDown, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Ragdoll our own copy of that player's ship. Crash() is idempotent, so the machine that
    /// originally spotted the hit simply no-ops when the verdict comes back to it.</summary>
    static void ApplyDownLocally(ulong ownerId)
    {
        var ship = GetShip(ownerId);
        if (ship != null) ship.Crash();

        if (Instance != null && Instance.ships.TryGetValue(ownerId, out var entry))
        {
            entry.active = false;
            entry.ship = null;      // the wreck destroys itself after its ragdoll
        }
    }

    // -------------------------------------------------------
    //  Incoming
    // -------------------------------------------------------

    void RegisterHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[SupportShip] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgShip, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out bool active);
            reader.ReadValueSafe(out bool inTrack);
            reader.ReadValueSafe(out byte repairs);

            if (IsServer && ownerId != LocalClientId)
            {
                using var writer = new FastBufferWriter(24, Allocator.Temp);
                writer.WriteValueSafe(ownerId);
                writer.WriteValueSafe(active);
                writer.WriteValueSafe(inTrack);
                writer.WriteValueSafe(repairs);
                SendToRemoteClients(MsgShip, writer, NetworkDelivery.ReliableSequenced, excludeClientId: ownerId);

                // A ship that can no longer be flown can't stay claimed — free THAT SHIP's controls so
                // the hub player isn't left steering nothing. Two ways that happens: the owner puts the
                // ship away, or the owner takes the return portal out of the track, which ends their
                // teammate's session at the pad exactly as if they had pressed SELECT. Note this must
                // NOT touch claims this player holds on OTHER ships (see ReleaseClaimsOnShip).
                if (!active || !inTrack) ReleaseClaimsOnShip(ownerId);
            }

            if (ownerId != LocalClientId)
            {
                var entry = GetOrCreate(ownerId);
                entry.active = active;
                entry.inTrack = inTrack;
                entry.repairs = repairs;
            }
        });

        msg.RegisterNamedMessageHandler(MsgAim, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out Vector3 offset);
            reader.ReadValueSafe(out Vector3 look);

            if (IsServer)
            {
                using var writer = new FastBufferWriter(48, Allocator.Temp);
                writer.WriteValueSafe(ownerId);
                writer.WriteValueSafe(offset);
                writer.WriteValueSafe(look);
                SendToRemoteClients(MsgAim, writer, NetworkDelivery.Unreliable, excludeClientId: sender);
            }

            // Our own flying is authoritative for us — never let a relayed echo of it fight the stick.
            if (LocalPilotOf == ownerId) return;
            var aimed = GetOrCreate(ownerId);
            RecordAim(aimed, offset, look);
        });

        msg.RegisterNamedMessageHandler(MsgPilot, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out ulong pilotId);
            reader.ReadValueSafe(out bool claim);

            // On the server this is a REQUEST from `pilotId`; on a client it's the settled verdict.
            if (IsServer) ResolvePilotRequest(ownerId, pilotId, claim);
            else ApplyPilotVerdict(ownerId, pilotId);
        });

        msg.RegisterNamedMessageHandler(MsgDown, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);

            if (IsServer) BroadcastDownVerdict(ownerId);   // a client's report — confirm it to everyone
            else ApplyDownLocally(ownerId);
        });

        msg.RegisterNamedMessageHandler(MsgFire, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out Vector3 offset);
            reader.ReadValueSafe(out Vector3 look);
            if (!IsServer) return;   // one-way: only the host acts on a trigger pull

            // Only whoever actually holds the controls may fire this ship. Cheap, and it means a
            // malformed or stale request can't have someone else's guns going off.
            if (PilotOf(ownerId) != sender) return;
            FireOnAuthority(ownerId, sender, offset, look);
        });

        msg.RegisterNamedMessageHandler(MsgLaserHit, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong targetClientId);
            if (targetClientId == LocalClientId) ApplyLaserHitLocally();
        });

        msg.RegisterNamedMessageHandler(MsgShotSfx, (sender, reader) =>
        {
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out byte kind);

            // The 3D tuning lives on the LASER PREFAB for the impacts, which a client has registered
            // with NpcReplicator, so pass what we have and let AudioManager fall back to the shared
            // Support Ship block when it is null.
            var laser = LocalLaserTuning();
            switch ((ShotSound)kind)
            {
                case ShotSound.Fire:
                    AudioManager.PlaySupportShipLaserFire(position); break;
                case ShotSound.HitEntity:
                    AudioManager.PlaySupportShipLaserHitEntity(position, laser != null ? laser.entityAudio3D : null); break;
                default:
                    AudioManager.PlaySupportShipLaserHitEnvironment(position, laser != null ? laser.environmentAudio3D : null); break;
            }
        });

        msg.RegisterNamedMessageHandler(MsgRepair, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out byte phase);
            switch ((RepairPhase)phase)
            {
                case RepairPhase.PilotAsks:
                    if (IsServer) BeginRepair(ownerId, sender);
                    break;
                case RepairPhase.OwnerSpends:
                    if (ownerId == LocalClientId) OwnerSpendRepair(ownerId);
                    break;
                case RepairPhase.OwnerSpent:
                    // The owner is the only one who may claim to have paid for their own ship.
                    if (IsServer && sender == ownerId) RepairOnAuthority(ownerId);
                    break;
            }
        });

        msg.RegisterNamedMessageHandler(MsgHealth, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out byte hits);
            reader.ReadValueSafe(out byte max);
            reader.ReadValueSafe(out byte repaired);

            var ship = GetShip(ownerId);
            if (ship == null) return;
            if (repaired != 0) ship.ApplyRepairFeedback(hits, max);
            else ship.SyncHealth(hits, max);
        });

        msg.RegisterNamedMessageHandler(MsgShipDmg, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out byte hitsTaken);
            reader.ReadValueSafe(out byte maxHits);

            var ship = GetShip(ownerId);   // resolves our OWN ship or our copy of theirs
            // Hits only — a downed ship is not reported here. Its verdict comes through GNRC_SHIP_DOWN
            // and runs Crash() on this copy, which paints the wreck itself.
            if (ship != null) ship.ApplyDamageFeedback(hitsTaken, maxHits);
        });
    }

    /// <summary>The laser prefab's own SupportShipLaser, purely to borrow its authored 3D blocks for a
    /// sound we were TOLD about rather than one we spawned. Cached — a burst asks three times a second.</summary>
    static SupportShipLaser LocalLaserTuning()
    {
        if (laserTuning != null) return laserTuning;
        var ability = SupportShipAbility.Instance;
        var template = ability != null ? ability.Ship : null;
        GameObject prefab = template != null ? template.laserPrefab : null;
        if (prefab == null && Instance != null)
            foreach (var kv in Instance.ships)
                if (kv.Value.ship != null && kv.Value.ship.laserPrefab != null)
                { prefab = kv.Value.ship.laserPrefab; break; }
        if (prefab != null) laserTuning = prefab.GetComponent<SupportShipLaser>();
        return laserTuning;
    }
    static SupportShipLaser laserTuning;

    ShipEntry GetOrCreate(ulong ownerId)
    {
        if (!ships.TryGetValue(ownerId, out var entry))
            ships[ownerId] = entry = new ShipEntry();
        return entry;
    }

    // -------------------------------------------------------
    //  Remote ship visuals
    // -------------------------------------------------------

    /// <summary>Drives the pilot offset of EVERY known ship on this machine — including our own — and
    /// builds/tears down our copies of other players' ships.
    ///
    /// The offset rule is the important part, and it has exactly one exception:
    ///  • **If WE are flying it**, our own <see cref="SupportShip.PilotOffset"/> writes are the truth and
    ///    nothing here may touch them. We only copy the value back into the entry so the transition when
    ///    we let go is seamless.
    ///  • **Otherwise** the ship eases toward the offset last received over the wire — whoever is flying
    ///    it, or the value it was abandoned at.
    ///
    /// Our OWN ship is included deliberately: it is owned by <see cref="SupportShipAbility"/>, which
    /// knows nothing about a teammate flying it, so if this method skipped it the owner's ship would
    /// never move — which is the whole feature.</summary>
    void SyncShips()
    {
        foreach (var kv in ships)
        {
            ulong ownerId = kv.Key;
            var entry = kv.Value;

            SupportShip ship;
            if (ownerId == LocalClientId)
            {
                // Never built or destroyed here — SupportShipAbility owns its lifetime. We only steer it.
                ship = SupportShipAbility.Instance != null ? SupportShipAbility.Instance.Ship : null;
                if (ship == null) { ClearAimPrediction(entry); continue; }
            }
            else
            {
                ship = ResolveRemoteShip(ownerId, entry);
                if (ship == null) continue;
            }

            // THE EXCEPTION. Without it, a pilot's stick is overwritten every frame by the offset they
            // were last SENT — and a machine never sends itself its own aim, so that value stays frozen
            // and drags the ship straight back. It looks exactly like a ship that refuses to move.
            if (LocalPilotOf == ownerId)
            {
                entry.offset = ship.PilotOffset;
                entry.look = ship.PilotLook;
                entry.smoothedOffset = entry.offset;
                entry.smoothedLook = entry.look;
                entry.hasSmoothed = true;
                continue;
            }

            // Ease the received values so a 20 Hz stream reads as a glide rather than a staircase —
            // but ease toward a LED target, not the raw one. See LeadAim.
            Vector3 targetOffset = ship.ClampPilotOffset(LeadAim(entry.offset, entry.offsetVelocity, entry.aimTime));
            Vector3 targetLook = ship.ClampPilotLook(LeadAim(entry.look, entry.lookVelocity, entry.aimTime));

            if (!entry.hasSmoothed)
            {
                entry.smoothedOffset = targetOffset;
                entry.smoothedLook = targetLook;
                entry.hasSmoothed = true;
            }
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(AimSmoothTau, 1e-4f));
            entry.smoothedOffset = Vector3.Lerp(entry.smoothedOffset, targetOffset, t);
            entry.smoothedLook = Vector3.Lerp(entry.smoothedLook, targetLook, t);
            ship.PilotOffset = entry.smoothedOffset;
            ship.PilotLook = entry.smoothedLook;
        }
    }

    /// <summary>Forgets everything we had inferred about how a ship was being flown. Called whenever the
    /// ship goes away, because the next frame with a rebuilt ship SNAPS to its target — and snapping to
    /// a pose led by a rate estimated before the ship existed is a visible pop.</summary>
    static void ClearAimPrediction(ShipEntry entry)
    {
        entry.hasSmoothed = false;
        entry.offsetVelocity = Vector3.zero;
        entry.lookVelocity = Vector3.zero;
        entry.aimTime = -1f;
    }

    /// <summary>Files a freshly received aim, and DIFFERENTIATES it into a rate so the ship can be led
    /// rather than trailed. The rate is filtered: a 20 Hz stream differentiates noisily, and the noise
    /// is about to be multiplied by the lead time.</summary>
    static void RecordAim(ShipEntry entry, Vector3 offset, Vector3 look)
    {
        float now = Time.time;
        float dt = entry.aimTime < 0f ? 0f : now - entry.aimTime;

        if (dt > 1e-4f && dt < MaxAimExtrapolation)
        {
            Vector3 rawOffsetVel = (offset - entry.offset) / dt;
            Vector3 rawLookVel = (look - entry.look) / dt;
            float t = 1f - Mathf.Exp(-dt / Mathf.Max(AimVelocityTau, 1e-4f));
            entry.offsetVelocity = Vector3.Lerp(entry.offsetVelocity, rawOffsetVel, t);
            entry.lookVelocity = Vector3.Lerp(entry.lookVelocity, rawLookVel, t);
        }
        else if (dt >= MaxAimExtrapolation)
        {
            // A long gap says nothing useful about the pilot's stick — start the estimate over rather
            // than leading on a rate averaged across it.
            entry.offsetVelocity = Vector3.zero;
            entry.lookVelocity = Vector3.zero;
        }

        entry.offset = offset;
        entry.look = look;
        entry.aimTime = now;
    }

    /// <summary>Projects a received aim value forward to where it should be NOW.
    ///
    /// Two lags are cancelled at once, and the second is the subtle one. The obvious one is packet AGE:
    /// at 20 Hz the newest value is already up to 50 ms old. The other is that an exponential chase
    /// sits <c>tau—v</c> BEHIND a moving target in steady state — so smoothing toward the raw value
    /// leaves the ship permanently trailing by AimSmoothTau (70 ms) even with a perfect connection.
    /// Leading by <c>age + tau</c> cancels both to first order. Exactly the trick, and the exact same
    /// reasoning, as RemoteCarPuppet's projection — which had this problem first, at 268 m/s.
    ///
    /// The age is capped so a pilot who stops sending parks rather than sails, and the CALLER clamps
    /// the result to the movement box and aim limits — without that, leading a pilot who is pinned
    /// against a wall of the box would push their ship visibly outside it.</summary>
    static Vector3 LeadAim(Vector3 value, Vector3 velocity, float stamp)
    {
        if (stamp < 0f) return value;
        float age = Mathf.Min(Time.time - stamp, MaxAimExtrapolation);
        return value + velocity * (age + AimSmoothTau);
    }

    /// <summary>Our copy of another player's ship, built on demand from the template on their own puppet
    /// — so it is automatically the right model for the car they chose, and it follows that puppet with
    /// the same lag the real one follows the real car. Null while they have no ship out or their car
    /// hasn't arrived.</summary>
    SupportShip ResolveRemoteShip(ulong ownerId, ShipEntry entry)
    {
        var remote = PlayerRegistry.FindRemote(ownerId);
        GameObject car = remote != null ? remote.Car : null;

        // Not out, or their car isn't here (roster still landing / they disconnected).
        if (!entry.active || car == null)
        {
            if (entry.ship != null) { Destroy(entry.ship.gameObject); entry.ship = null; }
            ClearAimPrediction(entry);
            return null;
        }

        if (entry.ship != null) return entry.ship;

        var template = SupportShipAbility.FindChildByName(car.transform, SupportShipAbility.ShipChildName);
        if (template == null) return null;   // their car model has no ship — nothing we can draw

        string layerName = SupportShipAbility.Instance != null
            ? SupportShipAbility.Instance.shipLayerName : "SupportShip";
        entry.ship = SupportShipAbility.BuildShip(template, car.transform, layerName, ref entry.warnedLayer);
        if (entry.ship == null) return null;
        entry.ship.name = "SupportShip_Remote_" + ownerId;

        // The puppet's template was stripped of its scripts, so the clone came up on code defaults.
        // Re-tune it from the untouched PREFAB ASSET for the car this player chose, or a teammate's
        // ship would fly with different limits and speed from your own.
        entry.ship.CopyTuningFrom(TuningTemplateFor(remote.CarName));

        // Clients have no other path to the laser prefab — it's only referenced from a car prefab's
        // SupportShip component — so register it now or NpcReplicator can't build puppets for the
        // rounds this ship fires.
        NpcReplicator.RegisterPrefab(entry.ship.laserPrefab);

        // Only the HOST may call a crash on someone else's ship: it is the one machine with real
        // projectiles and real obstacles. Every other viewer's copy is derived from an interpolated
        // puppet and would invent hits, so it waits to be told.
        entry.ship.ownerClientId = ownerId;   // so the host can name it when reporting damage
        entry.ship.detectCrashes = IsServer;

        // Tint tuning, same reason as CopyTuningFrom: this clone came off a STRIPPED puppet template,
        // so it has no DroneDamageTint of its own and would flash in code defaults.
        var authoredShip = TuningTemplateFor(remote.CarName);
        if (authoredShip != null)
            entry.ship.SeedDamageTint(authoredShip.GetComponentInChildren<DroneDamageTint>(true));
        if (IsServer)
        {
            ulong captured = ownerId;
            entry.ship.onCrashed += _ => ReportDown(captured);
        }
        entry.ship.PilotOffset = entry.offset;
        entry.ship.PilotLook = entry.look;
        entry.smoothedOffset = entry.offset;
        entry.smoothedLook = entry.look;
        entry.hasSmoothed = true;
        return entry.ship;
    }

    /// <summary>The authored SupportShip settings for a given car, read straight off the prefab ASSET
    /// (prefab references survive scene loads, and the asset is never stripped). Falls back to our own
    /// ship's settings, then to null — in which case the clone simply keeps its code defaults.</summary>
    static SupportShip TuningTemplateFor(string carName)
    {
        GameObject prefab = PlayerRegistry.CarPrefabFor(carName);
        if (prefab != null)
        {
            var template = SupportShipAbility.FindChildByName(prefab.transform, SupportShipAbility.ShipChildName);
            var authored = template != null ? template.GetComponent<SupportShip>() : null;
            if (authored != null) return authored;
        }

        var ability = SupportShipAbility.Instance;
        return ability != null ? ability.Ship : null;
    }

    static void SendToRemoteClients(string messageName, FastBufferWriter writer, NetworkDelivery delivery,
                                    ulong excludeClientId = ulong.MaxValue)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        foreach (var id in nm.ConnectedClientsIds)
            if (id != nm.LocalClientId && id != excludeClientId)
                nm.CustomMessagingManager.SendNamedMessage(messageName, id, writer, delivery);
    }
}
