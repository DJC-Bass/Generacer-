using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Multiplayer layer for the grappling hook. Two jobs:
///
///  • ROPE VISIBILITY — each owner streams its hook state (firing / attached + the hook-head world
///    position) at <see cref="SendRate"/>; every other machine draws a <see cref="GrappleRope"/> from
///    that player's PUPPET muzzle to that point, so you can see other people's tethers. Level-triggered
///    like the car-effect flags, so a dropped Unreliable packet self-heals on the next tick; the
///    RELEASE is sent Reliable so a rope can never be left hanging.
///
///  • PULLING A REMOTE PLAYER — movement is owner-authoritative, so player A tugging player B's car
///    cannot just push B's puppet: that puppet is a kinematic copy and the force would be overwritten
///    by B's next state update. Instead A sends a PULL, the host relays it to B, and B's own machine
///    applies the acceleration to their real car. The force lands where the authority is.
///
/// Added to the MultiplayerWorld object at session begin, alongside RemoteCarManager / NpcReplicator.
/// </summary>
public class GrappleReplicator : MonoBehaviour
{
    const string MsgGrapple = "GNRC_GRAPPLE";        // {senderId, state, hookPos}
    const string MsgPull = "GNRC_GRAPPLE_PULL";      // → victim: {accel} applied to their own car

    const float SendRate = 15f;                      // rope state updates per second

    public static GrappleReplicator Instance { get; private set; }

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    // One rope visual per remote player currently grappling.
    private class RemoteRope
    {
        public GameObject go;
        public GrappleRope rope;
        public Vector3 hookPos;
        public bool active;
    }
    private readonly Dictionary<ulong, RemoteRope> remoteRopes = new Dictionary<ulong, RemoteRope>();

