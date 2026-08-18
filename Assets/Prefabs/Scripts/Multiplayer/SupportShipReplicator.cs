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
    const string MsgShip = "GNRC_SHIP";         // owner  → all: {ownerId, active}
    const string MsgAim = "GNRC_SHIP_AIM";      // pilot  → all: {ownerId, offset(Vec3), look(Vec2)}
    const string MsgPilot = "GNRC_SHIP_PILOT";  // client → server (request) / server → all (verdict)
    const string MsgDown = "GNRC_SHIP_DOWN";    // any    → server (report)  / server → all (verdict)
    const string MsgFire = "GNRC_SHIP_FIRE";    // pilot  → server: fire owner X's guns, once

    // The aim stream is the only fast one, and only while somebody is actually flying.
    const float AimRate = 20f;
    const float HeartbeatRate = 2f;
    // Eases an incoming offset so a 20 Hz stream glides instead of stepping. Small — this is a slow,
    // deliberate slide inside a box a few tens of metres across, not a car at 600 mph.
    const float AimSmoothTau = 0.07f;

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
        public ulong pilotId = NoClient;
        public Vector3 offset;          // latest received (or locally flown) pilot offset
        public Vector3 smoothedOffset;
        public Vector2 look;            // aim angles: x = yaw, y = pitch (degrees)
        public Vector2 smoothedLook;
        public bool hasSmoothed;
        public SupportShip ship;        // our local visual, null until their puppet exists
        public bool warnedLayer;
    }
    private readonly Dictionary<ulong, ShipEntry> ships = new Dictionary<ulong, ShipEntry>();

    /// <summary>The ship WE are currently flying from the hub, or <see cref="NoClient"/>. Set only by
    /// the server's verdict, never optimistically — two hub players can reach for the same ship in the
    /// same frame and exactly one of them must get it.</summary>
    public static ulong LocalPilotOf { get; private set; } = NoClient;

    private float nextShipSend;
    private float nextAimSend;
    private bool lastSentActive;
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
        bool changed = !hasSentOnce || active != lastSentActive;

        if (!changed && Time.unscaledTime < nextShipSend) return;
        nextShipSend = Time.unscaledTime + 1f / HeartbeatRate;
        lastSentActive = active;
        hasSentOnce = true;

        // Record our OWN ship in the same table everyone else's lives in. Without this the host's
        // table has no entry for the host's ship, and ResolvePilotRequest — which runs on the host and
        // checks `active` before granting — would refuse every attempt to fly it.
        GetOrCreate(LocalClientId).active = active;

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(24, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(active);

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
        Vector2 look = ship.PilotLook;

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
            if (entry.active && entry.pilotId == NoClient) entry.pilotId = requesterId;
        }
        else if (entry.pilotId == requesterId)
        {
            entry.pilotId = NoClient;
        }

        BroadcastPilotVerdict(ownerId, entry.pilotId);
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
    /// in one place, and it is nil when the pilot happens to be the host.</summary>
    public static void RequestFire(ulong ownerId)
    {
        // No session (single-player, or the pad used solo for testing): there IS no host, so the only
        // copy of the world is this one. Fire it here.
        if (Msg == null) { FireOnAuthority(ownerId); return; }
        if (IsServer) { FireOnAuthority(ownerId); return; }

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(ownerId);
        Msg.SendNamedMessage(MsgFire, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Spawns the round off this machine's copy of that player's ship. `FireLaser` calls
    /// `NpcReplicator.Track`, which is a host-only no-op elsewhere, so the same call does the right
    /// thing whether this is a real host or a solo session.</summary>
    static void FireOnAuthority(ulong ownerId)
    {
        var ship = GetShip(ownerId);
        if (ship != null) ship.FireLaser();
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

            if (IsServer && ownerId != LocalClientId)
            {
                using var writer = new FastBufferWriter(24, Allocator.Temp);
                writer.WriteValueSafe(ownerId);
                writer.WriteValueSafe(active);
                SendToRemoteClients(MsgShip, writer, NetworkDelivery.ReliableSequenced, excludeClientId: ownerId);

                // A ship that has just been put away can't stay claimed — free THAT SHIP's controls so
                // the hub player isn't left steering nothing. Note this must NOT touch claims this
                // player holds on OTHER ships (see ReleaseClaimsOnShip).
                if (!active) ReleaseClaimsOnShip(ownerId);
            }

            if (ownerId != LocalClientId) GetOrCreate(ownerId).active = active;
        });

        msg.RegisterNamedMessageHandler(MsgAim, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong ownerId);
            reader.ReadValueSafe(out Vector3 offset);
            reader.ReadValueSafe(out Vector2 look);

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
            aimed.offset = offset;
            aimed.look = look;
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
            if (!IsServer) return;   // one-way: only the host acts on a trigger pull

            // Only whoever actually holds the controls may fire this ship. Cheap, and it means a
            // malformed or stale request can't have someone else's guns going off.
            if (PilotOf(ownerId) != sender) return;
            FireOnAuthority(ownerId);
        });
    }

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
                if (ship == null) { entry.hasSmoothed = false; continue; }
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

            // Ease the received values so a 20 Hz stream reads as a glide rather than a staircase.
            if (!entry.hasSmoothed)
            {
                entry.smoothedOffset = entry.offset;
                entry.smoothedLook = entry.look;
                entry.hasSmoothed = true;
            }
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(AimSmoothTau, 1e-4f));
            entry.smoothedOffset = Vector3.Lerp(entry.smoothedOffset, entry.offset, t);
            entry.smoothedLook = Vector2.Lerp(entry.smoothedLook, entry.look, t);
            ship.PilotOffset = entry.smoothedOffset;
            ship.PilotLook = entry.smoothedLook;
        }
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
            entry.hasSmoothed = false;
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
        entry.ship.detectCrashes = IsServer;
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
