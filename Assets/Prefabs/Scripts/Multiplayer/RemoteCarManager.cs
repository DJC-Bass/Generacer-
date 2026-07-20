using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Phase 3: makes the other five players VISIBLE. Added to the MultiplayerWorld object at session
/// begin (and torn down with it).
///
/// Architecture note — deliberately NO NetworkObject player prefab. Each client simulates only its
/// OWN car (the plain local car the game already spawns; CarController/cameras/SD/input therefore
/// exist only for the owner, which resolves the handoff's "local-only camera swivel" warning
/// structurally — there is no remote instance of any of those components to gate). Remote players
/// are LOCAL PUPPETS: stripped visual clones of the right car prefab (from PlayerRegistry's
/// catalog), driven purely by <see cref="RemoteCarPuppet"/> extrapolation from a 30 Hz UNRELIABLE
/// state stream relayed through the host. This keeps Phase 3 free of editor prefab work (no
/// NetworkObject on every selectable car, no NetworkPrefabs list, no ownership juggling when
/// PlayerCarSwapper destroys/respawns cars) at the cost of NGO conveniences we don't need yet.
///
/// Wire protocol (on the existing custom-named-message layer):
///  • HELLO  (client → host, once the local car exists): {name, carName, team}.
///  • ROSTER (host → all, on every hello AND on a disconnect): full {clientId, name, carName, team}
///    list. Receivers diff it: create puppets for new remotes, destroy for departed.
///  • CAR    (owner → host → other clients, 30 Hz, Unreliable): {clientId, seq, pos, rot, linVel,
///    angVel}. The host applies + relays; puppets consume via RemoteCarPuppet.ApplyState.
/// Puppets are NOT tagged "Player" and have no colliders — they can't be grabbed by
/// PlayerRegistry.LocalCar, can't fire portals/kill floors, and can't physically shove anyone
/// (deliberate: 600 mph contact through 100 ms of latency is not a fight worth having in v1).
/// </summary>
public class RemoteCarManager : MonoBehaviour
{
    const string MsgHello = "GNRC_HELLO";
    const string MsgRoster = "GNRC_ROSTER";
    const string MsgCar = "GNRC_CAR";

    const float SendRate = 30f;
    /// <summary>Puppets park here until their first state lands (out of sight, out of physics).</summary>
    static readonly Vector3 HiddenPose = new Vector3(0f, -10000f, 0f);

    private bool helloSent;
    private float nextSendTime;
    private ushort sendSequence;
    private GameObject stagingRoot;   // inactive parent: puppet Awakes are deferred until after the strip

    // Host bookkeeping: the authoritative roster it rebroadcasts.
    private readonly Dictionary<ulong, (string name, string car, int team)> hostRoster =
        new Dictionary<ulong, (string, string, int)>();

    /// <summary>Server-side: which team a client declared in its HELLO (1/2; 0 = unknown/not yet
    /// helloed). MultiplayerScoring aggregates team SD sets and validates awards with this.</summary>
    public int TeamOfServer(ulong clientId) =>
        hostRoster.TryGetValue(clientId, out var entry) ? entry.team : 0;

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    void Start()
    {
        RegisterHandlers();
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnDestroy()
    {
        UnregisterHandlers();
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        PlayerRegistry.ClearRemotes();
        if (stagingRoot != null) Destroy(stagingRoot);
    }

    void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return;

        var car = PlayerRegistry.LocalCar;
        if (car == null) return;   // no car yet (menu → hub transition)

        // One-shot introduction once we exist in the world.
        if (!helloSent)
        {
            helloSent = true;
            SendHello();
        }

        // The 30 Hz owner state stream.
        if (Time.unscaledTime >= nextSendTime)
        {
            nextSendTime = Time.unscaledTime + 1f / SendRate;
            SendCarState(car);
        }
    }

    // -------------------------------------------------------
    //  Roster (who exists, what car, what team)
    // -------------------------------------------------------

