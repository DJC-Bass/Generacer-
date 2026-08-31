using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>What kind of entity a replicated NPC is — selects stream rate and puppet build.</summary>
public enum NpcKind : byte
{
    Drone = 0,        // drones AND challengers (same handling, different prefab): 15 Hz, solid puppet
    Boulder = 1,      // ballistic: 20 Hz, solid puppet with ACCELERATION-aware extrapolation
    Projectile = 2,   // straight-line, host-authoritative hits: 20 Hz, collider-less visual
}

/// <summary>
/// Phase 5: HOST-simulated AI &amp; obstacles, streamed to clients. Added to the MultiplayerWorld
/// object at session begin. In multiplayer only the HOST runs the real sims (DroneCar, boulders,
/// projectiles — the spawners self-gate on <see cref="MultiplayerWorld.IsClientOnly"/>); every spawn
/// registers here via <see cref="Track"/> and this component streams typed state to the clients,
/// which render stripped puppets driven by the same <see cref="RemoteCarPuppet"/> extrapolation the
/// player cars use (drones are fast too; boulders add gravity to the projection). Lightning is
/// event-replicated (the host rolls each strike, clients instantiate the identical strike locally),
/// and fans are seed-deterministic so they never needed streaming.
///
/// Client puppets are found by PREFAB NAME: every spawner registers its prefab on every machine via
/// <see cref="RegisterPrefab"/> (scene references exist on all clients), so the spawn message only
/// carries the key. A spawn that arrives before its prefab is registered (track scene still loading)
/// parks as a record and is retried when its state updates arrive.
///
/// Also routes the host-authoritative per-player effects: projectile HITs (pop-up / drone-ending
/// game-over applied on the victim's own machine) and knockoff BOUNTIES (paid to whichever player's
/// car actually shoved the drone off).
/// </summary>
public class NpcReplicator : MonoBehaviour
{
    const string MsgSpawn = "GNRC_NPC_SPAWN";     // {id, kind, prefabKey, scale, pos, rot}
    const string MsgNpcSfx = "GNRC_NPC_SFX";      // server → all: {id, prefabKey, pos, kind} — a host-only sound
    const string MsgState = "GNRC_NPC_STATE";     // {id, seq, pos, rot, linVel, angVel}
    const string MsgDamage = "GNRC_NPC_DMG";      // {id, hitsTaken, maxHits} — event, not streamed
    const string MsgDespawn = "GNRC_NPC_DESPAWN"; // {id}
    const string MsgStrike = "GNRC_STRIKE";       // {point, height} — lightning, event-replicated
    const string MsgHit = "GNRC_NPC_HIT";         // server → victim: projectile hit YOUR car
    const string MsgBounty = "GNRC_BOUNTY";       // server → client: {credits} knockoff payout
    const string MsgShove = "GNRC_NPC_SHOVE";     // server → victim: a heavy NPC just rammed YOUR car

    public static NpcReplicator Instance { get; private set; }

    // ---- Prefab registry (populated on every machine by the scene spawners) ----
    private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

    /// <summary>Registers a prefab clients may need to build puppets from. Registering a shooter
    /// (drone car or drone plane) also registers its projectile prefab (clients have no other path
    /// to it).</summary>
    public static void RegisterPrefab(GameObject prefab)
    {
        if (prefab == null) return;
        prefabs[prefab.name] = prefab;
        var drone = prefab.GetComponent<DroneCar>();
        if (drone != null && drone.projectilePrefab != null)
            prefabs[drone.projectilePrefab.name] = drone.projectilePrefab;
        var plane = prefab.GetComponent<DronePlane>();
        if (plane != null && plane.projectilePrefab != null)
            prefabs[plane.projectilePrefab.name] = plane.projectilePrefab;
    }

    // ---- Host-side tracked entities ----
    private class HostEntity
    {
        public ushort id;
        public GameObject go;
        public string prefabKey;   // kept so a sound reported for this object can be tuned by its prefab
        public Rigidbody rb;
        public float baseInterval;
        public float nextSend;
        public ushort seq;

        // Rotation sampled at the previous send, so the host can MEASURE angular velocity.
        public Quaternion lastRot;
        public float lastRotTime;
        public bool hasRotSample;
    }
    private readonly List<HostEntity> hostEntities = new List<HostEntity>();
    private ushort nextEntityId;

