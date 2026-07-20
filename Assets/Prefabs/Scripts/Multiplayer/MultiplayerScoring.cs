using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Phase 4: the SERVER-authoritative game loop scoring. Added to the MultiplayerWorld object at
/// session begin. The single-player rule logic survives intact, with exactly the two changes the
/// roadmap called for:
///  • `playerFirstPlaceThisRound` → WHICH TEAM finished first: the first player to reach the end
///    portal before any AI (their local sim's AnyRacerFinishedAhead, until Phase 5 moves AI
///    server-side) claims the round for their team.
///  • `CountPlayerSDs()` → DISTINCT SD names across that team's members. Duplicates ACROSS teams are
///    fine (both teams may own a Lightning SD); a team is never awarded an SD it already holds.
///
/// Server state: per-client SD sets (client-reported from PlayerInventory changes — kill-floor wipes
/// arrive as an empty report — and updated instantly on the server's own awards so the round-end check
/// never races the echo), the round's first-place claim, and the SHARED DroneWins tally.
/// Round end (driven by MultiplayerWorld's loop): EvaluateRoundServer runs ONCE —
/// claimed team ⇒ SD award to the finisher (validated against the team set) + team win check at
/// `sdItemsToWin`; nobody ⇒ DroneWins++ ⇒ drone ending for EVERYONE at `droneWinsToGameOver`.
/// Endings broadcast to all machines; each fires the local GameLoopManager's puppet ending so the
/// existing HubSceneController presentations (drone swarm / victory banner) play everywhere at once.
///
/// Credits/turbos stay LOCAL concerns (each client still awards its own completion credits);
/// only SD ownership is server-truth.
/// </summary>
public class MultiplayerScoring : MonoBehaviour
{
    const string MsgFinish = "GNRC_FINISH";      // client → server: {localFirstPlace}
    const string MsgSds = "GNRC_SDS";            // client → server: {count, names...} (distinct SD names held)
    const string MsgSdAward = "GNRC_SD_AWARD";   // server → one client: {sdName}
    const string MsgScore = "GNRC_SCORE";        // server → all: {droneWins}
    const string MsgEnding = "GNRC_ENDING";      // server → all: {isDroneEnding, winningTeam}
    const string MsgRivals = "GNRC_RIVALS";      // server → all: {count, [playerId, rivalId]...}
    const string MsgRivalBonus = "GNRC_RIVAL_BONUS";   // server → one client: {credits} — beat your rival

    /// <summary>The SD pool first-place finishes draw from (mirrors ReturnPortalTrigger's).</summary>
    static readonly string[] SdPool = { "Fire SD", "Lightning SD", "Wind SD" };

    public static MultiplayerScoring Instance { get; private set; }

    /// <summary>Server-side: true once either ending has been broadcast — MultiplayerWorld's round
    /// loop stops scheduling rounds.</summary>
    public bool GameOverServer { get; private set; }

    /// <summary>Credits for reaching the end portal BEFORE your assigned rival this round.</summary>
    public int rivalBonusCredits = 100;

    // ---- Server state ----
    private readonly Dictionary<ulong, HashSet<string>> clientSds = new Dictionary<ulong, HashSet<string>>();
    private int claimedTeam;          // 1/2 once a first-place finish landed this round, else 0
    private ulong claimedByClient;
    private int droneWins;

    // ---- Rival system (server truth; the full map is broadcast, each client uses its own entry) ----
    private readonly Dictionary<ulong, ulong> rivalOf = new Dictionary<ulong, ulong>();
    private readonly HashSet<ulong> finishedThisRound = new HashSet<ulong>();
    private class RivalVacancy
    {
        public bool hasInherited;
        public ulong inheritedRival;               // the leaver's old rival — the replacement inherits it
        public readonly List<ulong> orphans = new List<ulong>();   // players whose rival was the leaver
    }
    private readonly List<RivalVacancy> vacancies = new List<RivalVacancy>();
    private bool rivalsAssigned;

    // ---- Client state (rivals) ----
    private static bool hasMyRival;
    private static ulong myRivalClientId;