    void SendHello()
    {
        string name = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalPlayerName : "PLAYER";
        string carName = SelectedCarStore.Instance != null ? SelectedCarStore.Instance.SelectedCarName ?? "" : "";
        int team = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;

        if (IsServer)
        {
            HandleHello(LocalClientId, name, carName, team);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(256, Allocator.Temp);
        writer.WriteValueSafe(name);
        writer.WriteValueSafe(carName);
        writer.WriteValueSafe(team);
        msg.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    void HandleHello(ulong clientId, string name, string carName, int team)
    {
        hostRoster[clientId] = (name, carName, team);
        BroadcastRoster();
    }

    void BroadcastRoster()
    {
        using (var writer = new FastBufferWriter(1024, Allocator.Temp, 65536))
        {
            writer.WriteValueSafe(hostRoster.Count);
            foreach (var kv in hostRoster)
            {
                writer.WriteValueSafe(kv.Key);
                writer.WriteValueSafe(kv.Value.name);
                writer.WriteValueSafe(kv.Value.car);
                writer.WriteValueSafe(kv.Value.team);
            }
            SendToRemoteClients(MsgRoster, writer, NetworkDelivery.ReliableSequenced);
        }
        ApplyRosterLocally();
    }

    void ApplyRosterLocally()
    {
        var entries = new List<(ulong id, string name, string car, int team)>();
        foreach (var kv in hostRoster)
            entries.Add((kv.Key, kv.Value.name, kv.Value.car, kv.Value.team));
        ApplyRoster(entries);
    }

    void ApplyRoster(List<(ulong id, string name, string car, int team)> entries)
    {
        // Additions/updates (skip ourselves — our car is the real one).
        var seen = new HashSet<ulong>();
        foreach (var (id, name, car, team) in entries)
        {
            seen.Add(id);
            if (id == LocalClientId) continue;
            var entry = PlayerRegistry.AddOrUpdateRemote(id, name, car, team);
            if (entry.Car == null)
            {
                entry.Car = CreatePuppet(name, car);
                Debug.Log($"[RemoteCars] Puppet created for {name} ({car}, team {team}).");
            }
        }

        // Departures.
        var stale = new List<ulong>();
        foreach (var remote in PlayerRegistry.Remotes)
            if (!seen.Contains(remote.ClientId)) stale.Add(remote.ClientId);
        foreach (var id in stale) PlayerRegistry.RemoveRemote(id);

        RefreshMarkers();
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;
        if (hostRoster.Remove(clientId)) BroadcastRoster();
    }

    // -------------------------------------------------------
    //  Owner state stream
    // -------------------------------------------------------

    void SendCarState(GameObject car)
    {
        var msg = Msg;
        if (msg == null) return;

        var rb = car.GetComponent<Rigidbody>();
        Vector3 linVel = rb != null ? rb.linearVelocity : Vector3.zero;
        Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;
        sendSequence++;

        using var writer = new FastBufferWriter(80, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(sendSequence);
        writer.WriteValueSafe(car.transform.position);
        writer.WriteValueSafe(car.transform.rotation);
        writer.WriteValueSafe(linVel);
        writer.WriteValueSafe(angVel);

        if (IsServer) SendToRemoteClients(MsgCar, writer, NetworkDelivery.Unreliable);
        else msg.SendNamedMessage(MsgCar, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
    }

    void HandleCarState(ulong senderId, ushort sequence, Vector3 pos, Quaternion rot,
                        Vector3 linVel, Vector3 angVel)
    {
        // Host: relay every client's stream to the other clients (senderId travels in the payload).
        if (IsServer && senderId != LocalClientId)
        {
            using var writer = new FastBufferWriter(80, Allocator.Temp);
            writer.WriteValueSafe(senderId);
            writer.WriteValueSafe(sequence);
            writer.WriteValueSafe(pos);
            writer.WriteValueSafe(rot);
            writer.WriteValueSafe(linVel);
            writer.WriteValueSafe(angVel);
            SendToRemoteClients(MsgCar, writer, NetworkDelivery.Unreliable, excludeClientId: senderId);
        }

        if (senderId == LocalClientId) return;   // our own relayed echo — nothing to drive

        var remote = PlayerRegistry.FindRemote(senderId);
        if (remote == null || remote.Car == null) return;   // roster hasn't landed yet
        var puppet = remote.Car.GetComponent<RemoteCarPuppet>();
        if (puppet != null) puppet.ApplyState(sequence, pos, rot, linVel, angVel);
    }

    // -------------------------------------------------------
    //  Puppet construction
    // -------------------------------------------------------

    GameObject CreatePuppet(string playerName, string carName)
    {
        var prefab = PlayerRegistry.CarPrefabFor(carName);
        GameObject go;

        if (prefab != null)
        {
            // Instantiate under an INACTIVE parent so no component Awake/OnEnable runs (the prefab
            // is full of owner-only scripts that would grab input/audio/the GameLoop), strip while
            // dormant, then release.
            if (stagingRoot == null)
            {
                stagingRoot = new GameObject("RemoteCarStaging");
                stagingRoot.SetActive(false);
                DontDestroyOnLoad(stagingRoot);
            }
            go = Instantiate(prefab, stagingRoot.transform);
            StripPuppet(go, keepColliders: true);   // SOLID puppet: players can bump each other
            go.transform.SetParent(null, false);
        }
        else
        {
            Debug.LogWarning("[RemoteCars] Car catalog empty — remote player shown as a placeholder cube. " +
                             "Fill MainMenuController.multiplayerCars.");
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = new Vector3(2f, 1.5f, 4.5f);
        }

        go.name = "RemoteCar_" + playerName;
        // NOT "Player" (tag lookups, portals, kill floors must never grab a puppet) — but a real tag
        // of its own so drones/projectiles/boulders can recognise remote players as players.
        go.tag = "RemotePlayer";
        go.transform.SetPositionAndRotation(HiddenPose, Quaternion.identity);
        DontDestroyOnLoad(go); // lives across the hub/track areas; destroyed via the roster/teardown
        go.AddComponent<RemoteCarPuppet>();

        // Phase 6: this car's own engine sound, driven by replicated speed (tuning copied off the
        // prefab ASSET's CarEngineAudio — the puppet's instance was stripped), and the
        // teammate/rival marker (labelled per-viewer by RefreshMarkers).
        if (prefab != null)
        {
            var engineTemplate = prefab.GetComponentInChildren<CarEngineAudio>(true);
            if (engineTemplate != null) go.AddComponent<RemoteCarAudio>().Configure(engineTemplate);
        }
        go.AddComponent<RemoteCarMarker>();

        go.SetActive(true);
        return go;
    }

    /// <summary>Re-labels every remote car for THIS viewer: "RIVAL" on the one opposing player the
    /// rival system assigned to us, "TEAMMATE" on our own team's cars, nothing on the rest. Called
    /// on roster changes and whenever the rival map updates — rival markers are inherently private
    /// because each machine only ever labels its own rival.</summary>
    public void RefreshMarkers()
    {
        int localTeam = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;
        bool hasRival = MultiplayerScoring.TryGetMyRival(out ulong rivalId);

        foreach (var remote in PlayerRegistry.Remotes)
        {
            if (remote.Car == null) continue;
            var marker = remote.Car.GetComponent<RemoteCarMarker>();
            if (marker == null) continue;

            if (hasRival && remote.ClientId == rivalId)
                marker.Show("RIVAL", new Color(1f, 0.35f, 0.25f));
            else if (localTeam != 0 && remote.Team == localTeam)
                marker.Show("TEAMMATE", new Color(0.35f, 0.9f, 1f));
            else
                marker.Hide();
        }
    }

    /// <summary>Reduces a full prefab clone (player car, drone, boulder…) to a puppet: every script,
    /// audio source and camera goes; trails/particles are muted (they're driven by the owner-only
    /// scripts that no longer exist). With <paramref name="keepColliders"/> the colliders survive and
    /// every Rigidbody becomes KINEMATIC — a solid, infinitely-massy obstacle the local physics can
    /// bump against (player-vs-player contact, drones/boulders shoving the local car) while the
    /// puppet itself is driven purely by RemoteCarPuppet's transform. Without it (projectiles, whose
    /// hits are host-authoritative) colliders and rigidbodies are removed entirely. Runs while the
    /// clone is dormant under an inactive staging root, so none of the removed components ever
    /// executes.</summary>
    internal static void StripPuppet(GameObject go, bool keepColliders)
    {
        // FIRST, replicate the "hidden until triggered" state the owner-only scripts would normally
        // establish — their Awake/OnEnable never runs on a puppet, so conditional visuals that are
        // authored ACTIVE in the prefab would otherwise show permanently.
        HideConditionalVisuals(go);

        // Scripts first (RequireComponent means the Rigidbody/colliders can't go before them).
        foreach (var script in go.GetComponentsInChildren<MonoBehaviour>(true))
            DestroyImmediate(script);
        foreach (var listener in go.GetComponentsInChildren<AudioListener>(true))
            DestroyImmediate(listener);
        foreach (var cam in go.GetComponentsInChildren<Camera>(true))
            DestroyImmediate(cam);
        foreach (var source in go.GetComponentsInChildren<AudioSource>(true))
            DestroyImmediate(source);

        if (keepColliders)
        {
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;      // immovable by contacts; follows the puppet transform
                rb.useGravity = false;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;   // fast contacts
            }
        }
        else
        {
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                DestroyImmediate(col);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                DestroyImmediate(rb);
        }

        foreach (var trail in go.GetComponentsInChildren<TrailRenderer>(true))
            trail.emitting = false;
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
        }
    }

    /// <summary>Puts the prefab's conditional accessories in their at-rest (hidden) state, exactly
    /// as their owner-side scripts would have: JetFlames hides its flame visuals until a jump
    /// (`OnEnable → SetFlames(false)` on its assigned list, defaulting to its direct children), and
    /// SDAbilityVFX DEACTIVATES each SD effect's GameObject (`Awake → Hide()` — stopping the particle
    /// system alone isn't enough, the object itself goes inactive). Runs BEFORE the script strip,
    /// while the component data is still readable. (Phase 6 may re-drive these from replicated state;
    /// for now remote cars simply never flare/glow.)</summary>
    static void HideConditionalVisuals(GameObject go)
    {
        foreach (var jet in go.GetComponentsInChildren<JetFlames>(true))
        {
            if (jet.flames != null && jet.flames.Length > 0)
            {
                foreach (var flame in jet.flames)
                    if (flame != null) flame.SetActive(false);
            }
            else
            {
                foreach (Transform child in jet.transform)
                    child.gameObject.SetActive(false);
            }
        }

        foreach (var vfx in go.GetComponentsInChildren<SDAbilityVFX>(true))
        {
            if (vfx.effects == null) continue;
            foreach (var effect in vfx.effects)
                if (effect.particleSystem != null)
                    effect.particleSystem.gameObject.SetActive(false);
        }
    }

    // -------------------------------------------------------
    //  Messaging plumbing
    // -------------------------------------------------------

    void RegisterHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[RemoteCars] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgHello, (sender, reader) =>
        {
            reader.ReadValueSafe(out string name);
            reader.ReadValueSafe(out string carName);
            reader.ReadValueSafe(out int team);
            if (IsServer) HandleHello(sender, name, carName, team);
        });
        msg.RegisterNamedMessageHandler(MsgRoster, (sender, reader) =>
        {
            reader.ReadValueSafe(out int count);
            var entries = new List<(ulong, string, string, int)>(count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ulong id);
                reader.ReadValueSafe(out string name);
                reader.ReadValueSafe(out string car);
                reader.ReadValueSafe(out int team);
                entries.Add((id, name, car, team));
            }
            ApplyRoster(entries);
        });
        msg.RegisterNamedMessageHandler(MsgCar, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out ushort sequence);
            reader.ReadValueSafe(out Vector3 pos);
            reader.ReadValueSafe(out Quaternion rot);
            reader.ReadValueSafe(out Vector3 linVel);
            reader.ReadValueSafe(out Vector3 angVel);
            HandleCarState(id, sequence, pos, rot, linVel, angVel);
        });
    }

    void UnregisterHandlers()
    {
        var msg = Msg;
        if (msg == null) return;
        msg.UnregisterNamedMessageHandler(MsgHello);
        msg.UnregisterNamedMessageHandler(MsgRoster);
        msg.UnregisterNamedMessageHandler(MsgCar);
    }

    static void SendToRemoteClients(string messageName, FastBufferWriter writer,
                                    NetworkDelivery delivery, ulong excludeClientId = ulong.MaxValue)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        foreach (var id in nm.ConnectedClientsIds)
            if (id != nm.LocalClientId && id != excludeClientId)
                nm.CustomMessagingManager.SendNamedMessage(messageName, id, writer, delivery);
    }
}