    // ---- Client-side puppets ----
    private class ClientPuppet
    {
        public NpcKind kind;
        public string prefabKey;
        public float scale;
        public Vector3 spawnPos;
        public Quaternion spawnRot;
        public GameObject go;   // null until the prefab is known (late track-scene load)
    }
    private readonly Dictionary<ushort, ClientPuppet> clientPuppets = new Dictionary<ushort, ClientPuppet>();
    private GameObject stagingRoot;

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    void Awake() => Instance = this;

    void Start() => RegisterHandlers();

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UnregisterHandlers();
        ClearClientPuppets();
        if (stagingRoot != null) Destroy(stagingRoot);
        prefabs.Clear();
    }

    // -------------------------------------------------------
    //  HOST API
    // -------------------------------------------------------

    /// <summary>Host: starts replicating a freshly spawned entity. No-op on clients/single-player,
    /// so spawners can call it unconditionally right after Instantiate.</summary>
    public static void Track(GameObject go, NpcKind kind, GameObject sourcePrefab, float scale = 1f)
    {
        if (Instance == null || !IsServer || go == null || sourcePrefab == null) return;
        Instance.TrackInternal(go, kind, sourcePrefab.name, scale);
    }

    void TrackInternal(GameObject go, NpcKind kind, string prefabKey, float scale)
    {
        var entity = new HostEntity
        {
            id = ++nextEntityId,
            go = go,
            rb = go.GetComponent<Rigidbody>(),
            // 20 Hz for everything, scaled per send by how close the nearest player is (see
            // RelevanceScale). Boulders were on 8 Hz - the slowest rate in the game on its fastest
            // object - and drones on 15 Hz, which is worse still: a Giga plane chases at 450 m/s, so
            // 15 Hz left 30 m between updates. In both cases positionTau (0.12 s) was close to the send
            // interval, so the puppet's correction blend never converged before the next correction
            // landed: it lived permanently mid-catch-up, which is what the choppiness actually was.
            baseInterval = 1f / 20f,
            nextSend = 0f,
            prefabKey = prefabKey,
        };
        hostEntities.Add(entity);

        using var writer = new FastBufferWriter(256, Allocator.Temp);
        writer.WriteValueSafe(entity.id);
        writer.WriteValueSafe((byte)kind);
        writer.WriteValueSafe(prefabKey);
        writer.WriteValueSafe(scale);
        writer.WriteValueSafe(go.transform.position);
        writer.WriteValueSafe(go.transform.rotation);
        SendToRemoteClients(MsgSpawn, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Host: the lightning spawner rolled a strike — replicate it as an event (clients
    /// instantiate an identical strike; same point + column height = same hazard everywhere).</summary>
    public static void BroadcastStrike(Vector3 point, float height)
    {
        if (Instance == null || !IsServer) return;
        using var writer = new FastBufferWriter(32, Allocator.Temp);
        writer.WriteValueSafe(point);
        writer.WriteValueSafe(height);
        SendToRemoteClients(MsgStrike, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Host: a host-simulated projectile hit a REMOTE player's puppet — tell the victim's
    /// machine so the pop-up (or drone-ending game-over) lands on their real car.</summary>
    public static void SendHitToClient(ulong clientId)
    {
        if (Instance == null || !IsServer) return;
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        msg.SendNamedMessage(MsgHit, clientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Host: a heavy NPC (a boulder) rammed this client's car, and here is the velocity change
    /// the collision should give it.
    ///
    /// ⚠️ Boulder hits have NO damage model at all - BoulderObstacle never had a collision handler. The
    /// whole effect is momentum: the sim just lets a 1500-6000 kg rock arriving at up to 200 m/s do what
    /// physics does. That cannot cross the wire by itself, and the reason is worth remembering: on the
    /// host a client's car is a KINEMATIC puppet, so the boulder bounces off a body that cannot move and
    /// the client's real car never feels a thing. Boulders could hit the host and ONLY the host.
    ///
    /// Reliable, like the projectile hit: a dropped shove is a hit that simply never happened, and no
    /// level-triggered state heals it on a later tick.</summary>
    public static void SendShoveToClient(ulong clientId, Vector3 velocityChange)
    {
        if (Instance == null || !IsServer) return;
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize<Vector3>(), Allocator.Temp);
        writer.WriteValueSafe(velocityChange);
        msg.SendNamedMessage(MsgShove, clientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Victim side: apply the host's shove to our own car. A VelocityChange so it lands the same
    /// on every chassis - the host already folded the mass ratio in when it computed this.</summary>
    static void ApplyShoveToLocalPlayer(Vector3 velocityChange)
    {
        var car = PlayerRegistry.LocalCar;
        var rb = car != null ? car.GetComponent<Rigidbody>() : null;
        if (rb == null) return;
        rb.AddForce(velocityChange, ForceMode.VelocityChange);
    }

    /// <summary>Host: tell everyone a tracked NPC just took a hit, so their copy can flash — or, when
    /// the pool is reported spent, paint the wreck tint that says it went down.
    ///
    /// Sent as an EVENT rather than folded into the 15/20 Hz state stream: damage is rare and the
    /// stream is per-entity-per-tick, so a byte there would cost far more bandwidth than an occasional
    /// message. Reliable, because a missed flash cannot be recovered — there is no level-triggered
    /// state to heal it on the next tick the way the car-effect flags do.</summary>
    public static void SendNpcDamage(GameObject go, int hitsTaken, int maxHits)
    {
        if (Instance == null || !IsServer || go == null) return;

        ushort id = 0;
        bool found = false;
        foreach (var entity in Instance.hostEntities)
            if (entity.go == go) { id = entity.id; found = true; break; }
        if (!found) return;   // not replicated (single-player, or spawned before the session)

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(id);
        writer.WriteValueSafe((byte)Mathf.Clamp(hitsTaken, 0, 255));
        writer.WriteValueSafe((byte)Mathf.Clamp(maxHits, 1, 255));
        SendToRemoteClients(MsgDamage, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Client: flash our puppet of that NPC, or paint it as a wreck if the report says the
    /// pool is spent. The component is ADDED here because the puppet was stripped of every script on
    /// spawn — and its tuning is copied off the registered prefab, so a client's damage colours match
    /// the host's instead of falling back to code defaults.</summary>
    void ApplyNpcDamage(ushort id, int hitsTaken, int maxHits)
    {
        if (!clientPuppets.TryGetValue(id, out var puppet) || puppet.go == null) return;

        prefabs.TryGetValue(puppet.prefabKey, out var prefab);

        var tint = puppet.go.GetComponent<DroneDamageTint>();
        if (tint == null)
        {
            tint = puppet.go.AddComponent<DroneDamageTint>();
            if (prefab != null)
                tint.CopyTuningFrom(prefab.GetComponentInChildren<DroneDamageTint>(true));
        }
        tint.RegisterHit(hitsTaken, maxHits);

        // ⚠️ The matching half of DronePlane.ShowDamage / ShowDowned. The plane only exists on the host,
        // so without this a client watching one break up watched it in silence - and the gunner shooting
        // it is very often the client. Same event, same split: the pool spent is a KILL, anything less
        // is a hit, exactly as DroneDamageTint reads it.
        //
        // Keyed on the prefab actually being a plane so a future damage report from another NPC kind
        // cannot borrow these sounds. Falloff comes off that prefab: a GigaPlus carries further.
        var plane = prefab != null ? prefab.GetComponent<DronePlane>() : null;
        if (plane == null) return;

        Vector3 at = puppet.go.transform.position;
        if (hitsTaken >= Mathf.Max(1, maxHits)) AudioManager.PlayDronePlaneDestroyed(at, plane.audio3D);
        else AudioManager.PlayDronePlaneHit(at, plane.audio3D);
    }

    /// <summary>Gives a boulder puppet its noise back.
    ///
    /// ⚠️ StripPuppet destroys every MonoBehaviour AND every AudioSource on a puppet, so anything a
    /// host-simulated object would have played FOR ITSELF is silent on every client. Boulders were:
    /// the host heard the spawn crack, the burning flight loop and the impact; clients heard an
    /// utterly silent rock. `RemoteCarAudio` exists for exactly this reason on cars, and this is the
    /// boulder equivalent — re-added rather than relayed, because the FLIGHT LOOP has to ride the
    /// moving puppet and a one-shot event could never reproduce it.
    ///
    /// BoulderAudio is safe to re-add because it is self-contained: it makes its own AudioSources, reads
    /// its clips from AudioLibrary, and does its own collision test rather than asking BoulderObstacle.
    ///
    /// KNOWN GAP: the IMPACT one-shot needs a collision, and a puppet's Rigidbody is kinematic — which
    /// raises no contact against STATIC geometry like the track. A boulder landing on the road is
    /// therefore still silent on clients, while one that hits the local player's (dynamic) car is not.
    /// Fixing that properly means relaying the impact as an event, the way GNRC_SHIP_SFX relays the
    /// laser's.</summary>
    static void RestoreBoulderAudio(GameObject go, GameObject prefab, NpcKind kind)
    {
        if (kind != NpcKind.Boulder || go == null) return;

        var audio = go.AddComponent<BoulderAudio>();
        if (prefab != null) audio.CopyTuningFrom(prefab.GetComponentInChildren<BoulderAudio>(true));
        // The puppet must NOT judge its own impacts. Its Rigidbody is kinematic, so it feels nothing
        // against the static track (where most boulders land) but DOES feel the local player's dynamic
        // car — it would miss the common case and double up on the rare one. The host relays instead.
        audio.impactsFromNetwork = true;
    }

    /// <summary>Which host-simulated one-shot to play. The player-vs-environment split matters: they
    /// are different clips, and "that hit somebody" is real information to everyone nearby.</summary>
    public enum NpcSound : byte { DroneShoot = 0, HitEnvironment = 1, HitPlayer = 2, BoulderImpact = 3 }

    /// <summary>HOST → everyone: play one of the drones' sounds at a world position.
    ///
    /// ⚠️ Necessary for the same reason the Support Ship's guns needed GNRC_SHIP_SFX and boulders
    /// needed their audio re-added: DRONES AND THEIR PROJECTILES ONLY EXIST ON THE HOST. Clients get
    /// puppets, and StripPuppet destroys every MonoBehaviour, AudioSource and collider on one — so no
    /// client ever ran the code that plays a drone's shot or its impact. A whole track's worth of
    /// incoming fire was completely silent to everyone but the host.
    ///
    /// <paramref name="prefabKey"/> picks the 3D tuning on the far side: the projectile VARIANTS (Big,
    /// Giga) carry their own falloff, and a Giga round heard at the plain one's range would be wrong.
    ///
    /// <paramref name="excludeClientId"/> is for the player who was HIT — they already play the impact
    /// locally off GNRC_NPC_HIT, immediately and with no round trip, so relaying it to them as well
    /// would be an audible double-tap. Unreliable: a lost gunshot is a missing tick, not broken state.</summary>
    public static void ReportNpcSound(string prefabKey, Vector3 position, NpcSound kind,
                                      ulong excludeClientId = ulong.MaxValue, ushort entityId = 0)
    {
        if (Instance == null || !IsServer || string.IsNullOrEmpty(prefabKey)) return;
        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(128, Allocator.Temp);
        writer.WriteValueSafe(entityId);
        writer.WriteValueSafe(prefabKey);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe((byte)kind);
        SendToRemoteClients(MsgNpcSfx, writer, NetworkDelivery.Unreliable, excludeClientId);
    }

    /// <summary>As above, for an object we have TRACKED — its prefab key is already on file, which a
    /// projectile in mid-collision has no other way of knowing about itself.</summary>
    public static void ReportNpcSound(GameObject tracked, Vector3 position, NpcSound kind,
                                      ulong excludeClientId = ulong.MaxValue)
    {
        if (Instance == null || !IsServer || tracked == null) return;
        foreach (var entity in Instance.hostEntities)
            if (entity.go == tracked)
            {
                // The id lets the far side act on the PUPPET itself, not just play a sound near it —
                // a landing boulder has a flight loop that must stop too.
                ReportNpcSound(entity.prefabKey, position, kind, excludeClientId, entity.id);
                return;
            }
    }

    /// <summary>Client: play a sound the host told us about, with the firing prefab's own 3D tuning.</summary>
    void PlayNpcSound(ushort entityId, string prefabKey, Vector3 position, NpcSound kind)
    {
        // If we still hold the puppet, let IT make the noise: the sound lands exactly on the object,
        // carries the tuning it was built with, and the object gets to react (a boulder cuts its loop).
        if (kind == NpcSound.BoulderImpact && entityId != 0
            && clientPuppets.TryGetValue(entityId, out var hit) && hit.go != null)
        {
            var boulder = hit.go.GetComponent<BoulderAudio>();
            if (boulder != null) { boulder.PlayNetworkImpact(); return; }
        }

        Spatial3DSettings tuning = null;
        prefabs.TryGetValue(prefabKey, out var prefab);
        if (prefab != null)
        {
            // Whichever component authored the falloff for this prefab. A Giga round heard at the plain
            // one's range would be wrong, and a boulder's carry is different again.
            var projectile = prefab.GetComponent<DroneProjectile>();
            if (projectile != null) tuning = projectile.audio3D;
            else
            {
                var boulder = prefab.GetComponentInChildren<BoulderAudio>(true);
                if (boulder != null) tuning = boulder.spatial;
            }
        }

        switch (kind)
        {
            case NpcSound.DroneShoot:  AudioManager.PlayDroneShoot(position, tuning); break;
            case NpcSound.HitPlayer:   AudioManager.PlayProjectileHitPlayer(position, tuning); break;
            case NpcSound.BoulderImpact: AudioManager.PlayBoulderImpact(position, tuning); break;
            default:                   AudioManager.PlayProjectileHitEnvironment(position, tuning); break;
        }
    }

    /// <summary>Host: pay a knockoff bounty to the client whose car shoved the drone off.</summary>
    public static void SendBounty(ulong clientId, int credits)
    {
        if (Instance == null || !IsServer) return;
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(credits);
        msg.SendNamedMessage(MsgBounty, clientId, writer, NetworkDelivery.ReliableSequenced);
    }

    /// <summary>Round over (or teardown): drop every client-side NPC puppet. Host entities die with
    /// the track scene and broadcast their own despawns as they go.</summary>
    public static void ClearRoundPuppets() => Instance?.ClearClientPuppets();

    void Update()
    {
        if (!IsServer) return;

        // Stream states; reap destroyed entities (scene unload, kill floor, lifetime) as despawns.
        for (int i = hostEntities.Count - 1; i >= 0; i--)
        {
            var entity = hostEntities[i];
            if (entity.go == null)
            {
                BroadcastDespawn(entity.id);
                hostEntities.RemoveAt(i);
                continue;
            }
            if (Time.unscaledTime < entity.nextSend) continue;
            entity.nextSend = Time.unscaledTime + entity.baseInterval * RelevanceScale(entity.go.transform.position);
            entity.seq++;

            // ⚠️ rb.POSITION, not transform.position. Every NPC prefab is set to Interpolate, so during
            // Update the TRANSFORM holds the interpolated RENDER pose while linearVelocity holds the
            // current PHYSICS value. Pairing them dead-reckons from two different instants, an error of
            // up to v x fixedDeltaTime - 4 m on a 200 m/s boulder - that oscillates with the render /
            // physics phase and therefore reads as jitter. rb.position samples both from one state.
            var body = entity.rb;
            Vector3 sendPos = body != null ? body.position : entity.go.transform.position;
            Quaternion sendRot = body != null ? body.rotation : entity.go.transform.rotation;

            using var writer = new FastBufferWriter(80, Allocator.Temp);
            writer.WriteValueSafe(entity.id);
            writer.WriteValueSafe(entity.seq);
            writer.WriteValueSafe(sendPos);
            writer.WriteValueSafe(sendRot);
            writer.WriteValueSafe(body != null ? body.linearVelocity : Vector3.zero);
            writer.WriteValueSafe(AngularVelocityFor(entity, sendRot));
            // One byte: is the sender WEIGHTLESS right now? A homing boulder switches its own gravity
            // off and thrusts instead, so a puppet projecting a ballistic arc predicts a fall that is
            // not happening. Reading useGravity keeps this general - no boulder-specific branch here.
            writer.WriteValueSafe((byte)(body != null && !body.useGravity ? 1 : 0));
            SendToRemoteClients(MsgState, writer, NetworkDelivery.Unreliable);
        }
    }

    /// <summary>The angular velocity to replicate: the Rigidbody's own when it has one, otherwise the
    /// rate its rotation is ACTUALLY changing, measured between sends.
    ///
    /// ⚠️ Drone planes and drone cars steer with <c>rb.MoveRotation(Slerp(...))</c> on a DYNAMIC body and
    /// never apply torque, so `rb.angularVelocity` sits at ~0 no matter how hard they bank. The host was
    /// faithfully replicating that zero, `RemoteCarPuppet.IntegrateRotation` early-outed on it, and the
    /// puppet had NOTHING to lead the nose with - it could only Slerp toward the last received rotation
    /// at rotationTau. The heading was permanently ~tau behind and stepped at the send rate, which on a
    /// plane banking through a chase is very visible. Boulders never showed it because their spin IS
    /// genuine angular velocity.
    ///
    /// Measuring closes that without asking the AI to report anything: rotation is rotation, however it
    /// was produced. The real value is preferred when present, because a measurement taken one send
    /// apart would alias on anything spinning more than half a turn between sends.</summary>
    static Vector3 AngularVelocityFor(HostEntity entity, Quaternion rotation)
    {
        var body = entity.rb;
        Vector3 actual = body != null ? body.angularVelocity : Vector3.zero;

        float now = Time.unscaledTime;
        Vector3 measured = Vector3.zero;
        if (entity.hasRotSample)
        {
            float dt = now - entity.lastRotTime;
            if (dt > 1e-4f)
            {
                (rotation * Quaternion.Inverse(entity.lastRot)).ToAngleAxis(out float degrees, out Vector3 axis);
                if (degrees > 180f) degrees -= 360f;   // shortest way round
                if (!float.IsNaN(degrees) && axis.sqrMagnitude > 1e-6f)
                    measured = axis.normalized * (degrees * Mathf.Deg2Rad / dt);
            }
        }
        entity.lastRot = rotation;
        entity.lastRotTime = now;
        entity.hasRotSample = true;

        // A hair above float noise: anything genuinely torque-driven clears this easily.
        return actual.sqrMagnitude > 1e-4f ? actual : measured;
    }

    // ---- Relevance: spend the rate where somebody can actually see it ----
    //
    // ⚠️ The counts here are the whole reason this exists. The prefabs spawn 150 drone planes, ~64 drone
    // cars and up to ~60 live boulders: ~274 entities, each streamed as its OWN named message. At the
    // old flat rates that was ~4,400 messages a second to EVERY client (~340 KB/s each, and the host
    // pays that per client). Simply raising the rate - the obvious answer to "planes look choppy" -
    // would have multiplied a figure that is already past what a home upstream can carry, and a
    // saturated link produces exactly the stutter it was meant to cure.
    //
    // So the rate is spent by proximity instead: the handful of entities near a player get MORE than
    // they used to, and the long tail nobody is looking at gets far less. Distances are generous - a
    // plane's own vision range is 800 - and the far band still trickles, so a puppet is never left with
    // nothing to correct against.
    const float NearRange = 800f;     // 30 Hz — close enough to be looked at
    const float MidRange = 2500f;     // 20 Hz — the old flat rate for everything
    const float FarRange = 6000f;     // ~7 Hz — visible as scenery, not as a threat

    private readonly List<Vector3> playerPositions = new List<Vector3>();
    private int playerPositionsFrame = -1;

    /// <summary>Where every player is this frame, cached because the send loop asks once per entity.</summary>
    List<Vector3> PlayerPositions()
    {
        if (playerPositionsFrame == Time.frameCount) return playerPositions;
        playerPositionsFrame = Time.frameCount;
        playerPositions.Clear();

        var local = PlayerRegistry.LocalCar;
        if (local != null) playerPositions.Add(local.transform.position);
        foreach (var remote in PlayerRegistry.Remotes)
            if (remote.Car != null) playerPositions.Add(remote.Car.transform.position);
        return playerPositions;
    }

    /// <summary>Multiplier on an entity's base send interval, from its distance to the NEAREST player.
    /// Below 1 means faster than the base rate.</summary>
    float RelevanceScale(Vector3 position)
    {
        var players = PlayerPositions();
        if (players.Count == 0) return 4f;   // nobody racing — idle along

        float nearestSq = float.MaxValue;
        foreach (var p in players)
        {
            float d = (p - position).sqrMagnitude;
            if (d < nearestSq) nearestSq = d;
        }

        if (nearestSq <= NearRange * NearRange) return 2f / 3f;   // 30 Hz
        if (nearestSq <= MidRange * MidRange) return 1f;          // 20 Hz
        if (nearestSq <= FarRange * FarRange) return 3f;          // ~7 Hz
        return 10f;                                               // 2 Hz
    }

    void BroadcastDespawn(ushort id)
    {
        using var writer = new FastBufferWriter(sizeof(ushort), Allocator.Temp);
        writer.WriteValueSafe(id);
        SendToRemoteClients(MsgDespawn, writer, NetworkDelivery.ReliableSequenced);
    }

    // -------------------------------------------------------
    //  CLIENT side
    // -------------------------------------------------------

    void HandleSpawn(ushort id, NpcKind kind, string prefabKey, float scale, Vector3 pos, Quaternion rot)
    {
        var puppet = new ClientPuppet
        {
            kind = kind, prefabKey = prefabKey, scale = scale, spawnPos = pos, spawnRot = rot,
        };
        clientPuppets[id] = puppet;

        // A boulder's spawn position is a half-buried point on the ground (the launch origin, often
        // inside track scenery). Building the puppet there would render it stuck half-buried until the
        // first state lands. Defer boulders to the first STATE — it carries the launch velocity, so the
        // puppet appears already in flight (RemoteCarPuppet snaps gravity puppets to their projection).
        // Drones/projectiles spawn in the open and moving, so they build immediately.
        if (kind != NpcKind.Boulder) TryCreatePuppet(puppet);
    }

    void TryCreatePuppet(ClientPuppet puppet)
    {
        if (puppet.go != null) return;
        if (!prefabs.TryGetValue(puppet.prefabKey, out var prefab) || prefab == null) return;   // retried on state

        if (stagingRoot == null)
        {
            stagingRoot = new GameObject("NpcPuppetStaging");
            stagingRoot.SetActive(false);
            DontDestroyOnLoad(stagingRoot);
        }

        var go = Instantiate(prefab, stagingRoot.transform);
        // Projectile hits are host-authoritative — its puppet is a pure visual. Drones/boulders keep
        // their colliders (kinematic) so they physically shove the local car exactly like the sim.
        // keepAmbientVfx: an NPC's particles are part of what it IS (the lava boulder's burning trail),
        // not a conditional flourish some destroyed script would have switched on.
        RemoteCarManager.StripPuppet(go, keepColliders: puppet.kind != NpcKind.Projectile,
                                     keepAmbientVfx: true);
        go.transform.SetParent(null, false);
        go.name = "Npc_" + puppet.prefabKey;
        if (!Mathf.Approximately(puppet.scale, 1f))
            go.transform.localScale = Vector3.one * puppet.scale;
        go.transform.SetPositionAndRotation(puppet.spawnPos, puppet.spawnRot);
        DontDestroyOnLoad(go);   // cleared by despawn/round-end, not scene lifecycle

        var sync = go.AddComponent<RemoteCarPuppet>();
        ConfigurePuppetMotion(sync, prefab, puppet.kind);
        RestoreBoulderAudio(go, prefab, puppet.kind);
        go.SetActive(true);
        puppet.go = go;
    }

    /// <summary>Sets up how a puppet PREDICTS and how it MOVES - the two things that decide whether it
    /// looks right and whether it can hit anything.
    ///
    /// The projected acceleration is read off the PREFAB rather than hard-coded to gravity: a boulder
    /// carries <c>gravityMultiplier</c> (3 by default), so the real thing falls at ~29 m/s^2 while the
    /// puppet used to predict 9.81. Predicting the wrong acceleration means every packet lands a
    /// correction, which at close range is exactly the choppiness this was reported as.
    ///
    /// MovePosition is for puppets that must SHOVE the local car. Boulders are deliberately excluded -
    /// their hits are host-authoritative now (see <see cref="SendShoveToClient"/>), and letting the
    /// puppet shove as well would both double-count and overshoot, since a kinematic body pushes with
    /// INFINITE mass while the real boulder has a finite one the host actually simulated.</summary>
    static void ConfigurePuppetMotion(RemoteCarPuppet sync, GameObject prefab, NpcKind kind)
    {
        if (kind == NpcKind.Boulder)
        {
            var boulder = prefab != null ? prefab.GetComponent<BoulderObstacle>() : null;
            float g = boulder != null ? Mathf.Max(1f, boulder.gravityMultiplier) : 1f;
            sync.projectAcceleration = Physics.gravity * g;
        }

        // Drones keep their colliders precisely so they can barge the local car about; without this
        // they only ever depenetrated it. Projectiles have no colliders and boulders relay their hits.
        sync.moveByPhysics = kind == NpcKind.Drone;
    }

    void HandleState(ushort id, ushort seq, Vector3 pos, Quaternion rot, Vector3 linVel, Vector3 angVel,
                     bool weightless)
    {
        if (!clientPuppets.TryGetValue(id, out var puppet)) return;
        if (puppet.go == null)
        {
            puppet.spawnPos = pos;
            puppet.spawnRot = rot;
            TryCreatePuppet(puppet);   // prefab may have registered since the spawn message
            if (puppet.go == null) return;
        }
        var sync = puppet.go.GetComponent<RemoteCarPuppet>();
        // NPCs (drones/boulders/projectiles) carry no player effect flags — 0, and they have no
        // RemoteCarEffects component to consume them anyway.
        if (sync != null) sync.ApplyState(seq, pos, rot, linVel, angVel, 0, weightless: weightless);
    }

    void HandleDespawn(ushort id)
    {
        if (!clientPuppets.TryGetValue(id, out var puppet)) return;
        if (puppet.go != null) Destroy(puppet.go);
        clientPuppets.Remove(id);
    }

    void ClearClientPuppets()
    {
        foreach (var puppet in clientPuppets.Values)
            if (puppet.go != null) Destroy(puppet.go);
        clientPuppets.Clear();
    }

    void HandleStrike(Vector3 point, float height)
    {
        var spawner = FindAnyObjectByType<LightningSpawner>();
        if (spawner != null) spawner.SpawnStrikeAt(point, height);
    }

    // -------------------------------------------------------
    //  Messaging plumbing
    // -------------------------------------------------------

    void RegisterHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[NpcReplicator] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgSpawn, (sender, reader) =>
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out string prefabKey);
            reader.ReadValueSafe(out float scale);
            reader.ReadValueSafe(out Vector3 pos);
            reader.ReadValueSafe(out Quaternion rot);
            HandleSpawn(id, (NpcKind)kind, prefabKey, scale, pos, rot);
        });
        msg.RegisterNamedMessageHandler(MsgState, (sender, reader) =>
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out ushort seq);
            reader.ReadValueSafe(out Vector3 pos);
            reader.ReadValueSafe(out Quaternion rot);
            reader.ReadValueSafe(out Vector3 linVel);
            reader.ReadValueSafe(out Vector3 angVel);
            reader.ReadValueSafe(out byte flags);
            HandleState(id, seq, pos, rot, linVel, angVel, (flags & 1) != 0);
        });

        msg.RegisterNamedMessageHandler(MsgNpcSfx, (sender, reader) =>
        {
            reader.ReadValueSafe(out ushort entityId);
            reader.ReadValueSafe(out string prefabKey);
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out byte kind);
            PlayNpcSound(entityId, prefabKey, position, (NpcSound)kind);
        });

        msg.RegisterNamedMessageHandler(MsgDamage, (sender, reader) =>
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out byte hitsTaken);
            reader.ReadValueSafe(out byte maxHits);
            ApplyNpcDamage(id, hitsTaken, maxHits);
        });
        msg.RegisterNamedMessageHandler(MsgDespawn, (sender, reader) =>
        {
            reader.ReadValueSafe(out ushort id);
            HandleDespawn(id);
        });
        msg.RegisterNamedMessageHandler(MsgStrike, (sender, reader) =>
        {
            reader.ReadValueSafe(out Vector3 point);
            reader.ReadValueSafe(out float height);
            HandleStrike(point, height);
        });
        msg.RegisterNamedMessageHandler(MsgHit, (sender, reader) =>
        {
            reader.ReadValueSafe(out byte _);
            DroneProjectile.ApplyRemoteHitToLocalPlayer();
        });
        msg.RegisterNamedMessageHandler(MsgShove, (sender, reader) =>
        {
            reader.ReadValueSafe(out Vector3 velocityChange);
            ApplyShoveToLocalPlayer(velocityChange);
        });
        msg.RegisterNamedMessageHandler(MsgBounty, (sender, reader) =>
        {
            reader.ReadValueSafe(out int credits);
            if (PlayerInventory.Instance != null)
            {
                PlayerInventory.Instance.AddCredits(credits);
                AudioManager.PlayKnockoffBounty();
                Debug.Log($"[NpcReplicator] Knocked a drone off — bounty {credits} credits.");
            }
        });
    }

    void UnregisterHandlers()
    {
        var msg = Msg;
        if (msg == null) return;
        msg.UnregisterNamedMessageHandler(MsgSpawn);
        msg.UnregisterNamedMessageHandler(MsgState);
        msg.UnregisterNamedMessageHandler(MsgDamage);
        msg.UnregisterNamedMessageHandler(MsgNpcSfx);
        msg.UnregisterNamedMessageHandler(MsgDespawn);
        msg.UnregisterNamedMessageHandler(MsgStrike);
        msg.UnregisterNamedMessageHandler(MsgHit);
        msg.UnregisterNamedMessageHandler(MsgShove);
        msg.UnregisterNamedMessageHandler(MsgBounty);
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