    /// <summary>This machine's assigned rival, if any. (clientId 0 is the host — a valid id — so
    /// this is a try-pattern, not a sentinel.)</summary>
    public static bool TryGetMyRival(out ulong clientId)
    {
        clientId = myRivalClientId;
        return hasMyRival;
    }

    // ---- Client state (SD reporting) ----
    private bool sdsDirty;
    private float nextSdReportTime;
    private string lastSdsSent = "";

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    void Awake() => Instance = this;

    void Start()
    {
        RegisterHandlers();
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged += MarkSdsDirty;
        MarkSdsDirty();   // initial report (starting inventory holds no SDs, but make it explicit)
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectedForRivals;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UnregisterHandlers();
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged -= MarkSdsDirty;
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedForRivals;
        hasMyRival = false;   // session over — no rival carries into the next game
    }

    void Update()
    {
        // Debounced SD-set reporting: inventory changes are chatty (credits tick too), so only
        // actually send when the DISTINCT SD SET changed, at most a few times a second.
        if (!sdsDirty || Time.unscaledTime < nextSdReportTime) return;
        sdsDirty = false;
        nextSdReportTime = Time.unscaledTime + 0.25f;

        var sds = LocalSdNames();
        string signature = string.Join("|", sds);
        if (signature == lastSdsSent) return;
        lastSdsSent = signature;
        SendSds(sds);
    }

    void MarkSdsDirty() => sdsDirty = true;

    /// <summary>The local player's distinct held SD names (same rule the single-player
    /// CountPlayerSDs uses: item name ends in " SD" with a positive count).</summary>
    static List<string> LocalSdNames()
    {
        var result = new List<string>();
        var inv = PlayerInventory.Instance;
        if (inv == null) return result;
        foreach (var name in inv.Order)
            if (!string.IsNullOrEmpty(name) && name.EndsWith(" SD") && inv.GetCount(name) > 0)
                result.Add(name);
        result.Sort();
        return result;
    }

    // -------------------------------------------------------
    //  Client → server notifications
    // -------------------------------------------------------

