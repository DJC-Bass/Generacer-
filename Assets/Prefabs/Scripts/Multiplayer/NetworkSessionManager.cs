using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// Persistent owner of the online-multiplayer session plumbing (Phase 1 of the multiplayer roadmap).
/// Wraps the Unity Multiplayer Services SDK "Sessions" API (com.unity.services.multiplayer), which
/// bundles Lobby + Relay + the NGO network start into ONE object: creating/joining a session with
/// WithRelayNetwork() allocates Relay and calls NetworkManager.StartHost()/StartClient() itself —
/// do NOT hand-roll separate Lobby heartbeats or Relay allocations on top of this.
///
/// Responsibilities:
///  • Anonymous UGS auth on first use (project must be linked to a UGS project id).
///  • Creates the persistent NetworkManager + UnityTransport (scene-management OFF — the multiplayer
///    world uses additive areas per the Phase 2 design, never NGO scene sync).
///  • Host / join-by-code / join-by-id / query public sessions.
///  • Team size is a SESSION PROPERTY (the lobby rule) — MaxPlayers = 2 × teamSize, never hard-coded.
///  • Per-player metadata (name / team / car / ready) as session player properties.
///  • NGO connection approval capped at the session's MaxPlayers (belt-and-braces over lobby slots).
///  • Disconnect handling: host quit DELETES the session (clients see "host closed the lobby");
///    losing the session cleanly shuts the network down and raises <see cref="SessionEnded"/>.
/// Created on demand by the lobby UI via <see cref="EnsureExists"/>; no scene setup.
/// </summary>
public class NetworkSessionManager : MonoBehaviour
{
    // ---- Session property keys (lobby rules) ----
    /// <summary>Players per team — the lobby rule. Public + indexed so the session browser can show it.</summary>
    public const string SessionPropTeamSize = "teamSize";
    /// <summary>"1" once the host has started the game (locks the lobby; Phase 2 reacts to it).</summary>
    public const string SessionPropStarted = "started";

    // ---- Player property keys (per-player metadata) ----
    public const string PlayerPropName = "name";
    public const string PlayerPropTeam = "team";    // "1" or "2"; "" = unassigned
    public const string PlayerPropCar = "car";      // display name of the chosen car
    public const string PlayerPropReady = "ready";  // "1" ready, "0" not

    public const int MinTeamSize = 1;
    public const int MaxTeamSize = 4;
    public const int DefaultTeamSize = 3;

    public static NetworkSessionManager Instance { get; private set; }

    /// <summary>The live session ("lobby") this client is in; null when not in one.</summary>
    public ISession Session { get; private set; }
    public bool InSession => Session != null;
    public bool IsSessionHost => Session != null && Session.IsHost;

    /// <summary>Local display name. Anonymous auth has no profile, so it's derived from the player id.</summary>
    public string LocalPlayerName { get; private set; } = "PLAYER";

    /// <summary>Players per team for the current session (the lobby rule; MaxPlayers is 2× this).</summary>
    public int TeamSize
    {
        get
        {
            if (Session == null) return DefaultTeamSize;
            if (Session.Properties != null &&
                Session.Properties.TryGetValue(SessionPropTeamSize, out var p) &&
                int.TryParse(p.Value, out int size) && size > 0)
                return size;
            return Mathf.Max(1, Session.MaxPlayers / 2);
        }
    }

    /// <summary>True once the host has pressed START GAME on the current session.</summary>
    public bool GameStarted =>
        Session != null && Session.Properties != null &&
        Session.Properties.TryGetValue(SessionPropStarted, out var p) && p.Value == "1";

    /// <summary>Raised whenever anything about the session changes (players, properties, state).</summary>
    public event Action SessionUpdated;

    /// <summary>Raised when the session ends from OUR point of view — kicked, host closed it, or the
    /// connection dropped — with a short reason for the UI. NOT raised on a deliberate local leave.</summary>
    public event Action<string> SessionEnded;

    private bool leaving;        // suppresses end-events raised by our own deliberate teardown
    private bool worldLaunched;  // the multiplayer world has been started for the current session

    /// <summary>Finds or creates the persistent manager. Safe to call repeatedly.</summary>
    public static NetworkSessionManager EnsureExists()
    {
        if (Instance == null)
        {
            var go = new GameObject("NetworkSessionManager");
            go.AddComponent<NetworkSessionManager>();   // sets Instance in Awake
        }
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        // Phase 2 launch hook: once the host flags the game as started (Phase 1's StartGameAsync),
        // every member — host and joiners alike — transitions from the lobby into the shared world.
        if (!worldLaunched && InSession && GameStarted)
        {
            worldLaunched = true;
            MultiplayerWorld.Launch();
        }
    }