    private float nextSend;
    private byte lastSentState = 255;   // forces the first send

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        RegisterHandlers();
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        var msg = Msg;
        if (msg != null)
        {
            msg.UnregisterNamedMessageHandler(MsgGrapple);
            msg.UnregisterNamedMessageHandler(MsgPull);
        }
        foreach (var kv in remoteRopes) if (kv.Value.go != null) Destroy(kv.Value.go);
        remoteRopes.Clear();
        Instance = null;
    }

    void Update()
    {
        BroadcastLocalState();
        UpdateRemoteRopes();
    }

    // -------------------------------------------------------
    //  Outgoing: our own rope
    // -------------------------------------------------------

    void BroadcastLocalState()
    {
        var hook = GrappleHook.Instance;
        if (hook == null) return;

        byte state = (byte)hook.CurrentState;
        bool stateChanged = state != lastSentState;

        // Rate-limit the streaming updates, but let a state CHANGE go out immediately — the release in
        // particular must not wait, or the rope lingers on everyone else's screen.
        if (!stateChanged && Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + 1f / SendRate;
        lastSentState = state;

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(64, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(state);
        writer.WriteValueSafe(hook.HookPosition);

        // Idle (a release) goes Reliable so it can't be the packet that gets dropped.
        var delivery = state == (byte)GrappleHook.State.Idle
            ? NetworkDelivery.ReliableSequenced : NetworkDelivery.Unreliable;

        if (IsServer) SendToRemoteClients(MsgGrapple, writer, delivery);
        else msg.SendNamedMessage(MsgGrapple, NetworkManager.ServerClientId, writer, delivery);
    }

    /// <summary>Called by the local GrappleHook when it reels a car belonging to ANOTHER player: routes
    /// the acceleration to that player's own machine, where their car actually lives.</summary>
    public static void SendPullToOwner(ulong targetClientId, Vector3 acceleration)
    {
        if (Instance == null) return;
        if (targetClientId == LocalClientId) return;   // our own car is pulled directly, not messaged

        var msg = Msg;
        if (msg == null) return;

        using var writer = new FastBufferWriter(32, Allocator.Temp);
        writer.WriteValueSafe(targetClientId);
        writer.WriteValueSafe(acceleration);

        // Clients can't address each other directly — everything goes via the host, which relays.
        if (IsServer) RelayPull(targetClientId, acceleration);
        else msg.SendNamedMessage(MsgPull, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
    }

    static void RelayPull(ulong targetClientId, Vector3 acceleration)
    {
        var msg = Msg;
        if (msg == null) return;

        if (targetClientId == LocalClientId) { ApplyPullLocally(acceleration); return; }

        using var writer = new FastBufferWriter(32, Allocator.Temp);
        writer.WriteValueSafe(targetClientId);
        writer.WriteValueSafe(acceleration);
        msg.SendNamedMessage(MsgPull, targetClientId, writer, NetworkDelivery.Unreliable);
    }

    /// <summary>A remote player is reeling OUR car in — apply it to the real thing on this machine.</summary>
    static void ApplyPullLocally(Vector3 acceleration)
    {
        var car = PlayerRegistry.LocalCar;
        if (car == null) return;
        var rb = car.GetComponent<Rigidbody>();
        if (rb != null) rb.AddForce(acceleration, ForceMode.Acceleration);
    }

    // -------------------------------------------------------
    //  Incoming
    // -------------------------------------------------------

    void RegisterHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[Grapple] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgGrapple, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong senderId);
            reader.ReadValueSafe(out byte state);
            reader.ReadValueSafe(out Vector3 hookPos);

            // Host relays every owner's rope on to the other clients (senderId rides in the payload).
            if (IsServer && senderId != LocalClientId)
            {
                using var writer = new FastBufferWriter(64, Allocator.Temp);
                writer.WriteValueSafe(senderId);
                writer.WriteValueSafe(state);
                writer.WriteValueSafe(hookPos);
                SendToRemoteClients(MsgGrapple, writer, NetworkDelivery.Unreliable, excludeClientId: senderId);
            }

            if (senderId != LocalClientId) ApplyRemoteState(senderId, state, hookPos);
        });

        msg.RegisterNamedMessageHandler(MsgPull, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong targetClientId);
            reader.ReadValueSafe(out Vector3 acceleration);

            // On the host this is a relay request; on a client it's addressed to us.
            if (IsServer && targetClientId != LocalClientId) RelayPull(targetClientId, acceleration);
            else if (targetClientId == LocalClientId) ApplyPullLocally(acceleration);
        });
    }

    void ApplyRemoteState(ulong senderId, byte state, Vector3 hookPos)
    {
        if (!remoteRopes.TryGetValue(senderId, out var entry))
            remoteRopes[senderId] = entry = new RemoteRope();

        entry.hookPos = hookPos;
        entry.active = state != (byte)GrappleHook.State.Idle;
    }

    /// <summary>Draws each remote player's rope from their puppet's nose to their hook point. Their
    /// puppet may not exist yet (roster still landing) or may have been destroyed on disconnect, so the
    /// rope is hidden whenever the car is missing rather than dangling in space.</summary>
    void UpdateRemoteRopes()
    {
        var hook = GrappleHook.Instance;
        float muzzleForward = hook != null ? hook.muzzleForward : 2.5f;
        float muzzleUp = hook != null ? hook.muzzleUp : 0.4f;

        foreach (var kv in remoteRopes)
        {
            var entry = kv.Value;
            var remote = PlayerRegistry.FindRemote(kv.Key);
            GameObject car = remote != null ? remote.Car : null;

            bool show = entry.active && car != null;
            if (!show)
            {
                if (entry.go != null && entry.go.activeSelf) entry.go.SetActive(false);
                continue;
            }

            if (entry.go == null)
            {
                entry.go = new GameObject("GrappleRope_Remote_" + kv.Key);
                DontDestroyOnLoad(entry.go);
                entry.go.AddComponent<LineRenderer>();
                entry.rope = entry.go.AddComponent<GrappleRope>();
            }
            if (!entry.go.activeSelf) { entry.go.SetActive(true); entry.rope.ResetShape(); }

            Vector3 muzzle = car.transform.position
                           + car.transform.forward * muzzleForward
                           + car.transform.up * muzzleUp;
            entry.rope.SetEnds(muzzle, entry.hookPos);
        }
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
