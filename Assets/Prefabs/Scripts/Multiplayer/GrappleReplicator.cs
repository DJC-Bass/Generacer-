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
    const string MsgGrapple = "GNRC_GRAPPLE";        // {senderId, state, kind, anchorClient, a, b, f}
    const string MsgPull = "GNRC_GRAPPLE_PULL";      // → victim: {accel} applied to their own car
    const string MsgBreak = "GNRC_GRAPPLE_BREAK";    // {victimId}: release any hook attached to them

    // Only a DYNAMIC anchor (drone/boulder) still needs a streamed position. Static points never move,
    // a PlayerCar is derived from the viewer's own copy of that car, and flight is simulated locally —
    // so those three only need a slow heartbeat to heal a missed packet or catch a late joiner.
    const float StreamRate = 15f;
    const float HeartbeatRate = 2f;
    // Eases a Dynamic anchor's streamed point so it glides between updates instead of stepping.
    const float DynamicSmoothTau = 0.08f;

    public static GrappleReplicator Instance { get; private set; }

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    // One rope visual per remote player currently grappling.
    private class RemoteRope
    {
        public GameObject go;
        public GrappleRope rope;
        public byte state;
        public byte kind;
        public ulong anchorClientId;
        public Vector3 localOffset;     // PlayerCar / Dynamic: attach point in the anchor's local space
        public Vector3 worldPoint;      // Static: the fixed point. Dynamic: latest streamed position.
        public Vector3 smoothedPoint;   // Dynamic only: eased toward worldPoint
        public bool hasSmoothed;
        public Vector3 flightOrigin;
        public Vector3 flightVelocity;
        public float flightBaseElapsed; // how long it had been flying when the message was sent
        public float flightLocalStart;  // local time we began simulating
        public bool active;
    }
    private readonly Dictionary<ulong, RemoteRope> remoteRopes = new Dictionary<ulong, RemoteRope>();

    private float nextSend;
    private byte lastSentState = 255;   // forces the first send
    private byte lastSentKind = 255;

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
            msg.UnregisterNamedMessageHandler(MsgBreak);
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
        byte kind = (byte)hook.CurrentAnchorKind;
        bool changed = state != lastSentState || kind != lastSentKind;

        // Only a Dynamic anchor needs the fast stream; everything else just heartbeats.
        bool streaming = state == (byte)GrappleHook.State.Attached
                      && kind == (byte)GrappleHook.AnchorKind.Dynamic;
        float interval = 1f / (streaming ? StreamRate : HeartbeatRate);

        if (!changed && Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + interval;
        lastSentState = state;
        lastSentKind = kind;

        var msg = Msg;
        if (msg == null) return;

        // Two payload slots reused per state, so the message stays a fixed size:
        //   Firing   → a = launch origin, b = velocity, f = seconds already flown
        //   Attached → a = local offset on the anchor, b = world point (Static / Dynamic)
        Vector3 a, b;
        float f;
        if (state == (byte)GrappleHook.State.Firing)
        {
            a = hook.FlightOrigin; b = hook.FlightVelocity; f = hook.FlightElapsed;
        }
        else
        {
            a = hook.AnchorLocalOffset; b = hook.AnchorWorldPoint; f = 0f;
        }

        using var writer = new FastBufferWriter(80, Allocator.Temp);
        WriteState(writer, LocalClientId, state, kind, hook.AnchorClientId, a, b, f);

        // A state CHANGE is the whole story now (fire / attach / release), so it must not be the packet
        // that gets dropped — those go Reliable. Heartbeats and the Dynamic stream can be lossy.
        var delivery = changed ? NetworkDelivery.ReliableSequenced : NetworkDelivery.Unreliable;

        if (IsServer) SendToRemoteClients(MsgGrapple, writer, delivery);
        else msg.SendNamedMessage(MsgGrapple, NetworkManager.ServerClientId, writer, delivery);
    }

    static void WriteState(FastBufferWriter writer, ulong senderId, byte state, byte kind,
                           ulong anchorClientId, Vector3 a, Vector3 b, float f)
    {
        writer.WriteValueSafe(senderId);
        writer.WriteValueSafe(state);
        writer.WriteValueSafe(kind);
        writer.WriteValueSafe(anchorClientId);
        writer.WriteValueSafe(a);
        writer.WriteValueSafe(b);
        writer.WriteValueSafe(f);
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

    // -------------------------------------------------------
    //  Breaking free (L3)
    // -------------------------------------------------------

    /// <summary>Shrug off anyone who has us grappled. The tether lives entirely on the GRAPPLER's
    /// machine — we hold no record of being hooked — so rather than track that, we simply announce
    /// "release me" and whoever is attached to us obeys. Cheap (one reliable message on a button press)
    /// and it needs no extra replicated state on the victim's side.</summary>
    public static void SendBreakFree()
    {
        if (Instance == null) return;
        var msg = Msg;
        if (msg == null) return;

        ReleaseHooksAttachedTo(LocalClientId);   // covers a grappler running on this same machine

        using var writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);

        if (IsServer) SendToRemoteClients(MsgBreak, writer, NetworkDelivery.ReliableSequenced);
        else msg.SendNamedMessage(MsgBreak, NetworkManager.ServerClientId, writer,
                                  NetworkDelivery.ReliableSequenced);
    }

    /// <summary>If OUR hook is currently latched onto that player's car, let go.</summary>
    static void ReleaseHooksAttachedTo(ulong victimId)
    {
        var hook = GrappleHook.Instance;
        if (hook == null) return;
        if (hook.CurrentState != GrappleHook.State.Attached) return;
        if (hook.CurrentAnchorKind != GrappleHook.AnchorKind.PlayerCar) return;
        if (hook.AnchorClientId != victimId) return;

        hook.Release();
        Debug.Log($"[Grapple] Client {victimId} broke free — our tether released.");
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
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out ulong anchorClientId);
            reader.ReadValueSafe(out Vector3 a);
            reader.ReadValueSafe(out Vector3 b);
            reader.ReadValueSafe(out float f);

            // Host relays every owner's rope on to the other clients (senderId rides in the payload).
            if (IsServer && senderId != LocalClientId)
            {
                using var writer = new FastBufferWriter(80, Allocator.Temp);
                WriteState(writer, senderId, state, kind, anchorClientId, a, b, f);
                SendToRemoteClients(MsgGrapple, writer, NetworkDelivery.ReliableSequenced,
                                    excludeClientId: senderId);
            }

            if (senderId != LocalClientId)
                ApplyRemoteState(senderId, state, kind, anchorClientId, a, b, f);
        });

        msg.RegisterNamedMessageHandler(MsgBreak, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong victimId);

            // Host fans it out — the grappler could be any machine, so everyone needs to hear it.
            if (IsServer && victimId != LocalClientId)
            {
                using var writer = new FastBufferWriter(16, Allocator.Temp);
                writer.WriteValueSafe(victimId);
                SendToRemoteClients(MsgBreak, writer, NetworkDelivery.ReliableSequenced,
                                    excludeClientId: victimId);
            }

            ReleaseHooksAttachedTo(victimId);
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

    void ApplyRemoteState(ulong senderId, byte state, byte kind, ulong anchorClientId,
                          Vector3 a, Vector3 b, float f)
    {
        if (!remoteRopes.TryGetValue(senderId, out var entry))
            remoteRopes[senderId] = entry = new RemoteRope();

        bool newFlight = state == (byte)GrappleHook.State.Firing
                      && entry.state != (byte)GrappleHook.State.Firing;

        entry.state = state;
        entry.kind = kind;
        entry.anchorClientId = anchorClientId;
        entry.active = state != (byte)GrappleHook.State.Idle;

        if (state == (byte)GrappleHook.State.Firing)
        {
            entry.flightOrigin = a;
            entry.flightVelocity = b;
            entry.flightBaseElapsed = f;
            // Re-base the local clock on every flight message so a heartbeat mid-flight corrects any
            // accumulated drift, rather than letting the simulation slide away from the owner's.
            entry.flightLocalStart = Time.time;
            if (newFlight) entry.hasSmoothed = false;
        }
        else
        {
            entry.localOffset = a;
            entry.worldPoint = b;
            if (kind != (byte)GrappleHook.AnchorKind.Dynamic) entry.hasSmoothed = false;
        }
    }

    /// <summary>Where this rope's far end is RIGHT NOW, on this machine. The whole point of option A:
    /// for a PlayerCar anchor we read OUR OWN copy of that car — already interpolated by
    /// RemoteCarPuppet — so the rope end is rigidly glued to the car as this viewer sees it, with no
    /// lag and nothing to snap. Only a Dynamic anchor falls back to a streamed (and eased) point.</summary>
    bool TryResolveHookPosition(RemoteRope entry, out Vector3 position)
    {
        position = entry.worldPoint;

        // FLIGHT (option D): simulate the straight-line travel locally instead of being fed positions.
        if (entry.state == (byte)GrappleHook.State.Firing)
        {
            float elapsed = entry.flightBaseElapsed + (Time.time - entry.flightLocalStart);
            var hook = GrappleHook.Instance;
            float timeout = hook != null ? hook.flightTimeout : 2f;
            // Safety: if the owner's release/attach message never arrived, don't fly forever.
            if (elapsed > timeout + 0.5f) return false;
            position = entry.flightOrigin + entry.flightVelocity * elapsed;
            return true;
        }

        switch ((GrappleHook.AnchorKind)entry.kind)
        {
            case GrappleHook.AnchorKind.Static:
                return true;                                  // fixed point — identical on every machine

            case GrappleHook.AnchorKind.PlayerCar:
            {
                Transform car = ResolveCar(entry.anchorClientId);
                if (car == null) return false;                // their car isn't here yet — hide the rope
                position = car.TransformPoint(entry.localOffset);
                return true;
            }

            case GrappleHook.AnchorKind.Dynamic:
            {
                // No local identity for these yet, so ease the streamed point to hide the stepping.
                if (!entry.hasSmoothed) { entry.smoothedPoint = entry.worldPoint; entry.hasSmoothed = true; }
                float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(DynamicSmoothTau, 1e-4f));
                entry.smoothedPoint = Vector3.Lerp(entry.smoothedPoint, entry.worldPoint, t);
                position = entry.smoothedPoint;
                return true;
            }
        }
        return false;
    }

    /// <summary>The car belonging to a client id — ours if it's us being hooked, otherwise their puppet.</summary>
    static Transform ResolveCar(ulong clientId)
    {
        if (clientId == LocalClientId)
        {
            var local = PlayerRegistry.LocalCar;
            return local != null ? local.transform : null;
        }
        var remote = PlayerRegistry.FindRemote(clientId);
        return remote != null && remote.Car != null ? remote.Car.transform : null;
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

            // Declared up front: the && short-circuits, so the compiler can't prove the `out` ran.
            Vector3 hookPos = Vector3.zero;
            bool show = entry.active && car != null && TryResolveHookPosition(entry, out hookPos);
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
            entry.rope.SetEnds(muzzle, hookPos);
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