    // -------------------------------------------------------
    //  UGS bootstrap
    // -------------------------------------------------------

    /// <summary>Initializes Unity Gaming Services and signs in anonymously (once). Throws on failure —
    /// most commonly the project not being linked to a UGS project id (Project Settings → Services).</summary>
    public async Task EnsureServicesAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        string id = AuthenticationService.Instance.PlayerId ?? "";
        LocalPlayerName = "PLAYER-" + id.Substring(0, Mathf.Min(4, id.Length)).ToUpperInvariant();
    }

    /// <summary>Creates the persistent NetworkManager + UnityTransport if none exists. The Sessions
    /// API's NGO integration needs NetworkManager.Singleton alive BEFORE create/join — it starts the
    /// host/client itself once Relay is allocated.</summary>
    void EnsureNetworkManager()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            var go = new GameObject("NetworkManager");
            DontDestroyOnLoad(go);
            nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();
            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                ConnectionApproval = true,
                // Phase 2 design: hub + track are ADDITIVE AREAS in one session, never NGO scene sync.
                EnableSceneManagement = false,
            };
        }
        nm.ConnectionApprovalCallback = ApproveConnection;
        nm.OnClientDisconnectCallback -= OnNgoClientDisconnect;
        nm.OnClientDisconnectCallback += OnNgoClientDisconnect;
    }

    /// <summary>NGO connection approval (runs on the host). The lobby's MaxPlayers (2 × team size)
    /// already caps slots service-side; this is the transport-level backstop, and it refuses joins
    /// once the game has started.</summary>
    void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
                           NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = false;   // no networked player prefab until Phase 3

        bool isHostSelf = request.ClientNetworkId == NetworkManager.ServerClientId;
        int connected = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClientsIds.Count : 0;

        if (Session == null && !isHostSelf)
        {
            response.Approved = false;
            response.Reason = "No active session.";
        }
        else if (!isHostSelf && GameStarted)
        {
            response.Approved = false;
            response.Reason = "Game already started.";
        }
        else if (Session != null && connected >= Session.MaxPlayers)
        {
            response.Approved = false;
            response.Reason = "Session is full.";
        }
        else
        {
            response.Approved = true;
        }
    }

    // -------------------------------------------------------
    //  Host / join / browse
    // -------------------------------------------------------

    /// <summary>Creates a session: lobby + Relay allocation + NGO StartHost in one call. The host
    /// defaults to team 1. Team size is stored as a public, indexed session property so the browser
    /// can display it.</summary>
    public async Task<ISession> HostSessionAsync(string lobbyName, int teamSize, bool isPrivate)
    {
        teamSize = Mathf.Clamp(teamSize, MinTeamSize, MaxTeamSize);
        await EnsureServicesAsync();
        EnsureNetworkManager();

        var options = new SessionOptions
        {
            Name = string.IsNullOrEmpty(lobbyName) ? LocalPlayerName + "'S LOBBY" : lobbyName,
            MaxPlayers = teamSize * 2,
            IsPrivate = isPrivate,
            SessionProperties = new Dictionary<string, SessionProperty>
            {
                {
                    SessionPropTeamSize,
                    new SessionProperty(teamSize.ToString(), VisibilityPropertyOptions.Public, PropertyIndex.Number1)
                },
            },
            PlayerProperties = BuildLocalPlayerProperties(team: "1"),
        }.WithRelayNetwork();

        var session = await MultiplayerService.Instance.CreateSessionAsync(options);
        AdoptSession(session);
        return session;
    }

    /// <summary>Joins by the 6-char join code shown in the host's lobby room, then auto-assigns the
    /// smaller team.</summary>
    public async Task<ISession> JoinByCodeAsync(string code)
    {
        await EnsureServicesAsync();
        EnsureNetworkManager();

        var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(
            code.Trim().ToUpperInvariant(),
            new JoinSessionOptions { PlayerProperties = BuildLocalPlayerProperties(team: "") });
        AdoptSession(session);
        await AutoAssignTeamAsync();
        return session;
    }

    /// <summary>Joins a session picked from the public browser list, then auto-assigns the smaller team.</summary>
    public async Task<ISession> JoinByIdAsync(string sessionId)
    {
        await EnsureServicesAsync();
        EnsureNetworkManager();

        var session = await MultiplayerService.Instance.JoinSessionByIdAsync(
            sessionId,
            new JoinSessionOptions { PlayerProperties = BuildLocalPlayerProperties(team: "") });
        AdoptSession(session);
        await AutoAssignTeamAsync();
        return session;
    }

    /// <summary>Queries public (non-private, non-locked) sessions for the browser screen.</summary>
    public async Task<IList<ISessionInfo>> QueryPublicSessionsAsync(int count = 20)
    {
        await EnsureServicesAsync();
        var results = await MultiplayerService.Instance.QuerySessionsAsync(
            new QuerySessionsOptions { Count = count });
        return results.Sessions;
    }

    Dictionary<string, PlayerProperty> BuildLocalPlayerProperties(string team)
    {
        string car = SelectedCarStore.Instance != null ? SelectedCarStore.Instance.SelectedCarName ?? "" : "";
        return new Dictionary<string, PlayerProperty>
        {
            { PlayerPropName, new PlayerProperty(LocalPlayerName, VisibilityPropertyOptions.Member) },
            { PlayerPropTeam, new PlayerProperty(team, VisibilityPropertyOptions.Member) },
            { PlayerPropCar, new PlayerProperty(car, VisibilityPropertyOptions.Member) },
            { PlayerPropReady, new PlayerProperty("0", VisibilityPropertyOptions.Member) },
        };
    }

    // -------------------------------------------------------
    //  Player metadata
    // -------------------------------------------------------

    /// <summary>Sets one of the local player's session properties (team/car/ready/…) and pushes it to
    /// the service, so every member's Changed event fires.</summary>
    public async Task SetLocalPlayerPropertyAsync(string key, string value)
    {
        if (Session == null || Session.CurrentPlayer == null) return;
        Session.CurrentPlayer.SetProperty(key, new PlayerProperty(value ?? "", VisibilityPropertyOptions.Member));
        await Session.SaveCurrentPlayerDataAsync();
        SessionUpdated?.Invoke();   // remote members get Changed; refresh our own UI immediately too
    }

    /// <summary>Reads a player property, "" when missing.</summary>
    public static string PropertyOf(IReadOnlyPlayer player, string key)
    {
        if (player?.Properties != null && player.Properties.TryGetValue(key, out var p) && p.Value != null)
            return p.Value;
        return "";
    }

    /// <summary>The player's team: 1, 2, or 0 when unassigned.</summary>
    public static int TeamOf(IReadOnlyPlayer player)
    {
        return int.TryParse(PropertyOf(player, PlayerPropTeam), out int t) ? t : 0;
    }

    public static bool IsReady(IReadOnlyPlayer player) => PropertyOf(player, PlayerPropReady) == "1";

    /// <summary>How many session members are currently on <paramref name="team"/> (1 or 2).</summary>
    public int CountTeam(int team)
    {
        if (Session == null) return 0;
        int n = 0;
        foreach (var p in Session.Players)
            if (TeamOf(p) == team) n++;
        return n;
    }

    /// <summary>Local player's team (1/2, 0 unassigned).</summary>
    public int LocalTeam() => Session != null ? TeamOf(Session.CurrentPlayer) : 0;

    /// <summary>Puts the local player on whichever team has fewer members (tie → team 1). Called
    /// right after joining, so nobody sits unassigned unless both teams are somehow full.</summary>
    public async Task AutoAssignTeamAsync()
    {
        if (Session == null) return;
        int one = CountTeam(1), two = CountTeam(2), cap = TeamSize;
        int pick = one <= two ? 1 : 2;
        if ((pick == 1 ? one : two) >= cap) pick = pick == 1 ? 2 : 1;   // preferred team full — take the other
        if ((pick == 1 ? one : two) >= cap) return;                     // both full (shouldn't happen)
        await SetLocalPlayerPropertyAsync(PlayerPropTeam, pick.ToString());
    }

    /// <summary>Moves the local player to the other team if there's room. False (with a reason) if not.</summary>
    public async Task<bool> TrySwitchTeamAsync()
    {
        if (Session == null) return false;
        int target = LocalTeam() == 1 ? 2 : 1;
        if (CountTeam(target) >= TeamSize) return false;
        await SetLocalPlayerPropertyAsync(PlayerPropTeam, target.ToString());
        return true;
    }

    /// <summary>True when the lobby can launch: every member has a team, no team is over the size
    /// rule, and everyone is ready. Outs a short reason for the UI when it can't.</summary>
    public bool ReadyToStart(out string reason)
    {
        reason = "";
        if (Session == null) { reason = "NO SESSION"; return false; }
        int one = 0, two = 0;
        foreach (var p in Session.Players)
        {
            int t = TeamOf(p);
            if (t == 1) one++;
            else if (t == 2) two++;
            else { reason = "A PLAYER HAS NO TEAM"; return false; }
            if (!IsReady(p)) { reason = "WAITING FOR PLAYERS TO READY UP"; return false; }
        }
        if (one > TeamSize || two > TeamSize) { reason = "A TEAM IS OVER THE SIZE LIMIT"; return false; }
        return true;
    }

    // -------------------------------------------------------
    //  Start / leave / teardown
    // -------------------------------------------------------

    /// <summary>Host only: locks the lobby (no more joins) and flags the game as started. Phase 2's
    /// shared-world load takes over from this property; Phase 1 stops at the flag.</summary>
    public async Task StartGameAsync()
    {
        if (!IsSessionHost) return;
        var host = Session.AsHost();
        host.IsLocked = true;
        host.SetProperty(SessionPropStarted, new SessionProperty("1", VisibilityPropertyOptions.Member));
        await host.SavePropertiesAsync();
        Debug.Log("[NetworkSession] Game start flagged — Phase 2 loads the shared world from here.");
        SessionUpdated?.Invoke();
    }

    /// <summary>Deliberately leaves the current session. The HOST deletes it outright so every client
    /// gets a clean "host closed the lobby" ending instead of a headless session. Always shuts the
    /// NGO connection down.</summary>
    public async Task LeaveSessionAsync()
    {
        var session = Session;
        if (session == null) return;

        leaving = true;
        Session = null;
        UnhookSessionEvents(session);
        try
        {
            if (session.IsHost) await session.AsHost().DeleteAsync();
            else await session.LeaveAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NetworkSession] Leave/delete failed (session may already be gone): {e.Message}");
        }
        finally
        {
            ShutdownNetwork();
            leaving = false;
            worldLaunched = false;
        }
        SessionUpdated?.Invoke();
    }

    void AdoptSession(ISession session)
    {
        Session = session;
        session.Changed += OnSessionChanged;
        session.RemovedFromSession += OnRemovedFromSession;
        session.Deleted += OnSessionDeleted;
        session.StateChanged += OnSessionStateChanged;
        SessionUpdated?.Invoke();
    }

    void UnhookSessionEvents(ISession session)
    {
        session.Changed -= OnSessionChanged;
        session.RemovedFromSession -= OnRemovedFromSession;
        session.Deleted -= OnSessionDeleted;
        session.StateChanged -= OnSessionStateChanged;
    }

    void OnSessionChanged() => SessionUpdated?.Invoke();
    void OnRemovedFromSession() => EndLocally("REMOVED FROM LOBBY");
    void OnSessionDeleted() => EndLocally("HOST CLOSED THE LOBBY");

    void OnSessionStateChanged(SessionState state)
    {
        if (state == SessionState.Disconnected) EndLocally("CONNECTION LOST");
        else SessionUpdated?.Invoke();
    }

    void OnNgoClientDisconnect(ulong clientId)
    {
        // Our own transport connection dropped (e.g. host vanished without deleting the lobby).
        // The session's Disconnected state usually fires too — EndLocally is idempotent via Session null.
        var nm = NetworkManager.Singleton;
        if (nm != null && Session != null && !Session.IsHost && clientId == nm.LocalClientId)
            EndLocally("CONNECTION LOST");
    }

    /// <summary>The session ended on us (not a deliberate local leave): clean up and tell the UI why.</summary>
    void EndLocally(string reason)
    {
        if (leaving || Session == null) return;
        var session = Session;
        Session = null;
        UnhookSessionEvents(session);
        ShutdownNetwork();
        worldLaunched = false;
        Debug.Log($"[NetworkSession] Session ended: {reason}");

        // Mid-game session death (host quit, connection lost): tear the shared world down to the
        // Main Menu — the lobby UI (which normally shows the reason) died with the menu scene.
        if (MultiplayerWorld.IsMultiplayerGame)
            MultiplayerWorld.Instance.TeardownToMenu(reason);

        SessionEnded?.Invoke(reason);
        SessionUpdated?.Invoke();
    }

    void ShutdownNetwork()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();
    }
}
