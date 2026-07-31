using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>What kind of entity a replicated NPC is — selects stream rate and puppet build.</summary>
public enum NpcKind : byte
{
    Drone = 0,        // drones AND challengers (same handling, different prefab): 15 Hz, solid puppet
    Boulder = 1,      // ballistic: 8 Hz, solid puppet with GRAVITY-aware extrapolation
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
    const string MsgState = "GNRC_NPC_STATE";     // {id, seq, pos, rot, linVel, angVel}
    const string MsgDespawn = "GNRC_NPC_DESPAWN"; // {id}
    const string MsgStrike = "GNRC_STRIKE";       // {point, height} — lightning, event-replicated
    const string MsgHit = "GNRC_NPC_HIT";         // server → victim: projectile hit YOUR car
    const string MsgBounty = "GNRC_BOUNTY";       // server → client: {credits} knockoff payout

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
        public Rigidbody rb;
        public float sendInterval;
        public float nextSend;
        public ushort seq;
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
            sendInterval = kind == NpcKind.Boulder ? 1f / 8f : kind == NpcKind.Projectile ? 1f / 20f : 1f / 15f,
            nextSend = 0f,
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
            entity.nextSend = Time.unscaledTime + entity.sendInterval;
            entity.seq++;

            using var writer = new FastBufferWriter(80, Allocator.Temp);
            writer.WriteValueSafe(entity.id);
            writer.WriteValueSafe(entity.seq);
            writer.WriteValueSafe(entity.go.transform.position);
            writer.WriteValueSafe(entity.go.transform.rotation);
            writer.WriteValueSafe(entity.rb != null ? entity.rb.linearVelocity : Vector3.zero);
            writer.WriteValueSafe(entity.rb != null ? entity.rb.angularVelocity : Vector3.zero);
            SendToRemoteClients(MsgState, writer, NetworkDelivery.Unreliable);
        }
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
        RemoteCarManager.StripPuppet(go, keepColliders: puppet.kind != NpcKind.Projectile);
        go.transform.SetParent(null, false);
        go.name = "Npc_" + puppet.prefabKey;
        if (!Mathf.Approximately(puppet.scale, 1f))
            go.transform.localScale = Vector3.one * puppet.scale;
        go.transform.SetPositionAndRotation(puppet.spawnPos, puppet.spawnRot);
        DontDestroyOnLoad(go);   // cleared by despawn/round-end, not scene lifecycle

        var sync = go.AddComponent<RemoteCarPuppet>();
        sync.projectGravity = puppet.kind == NpcKind.Boulder;
        go.SetActive(true);
        puppet.go = go;
    }

    void HandleState(ushort id, ushort seq, Vector3 pos, Quaternion rot, Vector3 linVel, Vector3 angVel)
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
        if (sync != null) sync.ApplyState(seq, pos, rot, linVel, angVel, 0);
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
            HandleState(id, seq, pos, rot, linVel, angVel);
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
        msg.UnregisterNamedMessageHandler(MsgDespawn);
        msg.UnregisterNamedMessageHandler(MsgStrike);
        msg.UnregisterNamedMessageHandler(MsgHit);
        msg.UnregisterNamedMessageHandler(MsgBounty);
    }

    static void SendToRemoteClients(string messageName, FastBufferWriter writer, NetworkDelivery delivery)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        foreach (var id in nm.ConnectedClientsIds)
            if (id != nm.LocalClientId)
                nm.CustomMessagingManager.SendNamedMessage(messageName, id, writer, delivery);
    }
}