    /// <summary>Called by ReturnPortalTrigger's multiplayer branch when the LOCAL player crosses the
    /// end portal. First-place is judged against this client's own AI sim until Phase 5 centralises
    /// the drones.</summary>
    public static void NotifyLocalFinished(bool localFirstPlace)
    {
        var self = Instance;
        if (self == null) return;

        if (IsServer)
        {
            self.HandleFinish(LocalClientId, localFirstPlace);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
        writer.WriteValueSafe(localFirstPlace);
        msg.SendNamedMessage(MsgFinish, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    void SendSds(List<string> sds)
    {
        if (IsServer)
        {
            HandleSds(LocalClientId, sds);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(512, Allocator.Temp, 4096);
        writer.WriteValueSafe(sds.Count);
        foreach (var sd in sds) writer.WriteValueSafe(sd);
        msg.SendNamedMessage(MsgSds, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
    }

    // -------------------------------------------------------
    //  Server logic
    // -------------------------------------------------------

    void HandleSds(ulong clientId, List<string> sds)
    {
        if (!clientSds.TryGetValue(clientId, out var set))
            clientSds[clientId] = set = new HashSet<string>();
        set.Clear();
        foreach (var sd in sds) set.Add(sd);
    }

    /// <summary>A player finished the track. Since Phase 5, the AI racers live ONLY on the host, so
    /// first place is judged against the SERVER's own AnyRacerFinishedAhead — the client's local
    /// verdict is advisory (its flag mirrors the host's via the RACER_FIN broadcast anyway).</summary>
    void HandleFinish(ulong clientId, bool localFirstPlace)
    {
        if (GameOverServer) return;

        // Rival bonus (independent of first place): finishing while your assigned rival hasn't
        // finished this round = you beat them to the return portal → +100 credits.
        if (!finishedThisRound.Contains(clientId))
        {
            if (rivalOf.TryGetValue(clientId, out ulong rival) && !finishedThisRound.Contains(rival))
                SendRivalBonus(clientId);
            finishedThisRound.Add(clientId);
        }

        bool serverFirstPlace = GameLoopManager.Instance == null
            || !GameLoopManager.Instance.AnyRacerFinishedAhead;
        if (localFirstPlace != serverFirstPlace)
            Debug.Log($"[Scoring] Client {clientId} first-place verdict ({localFirstPlace}) disagrees " +
                      $"with the server ({serverFirstPlace}) — server wins.");

        if (!serverFirstPlace || claimedTeam != 0) return;

        var carManager = GetComponent<RemoteCarManager>();
        int team = carManager != null ? carManager.TeamOfServer(clientId) : 0;
        if (team != 1 && team != 2)
        {
            Debug.LogWarning($"[Scoring] Finish from client {clientId} with unknown team — claim ignored.");
            return;
        }

        claimedTeam = team;
        claimedByClient = clientId;
        Debug.Log($"[Scoring] Client {clientId} finished FIRST — round claimed for team {team}.");

        // Award one SD the TEAM doesn't collectively hold (duplicates across teams are fine).
        var teamSet = TeamDistinctSds(team);
        var missing = new List<string>();
        foreach (var sd in SdPool)
            if (!teamSet.Contains(sd)) missing.Add(sd);
        if (missing.Count == 0) return;   // team already holds the full set

        string award = missing[Random.Range(0, missing.Count)];

        // Book it server-side IMMEDIATELY so the round-end win check never races the client's echo.
        if (!clientSds.TryGetValue(clientId, out var set))
            clientSds[clientId] = set = new HashSet<string>();
        set.Add(award);

        SendSdAward(clientId, award);
        Debug.Log($"[Scoring] Awarded '{award}' to client {clientId} (team {team} now holds {TeamDistinctSds(team).Count}).");
    }

    HashSet<string> TeamDistinctSds(int team)
    {
        var result = new HashSet<string>();
        var carManager = GetComponent<RemoteCarManager>();
        if (carManager == null) return result;
        foreach (var kv in clientSds)
            if (carManager.TeamOfServer(kv.Key) == team)
                result.UnionWith(kv.Value);
        return result;
    }

    /// <summary>Server: fresh round — clear the first-place claim and the rival finish ledger.</summary>
    public void ResetRoundServer()
    {
        claimedTeam = 0;
        claimedByClient = 0;
        finishedThisRound.Clear();
    }

    // -------------------------------------------------------
    //  Rival system (server)
    // -------------------------------------------------------

    /// <summary>Server: assigns every player a random RIVAL from the opposing team, once the full
    /// room is in the hub (called by MultiplayerWorld when the game loop begins). Each direction is
    /// a random permutation of the opposing team, so no player is the rival OF two players on the
    /// same side, and assignments need not be mutual. Waits briefly for any hello-roster stragglers
    /// so every team is known.</summary>
    public void AssignRivalsServer()
    {
        if (!IsServer || rivalsAssigned) return;
        rivalsAssigned = true;
        StartCoroutine(AssignRivalsWhenTeamsKnown());
    }

    IEnumerator AssignRivalsWhenTeamsKnown()
    {
        var carManager = GetComponent<RemoteCarManager>();
        var nm = NetworkManager.Singleton;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            bool allKnown = carManager != null && nm != null;
            if (allKnown)
                foreach (var id in nm.ConnectedClientsIds)
                    if (carManager.TeamOfServer(id) == 0) { allKnown = false; break; }
            if (allKnown) break;
            yield return new WaitForSeconds(0.5f);
        }
        if (carManager == null || nm == null) yield break;

        var teamOne = new List<ulong>();
        var teamTwo = new List<ulong>();
        foreach (var id in nm.ConnectedClientsIds)
        {
            int team = carManager.TeamOfServer(id);
            if (team == 1) teamOne.Add(id);
            else if (team == 2) teamTwo.Add(id);
        }

        rivalOf.Clear();
        AssignRivalDirection(teamOne, teamTwo);
        AssignRivalDirection(teamTwo, teamOne);
        BroadcastRivals();
        Debug.Log($"[Scoring] Rivals assigned ({rivalOf.Count} players paired).");
    }

    /// <summary>Gives each player in <paramref name="players"/> a rival from <paramref name="pool"/>
    /// as a random permutation — injective, so nobody is the rival of two players on this side.</summary>
    void AssignRivalDirection(List<ulong> players, List<ulong> pool)
    {
        if (players.Count == 0 || pool.Count == 0) return;
        var shuffled = new List<ulong>(pool);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        for (int i = 0; i < players.Count; i++)
            rivalOf[players[i]] = shuffled[i % shuffled.Count];
    }

    /// <summary>Server: a player left mid-game — their rival slot becomes a VACANCY: the next joiner
    /// inherits the leaver's rival, and everyone whose rival was the leaver gets the joiner instead.
    /// Until then those players simply have no rival (their marker disappears).</summary>
    void OnClientDisconnectedForRivals(ulong leaverId)
    {
        if (!IsServer || !rivalsAssigned) return;

        var vacancy = new RivalVacancy();
        if (rivalOf.TryGetValue(leaverId, out ulong inherited))
        {
            vacancy.hasInherited = true;
            vacancy.inheritedRival = inherited;
            rivalOf.Remove(leaverId);
        }
        foreach (var kv in rivalOf)
            if (kv.Value == leaverId) vacancy.orphans.Add(kv.Key);
        foreach (var orphan in vacancy.orphans)
            rivalOf.Remove(orphan);

        vacancies.Add(vacancy);
        BroadcastRivals();
    }

    /// <summary>Server: a replacement player entered the hub mid-game (called by MultiplayerWorld's
    /// MarkReady). They fill the oldest vacancy: their rival = the leaver's old rival, and the
    /// leaver's orphaned opponents get them as the new rival.</summary>
    public void HandleJoinerServer(ulong joinerId)
    {
        if (!IsServer || !rivalsAssigned) return;
        StartCoroutine(FillVacancyWhenTeamKnown(joinerId));
    }

    IEnumerator FillVacancyWhenTeamKnown(ulong joinerId)
    {
        var carManager = GetComponent<RemoteCarManager>();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (carManager != null && carManager.TeamOfServer(joinerId) != 0) break;
            yield return new WaitForSeconds(0.5f);
        }
        if (carManager == null || carManager.TeamOfServer(joinerId) == 0) yield break;

        RivalVacancy vacancy = vacancies.Count > 0 ? vacancies[0] : new RivalVacancy();
        if (vacancies.Count > 0) vacancies.RemoveAt(0);

        // The joiner's own rival: the leaver's old rival if they're still here and still opposing;
        // otherwise a random opposing player.
        ulong rival = vacancy.hasInherited ? vacancy.inheritedRival : 0;
        if (!vacancy.hasInherited || !IsConnected(rival) || rival == joinerId
            || carManager.TeamOfServer(rival) == carManager.TeamOfServer(joinerId))
        {
            if (!TryPickRandomOpposing(joinerId, out rival)) rival = 0;
            else vacancy.hasInherited = true;
        }
        if (vacancy.hasInherited || rival != 0) rivalOf[joinerId] = rival;

        // Everyone who lost the leaver as their rival now hunts the joiner instead.
        foreach (var orphan in vacancy.orphans)
            if (IsConnected(orphan)) rivalOf[orphan] = joinerId;

        BroadcastRivals();
        Debug.Log($"[Scoring] Joiner {joinerId} filled a rival vacancy.");
    }

    static bool IsConnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return false;
        foreach (var id in nm.ConnectedClientsIds)
            if (id == clientId) return true;
        return false;
    }

    bool TryPickRandomOpposing(ulong ofClient, out ulong picked)
    {
        picked = 0;
        var carManager = GetComponent<RemoteCarManager>();
        var nm = NetworkManager.Singleton;
        if (carManager == null || nm == null) return false;
        int myTeam = carManager.TeamOfServer(ofClient);
        var pool = new List<ulong>();
        foreach (var id in nm.ConnectedClientsIds)
            if (id != ofClient && carManager.TeamOfServer(id) != myTeam && carManager.TeamOfServer(id) != 0)
                pool.Add(id);
        if (pool.Count == 0) return false;
        picked = pool[Random.Range(0, pool.Count)];
        return true;
    }

    void BroadcastRivals()
    {
        using (var writer = new FastBufferWriter(512, Allocator.Temp, 8192))
        {
            writer.WriteValueSafe(rivalOf.Count);
            foreach (var kv in rivalOf)
            {
                writer.WriteValueSafe(kv.Key);
                writer.WriteValueSafe(kv.Value);
            }
            SendToRemoteClients(MsgRivals, writer);
        }
        ApplyRivals(new Dictionary<ulong, ulong>(rivalOf));   // host applies its own entry directly
    }

    void ApplyRivals(Dictionary<ulong, ulong> map)
    {
        hasMyRival = map.TryGetValue(LocalClientId, out myRivalClientId);
        var carManager = GetComponent<RemoteCarManager>();
        if (carManager != null) carManager.RefreshMarkers();
        Debug.Log(hasMyRival
            ? $"[Scoring] Your RIVAL is client {myRivalClientId}."
            : "[Scoring] You currently have no rival.");
    }

    void SendRivalBonus(ulong clientId)
    {
        if (clientId == LocalClientId)
        {
            ApplyRivalBonus(rivalBonusCredits);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(rivalBonusCredits);
        msg.SendNamedMessage(MsgRivalBonus, clientId, writer, NetworkDelivery.ReliableSequenced);
    }

    static void ApplyRivalBonus(int credits)
    {
        if (PlayerInventory.Instance == null) return;
        PlayerInventory.Instance.AddCredits(credits);
        AudioManager.PlayKnockoffBounty();   // reward stinger
        Debug.Log($"[Scoring] Beat your RIVAL to the finish — +{credits} credits!");
    }

    /// <summary>Server: score the round that just ended (called ONCE by MultiplayerWorld's loop,
    /// after the round-end broadcast so ending presentations find everyone back in the hub).
    /// A claimed team ⇒ win check at sdItemsToWin; nobody ⇒ a shared drone win, drone ending at
    /// droneWinsToGameOver.</summary>
    public void EvaluateRoundServer()
    {
        if (GameOverServer) return;
        var glm = GameLoopManager.Instance;
        int sdItemsToWin = glm != null ? glm.sdItemsToWin : 3;
        int droneWinsToGameOver = glm != null ? glm.droneWinsToGameOver : 2;

        if (claimedTeam != 0)
        {
            int count = TeamDistinctSds(claimedTeam).Count;
            Debug.Log($"[Scoring] Round won by team {claimedTeam} — {count}/{sdItemsToWin} SDs held.");
            if (count >= sdItemsToWin)
            {
                GameOverServer = true;
                BroadcastEnding(isDroneEnding: false, winningTeam: claimedTeam);
            }
        }
        else
        {
            droneWins++;
            Debug.Log($"[Scoring] Drone win {droneWins}/{droneWinsToGameOver} (no first-place finish).");
            BroadcastScore();
            if (droneWins >= droneWinsToGameOver)
            {
                GameOverServer = true;
                BroadcastEnding(isDroneEnding: true, winningTeam: 0);
            }
        }
        ResetRoundServer();
    }

    // -------------------------------------------------------
    //  Server → client messages
    // -------------------------------------------------------

    void SendSdAward(ulong clientId, string sdName)
    {
        if (clientId == LocalClientId)
        {
            ApplySdAward(sdName);
            return;
        }
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(128, Allocator.Temp);
        writer.WriteValueSafe(sdName);
        msg.SendNamedMessage(MsgSdAward, clientId, writer, NetworkDelivery.ReliableSequenced);
    }

    void BroadcastScore()
    {
        using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
        {
            writer.WriteValueSafe(droneWins);
            SendToRemoteClients(MsgScore, writer);
        }
        ApplyScore(droneWins);
    }

    void BroadcastEnding(bool isDroneEnding, int winningTeam)
    {
        using (var writer = new FastBufferWriter(8, Allocator.Temp))
        {
            writer.WriteValueSafe(isDroneEnding);
            writer.WriteValueSafe(winningTeam);
            SendToRemoteClients(MsgEnding, writer);
        }
        ApplyEnding(isDroneEnding, winningTeam);
    }

    // -------------------------------------------------------
    //  Client-side application
    // -------------------------------------------------------

    void ApplySdAward(string sdName)
    {
        if (PlayerInventory.Instance == null || string.IsNullOrEmpty(sdName)) return;
        PlayerInventory.Instance.Add(sdName, 1);
        Debug.Log($"[Scoring] First place — the server awarded: {sdName}");
        // The inventory change re-reports our SD set to the server (harmless echo of its own book).
    }

    void ApplyScore(int wins)
    {
        var glm = GameLoopManager.Instance;
        if (glm != null) glm.RemoteSetDroneWins(wins);
    }

    void ApplyEnding(bool isDroneEnding, int winningTeam)
    {
        if (MultiplayerWorld.Instance != null)
            MultiplayerWorld.Instance.ApplyEnding(isDroneEnding, winningTeam);
    }

    // -------------------------------------------------------
    //  Messaging plumbing
    // -------------------------------------------------------

    void RegisterHandlers()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[Scoring] No CustomMessagingManager — is NGO running?"); return; }

        msg.RegisterNamedMessageHandler(MsgFinish, (sender, reader) =>
        {
            reader.ReadValueSafe(out bool firstPlace);
            if (IsServer) HandleFinish(sender, firstPlace);
        });
        msg.RegisterNamedMessageHandler(MsgSds, (sender, reader) =>
        {
            reader.ReadValueSafe(out int count);
            var sds = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out string sd);
                sds.Add(sd);
            }
            if (IsServer) HandleSds(sender, sds);
        });
        msg.RegisterNamedMessageHandler(MsgSdAward, (sender, reader) =>
        {
            reader.ReadValueSafe(out string sdName);
            ApplySdAward(sdName);
        });
        msg.RegisterNamedMessageHandler(MsgScore, (sender, reader) =>
        {
            reader.ReadValueSafe(out int wins);
            ApplyScore(wins);
        });
        msg.RegisterNamedMessageHandler(MsgEnding, (sender, reader) =>
        {
            reader.ReadValueSafe(out bool isDroneEnding);
            reader.ReadValueSafe(out int winningTeam);
            ApplyEnding(isDroneEnding, winningTeam);
        });
        msg.RegisterNamedMessageHandler(MsgRivals, (sender, reader) =>
        {
            reader.ReadValueSafe(out int count);
            var map = new Dictionary<ulong, ulong>(count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ulong player);
                reader.ReadValueSafe(out ulong rival);
                map[player] = rival;
            }
            ApplyRivals(map);
        });
        msg.RegisterNamedMessageHandler(MsgRivalBonus, (sender, reader) =>
        {
            reader.ReadValueSafe(out int credits);
            ApplyRivalBonus(credits);
        });
    }

    void UnregisterHandlers()
    {
        var msg = Msg;
        if (msg == null) return;
        msg.UnregisterNamedMessageHandler(MsgFinish);
        msg.UnregisterNamedMessageHandler(MsgSds);
        msg.UnregisterNamedMessageHandler(MsgSdAward);
        msg.UnregisterNamedMessageHandler(MsgScore);
        msg.UnregisterNamedMessageHandler(MsgEnding);
        msg.UnregisterNamedMessageHandler(MsgRivals);
        msg.UnregisterNamedMessageHandler(MsgRivalBonus);
    }

    static void SendToRemoteClients(string messageName, FastBufferWriter writer)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        foreach (var id in nm.ConnectedClientsIds)
            if (id != nm.LocalClientId)
                nm.CustomMessagingManager.SendNamedMessage(messageName, id, writer, NetworkDelivery.ReliableSequenced);
    }
}
