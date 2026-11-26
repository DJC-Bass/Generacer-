using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Vivox;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// In-game VOICE CHAT built on UNITY VIVOX (replaces the old DIY custom-message system). Vivox owns
/// capture, encode, echo-cancellation/noise-suppression, transport (its own voice servers — NOT the
/// NGO host relay) and 3D spatialization, so all the sample-rate/relay problems of the hand-rolled
/// version are gone. Two channels, matching the project's design:
///
///  • PROXIMITY (default, open mic): a Vivox POSITIONAL channel. Each client reports its OWN car
///    position every ~0.1 s via Set3DPosition — or its SUPPORT SHIP's, while flying one, since that is
///    where the player actually is (see VoiceSource); Vivox mixes what everyone hears from those positions
///    with distance falloff (full volume within <see cref="ConversationalDistance"/>, silent past
///    <see cref="AudibleDistance"/>). No per-puppet AudioSources needed — Vivox does the panning.
///  • TEAM (LB toggles): a Vivox 2D GROUP channel, one per team, joined only by that team. You stay
///    JOINED to both channels (so you always HEAR nearby proximity + your teammates); the LB toggle
///    only changes which channel you TRANSMIT into (SetChannelTransmissionMode Single).
///
/// This is a PERSISTENT, self-bootstrapped singleton (like AudioManager) rather than a match-scoped
/// component, because the AUDIO-settings microphone picker needs Vivox's device list, and Vivox can
/// only enumerate devices AFTER InitializeAsync. So we init Vivox early (at boot, right after UGS
/// auth); the login happens when the first multiplayer match begins (<see cref="BeginMatch"/>) and is
/// KEPT from then on — each match only joins and leaves its channels (see <see cref="EnsureLoggedInAsync"/>
/// for why). While a match runs, <see cref="KeepConnected"/> rejoins anything that drops.
///
/// PREREQUISITE: Vivox must be enabled for the project in the Unity Cloud Dashboard (it auto-provisions
/// credentials tied to the existing UGS project). Without it, InitializeAsync/LoginAsync throw — every
/// call here is guarded so that degrades to "no voice" instead of crashing.
///
/// Settings (both menus' AUDIO panels, persisted in GameSettings, polled live so changes apply
/// mid-game): MICROPHONE device picker (Vivox devices, see <see cref="InputDeviceNames"/>), MUTE MY
/// MIC (MuteInputDevice), MUTE PLAYERS (MuteOutputDevice). Voice loudness follows the SFX slider.
/// </summary>
public class VoiceService : MonoBehaviour
{
    public static VoiceService Instance { get; private set; }

    // ---- Positional (proximity) tuning — "distance units" ≈ metres, same scale as the world.
    //      >>> THIS is where the 3D proximity Min/Max distance lives (dev-tuned, applied at channel
    //      join). To change the feel, edit these two and rejoin a match. <<<
    const int AudibleDistance = 400;         // MAX distance — voice is silent beyond this
    const int ConversationalDistance = 200;  // MIN distance — full volume within, fades past here
    const float PositionalUpdateInterval = 0.1f;   // 10 Hz Set3DPosition — plenty for smooth falloff

    // ---- Speaker list UI ----
    // Below the driver portrait box (bottom edge at y -173), with room for portrait art that spills
    // out underneath it.
    static readonly Vector2 SpeakerListOffset = new Vector2(24f, -(CharacterPortraitHUD.BoxBottom + 40f));
    const float SpeakerLinger = 0.4f;        // seconds a team-talker's name lingers after they stop

    // ---- Vivox lifecycle ----
    Task initTask;                           // cached so concurrent callers await the SAME init
    bool vivoxInitialized;

    // ---- Match / channel state ----
    bool matchActive;
    int matchId;                             // bumped by every start and stop, so stale queued steps stand down
    string sessionId;
    string proximityChannel;
    string teamChannel;                      // null until the local team is known
    bool teamMode;                           // LB toggle: transmit team (true) vs proximity (false)
    float nextPositionalUpdate;

    // ---- Connection upkeep (login + joins, retried until they hold) ----
    const float ConnectCheckInterval = 0.5f;
    const float ReconnectDelay = 3f;         // after an attempt, before the next one...
    const float MaxReconnectDelay = 30f;     // ...doubling with each failure in a row, up to this
    Task lifecycle = Task.CompletedTask;     // login / join / leave steps, run one after another
    bool connecting;                         // a connect step is queued or running
    int connectFailures;                     // in a row
    float nextConnectCheck;
    string loginName;                        // the display name the current Vivox login was made with
    int statusRound = -1;                    // last round whose voice status was logged

    // ---- Live-settings mirror (GameSettings → Vivox), so we only call on change ----
    float nextSettingsPoll;
    string appliedMic = "\0";           // sentinel so the first poll always applies
    bool appliedMuteSelf, appliedMuteOthers;
    int appliedVoiceVolume = int.MinValue;   // last-applied per-channel voice volume (−50..+50)

    // ---- Speaker list ----
    GameObject uiCanvas;
    TextMeshProUGUI speakerText;
    string lastSpeakerRender = "";
    readonly Dictionary<string, SpeakerEntry> teamSpeakers = new Dictionary<string, SpeakerEntry>();
    struct SpeakerEntry { public string name; public float lastSpoke; }

    // =====================================================================
    //  Bootstrap
    // =====================================================================

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("VoiceService");
        DontDestroyOnLoad(go);
        go.AddComponent<VoiceService>();   // Awake wires up Instance
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    async void Start()
    {
        BuildSpeakerUI();
        // Kick off Vivox init in the background so the mic picker has a device list by the time the
        // player reaches the AUDIO settings. Failures are swallowed (see EnsureVivoxAsync).
        await EnsureVivoxAsync();
    }

    void OnDestroy()
    {
        if (uiCanvas != null) Destroy(uiCanvas);
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        PollSettings();
        if (matchActive)
        {
            KeepConnected();
            PollTeamToggle();
            UpdatePositional();
            LogRoundStatus();
        }
        UpdateSpeakerList();
    }

    // =====================================================================
    //  Vivox init (early, for the device list) + UGS auth it rides on
    // =====================================================================

    Task EnsureVivoxAsync()
    {
        if (vivoxInitialized) return Task.CompletedTask;
        if (initTask == null) initTask = InitVivoxAsync();
        return initTask;
    }

    async Task InitVivoxAsync()
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            await VivoxService.Instance.InitializeAsync();
            vivoxInitialized = true;
            // Keep the global OUTPUT device at neutral — the Voice slider works via per-CHANNEL volume
            // (remote players only), so it never touches capture/mic gain or the local output master.
            try { VivoxService.Instance.SetOutputDeviceVolume(0); } catch { }
            // The saved mic, mutes and volume go to Vivox once logged in (EnsureLoggedInAsync).
            Debug.Log("[VoiceService] Vivox initialized. Input devices: " +
                      (InputDeviceNames.Length > 0 ? string.Join(", ", InputDeviceNames) : "(none)"));
        }
        catch (Exception e)
        {
            initTask = null;   // allow a later retry (e.g. when a match starts)
            Debug.LogWarning("[VoiceService] Vivox init failed — voice disabled. Is Vivox enabled for " +
                             "this project in the Unity Cloud Dashboard? Details: " + e.Message);
        }
    }

    // =====================================================================
    //  Device list for the settings menus
    // =====================================================================

    /// <summary>Vivox input-device friendly names for the AUDIO-panel MICROPHONE picker. Empty until
    /// Vivox has finished initializing (which starts at boot) — the menus prepend their own "Default"
    /// entry, so an empty list still offers the system default.</summary>
    public static string[] InputDeviceNames
    {
        get
        {
            if (Instance == null || !Instance.vivoxInitialized) return Array.Empty<string>();
            try
            {
                var devices = VivoxService.Instance.AvailableInputDevices;
                var names = new string[devices.Count];
                for (int i = 0; i < devices.Count; i++) names[i] = devices[i].DeviceName;
                return names;
            }
            catch { return Array.Empty<string>(); }
        }
    }

    // =====================================================================
    //  Match lifecycle (called by MultiplayerWorld)
    // =====================================================================

    /// <summary>Begin voice for a multiplayer match: <see cref="KeepConnected"/> logs in and joins the
    /// proximity channel from the next frame, and the team channel once the local team is resolved. Safe
    /// to call before Vivox has finished init.</summary>
    public static void BeginMatch()
    {
        if (Instance != null) Instance.StartMatch();
    }

    /// <summary>End voice for the match: leave its channels (staying logged in). Called from the MP
    /// teardown.</summary>
    public static void EndMatch()
    {
        if (Instance != null) Instance.StopMatch();
    }

    void StartMatch()
    {
        if (matchActive) return;             // already running
        var nsm = NetworkSessionManager.Instance;
        sessionId = Sanitize(nsm != null && nsm.Session != null ? nsm.Session.Id : "solo");
        proximityChannel = "prox_" + sessionId;
        teamChannel = null;
        teamMode = false;                    // open mic into proximity; LB re-points it to the team
        matchActive = true;
        matchId++;
        nextConnectCheck = 0f;
        connectFailures = 0;
        statusRound = -1;
    }

    void StopMatch()
    {
        matchActive = false;
        matchId++;
        teamMode = false;
        proximityChannel = null;
        teamChannel = null;
        teamSpeakers.Clear();
        Queue(LeaveChannelsAsync);
    }

    /// <summary>Gets this match's voice up and KEEPS it up: logged in, in the proximity channel, and in
    /// the team channel once the team is known. Whenever any of that is missing — the match just started,
    /// the team assignment just landed, a join failed, Vivox dropped us and couldn't recover — it
    /// reconnects, a few seconds apart, so one failed attempt can't leave a player silent for the rest of
    /// the game.</summary>
    void KeepConnected()
    {
        if (connecting || Time.unscaledTime < nextConnectCheck) return;
        nextConnectCheck = Time.unscaledTime + ConnectCheckInterval;

        if (teamChannel == null)   // teams come from session properties, which may lag the match start
        {
            int team = NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalTeam() : 0;
            if (team != 0) teamChannel = "team_" + sessionId + "_" + team;
        }
        if (vivoxInitialized && VivoxService.Instance.IsLoggedIn && InChannel(proximityChannel)
            && (teamChannel == null || InChannel(teamChannel))) return;

        nextConnectCheck = Time.unscaledTime + ReconnectDelay;
        connecting = true;
        int match = matchId;
        Queue(() => ConnectAsync(match));
    }

    async Task ConnectAsync(int match)
    {
        try
        {
            await EnsureVivoxAsync();
            if (match != matchId) return;   // the match is over
            if (!vivoxInitialized) { RetryLater(); return; }   // init already logged why
            await EnsureLoggedInAsync();

            string proximity = proximityChannel;
            if (match == matchId && !InChannel(proximity))
            {
                await VivoxService.Instance.JoinPositionalChannelAsync(
                    proximity, ChatCapability.AudioOnly,
                    new Channel3DProperties(AudibleDistance, ConversationalDistance, 1.0f,
                                            AudioFadeModel.LinearByDistance));
                ApplyVoiceVolumeToChannel(proximity);   // remote players come in at the saved level
                Debug.Log($"[VoiceService] Joined proximity channel '{proximity}'.");
            }

            string team = teamChannel;
            if (match == matchId && team != null && !InChannel(team))
            {
                await VivoxService.Instance.JoinGroupChannelAsync(team, ChatCapability.AudioOnly);
                ApplyVoiceVolumeToChannel(team);        // remote teammates come in at the saved level
                Debug.Log($"[VoiceService] Joined team channel '{team}'.");
            }

            // Talk into whichever channel the LB toggle says, re-asserted on every (re)connect.
            if (match == matchId)
                await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single,
                    teamMode && InChannel(teamChannel) ? teamChannel : proximityChannel);
            connectFailures = 0;
        }
        catch (Exception e)
        {
            float delay = RetryLater();
            Debug.LogWarning($"[VoiceService] Voice connect failed — retrying in {delay:F0} s: " + e.Message);
        }
        finally
        {
            connecting = false;
        }
    }

    float RetryLater()
    {
        float delay = Mathf.Min(ReconnectDelay * (1 << Mathf.Min(connectFailures, 4)), MaxReconnectDelay);
        connectFailures++;
        nextConnectCheck = Time.unscaledTime + delay;
        return delay;
    }

    /// <summary>Logs in to Vivox unless already logged in — and STAYS logged in from match to match.
    ///
    /// ⚠️ Logging out at the end of every match is what made voice settings inconsistent between games.
    /// Vivox sends the mic choice and the mutes to the CURRENT LOGIN: a change made in the menus between
    /// matches went to the logged-out login and failed ("Target Object Does Not Exist"), and nothing sent
    /// the saved settings to each match's new login. Now there is one login for the whole run of the
    /// game, every setting is sent to it right after it's made, and it carries into every match after.
    ///
    /// Logs in afresh only when the player's name has changed (Vivox shows the name given at login) or
    /// Vivox dropped the login itself.</summary>
    async Task EnsureLoggedInAsync()
    {
        var vivox = VivoxService.Instance;
        var nsm = NetworkSessionManager.Instance;
        string displayName = nsm != null ? nsm.LocalPlayerName : "PLAYER";
        if (vivox.IsLoggedIn && displayName == loginName) return;

        if (vivox.IsLoggedIn) await vivox.LogoutAsync();
        await vivox.LoginAsync(new LoginOptions { DisplayName = displayName });
        loginName = displayName;
        ApplySettings(force: true);   // before any channel is joined
        Debug.Log($"[VoiceService] Logged in to Vivox as '{displayName}'.");
    }

    async Task LeaveChannelsAsync()
    {
        // Logged out, there's nothing to leave — and Vivox would log in just to leave it.
        if (!vivoxInitialized || !VivoxService.Instance.IsLoggedIn) return;
        try
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VoiceService] Voice teardown: " + e.Message);
        }
    }

    /// <summary>Runs <paramref name="step"/> once every step queued before it has finished. Login, joins
    /// and leaves are all awaited calls, and run interleaved they undo each other: a game starting before
    /// the last one's leave had finished could join its channel only to be pulled straight back out.</summary>
    void Queue(Func<Task> step) => lifecycle = RunAfter(lifecycle, step);

    static async Task RunAfter(Task previous, Func<Task> step)
    {
        try { await previous; } catch { /* already logged by the step */ }
        await step();
    }

    /// <summary>Are we in this channel, fully connected? (Vivox lists a channel only once it is.)</summary>
    static bool InChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return false;
        try { return VivoxService.Instance.ActiveChannels.ContainsKey(channel); }
        catch { return false; }
    }

    /// <summary>One line per round with where this player's voice stands, so a playtest's log shows what
    /// voice looked like on both ends whenever someone couldn't be heard.</summary>
    void LogRoundStatus()
    {
        var glm = GameLoopManager.Instance;
        int round = glm != null ? glm.RoundNumber : 0;
        if (round == statusRound || connecting) return;
        statusRound = round;

        if (!vivoxInitialized) { Debug.Log($"[VoiceService] Round {round}: no voice (Vivox not initialized)."); return; }
        try
        {
            var vivox = VivoxService.Instance;
            var mic = vivox.EffectiveInputDevice;
            string team = teamChannel == null ? "no team yet" : InChannel(teamChannel) ? "in team" : "NOT in team";
            Debug.Log($"[VoiceService] Round {round}: {(vivox.IsLoggedIn ? "logged in" : "NOT logged in")}, " +
                      $"{(InChannel(proximityChannel) ? "in proximity" : "NOT in proximity")}, {team}; " +
                      $"talking to {(teamMode ? "TEAM ONLY" : "proximity")}; mic '{(mic != null ? mic.DeviceName : "none")}'" +
                      $"{(vivox.IsInputDeviceMuted ? " (MUTED)" : "")}{(vivox.IsOutputDeviceMuted ? "; other players MUTED" : "")}.");
        }
        catch (Exception e) { Debug.LogWarning("[VoiceService] Voice status unavailable: " + e.Message); }
    }

    // =====================================================================
    //  Proximity position + LB team toggle
    // =====================================================================

    void UpdatePositional()
    {
        if (Time.unscaledTime < nextPositionalUpdate) return;
        nextPositionalUpdate = Time.unscaledTime + PositionalUpdateInterval;
        // Only once joined: logged out, Set3DPosition would have Vivox log in by itself, nameless.
        if (!InChannel(proximityChannel)) return;

        var speaker = VoiceSource();
        if (speaker == null) return;
        try { VivoxService.Instance.Set3DPosition(speaker, proximityChannel); }
        catch { /* channel may be tearing down between frames — ignore */ }
    }

    /// <summary>The object our proximity voice comes OUT of — and, just as importantly, the object we
    /// HEAR from. Set3DPosition sets both the speaker and the listener pose from one transform.
    ///
    /// Normally our car. But a Support Ship pilot's car is parked on the hub pad while they are flying
    /// ~100 km away over the track, and AudibleDistance is 400 — so reporting the car did not make them
    /// quiet, it made them SILENT IN BOTH DIRECTIONS. The racer being escorted and the teammate
    /// escorting them, the two people most likely to need to talk, could not hear each other at all.
    ///
    /// ⚠️ Note this is NOT covered by the AudioListener PilotControlCenter moves onto the ship.
    /// Vivox does its own spatialization from these coordinates and never consults Unity's listener, so
    /// the two have to be pointed at the ship separately.</summary>
    GameObject VoiceSource()
    {
        ulong piloted = SupportShipReplicator.LocalPilotOf;
        if (piloted != SupportShipReplicator.NoClient)
        {
            var ship = SupportShipReplicator.GetShip(piloted);
            if (ship != null) return ship.gameObject;
        }
        return PlayerRegistry.LocalCar;
    }

    void PollTeamToggle()
    {
#if ENABLE_INPUT_SYSTEM
        if (teamChannel == null || proximityChannel == null) return;

        // MenuState.AnyOpen is doing double duty, and this is the one place that matters. It means "a
        // menu has the controls", but PilotControlCenter also raises it purely to stop the pilot's
        // parked car driving off under the stick that is flying the ship. Voice belongs on the other
        // side of that line: a pilot is the player most likely to need team chat, and blocking LB for
        // them defeated the point of moving the ship's roll onto the triggers to free it.
        bool piloting = SupportShipReplicator.LocalPilotOf != SupportShipReplicator.NoClient;
        if (MenuState.AnyOpen && !piloting) return;
        var gp = Gamepad.current;
        if (gp == null || !gp.leftShoulder.wasPressedThisFrame) return;
        if (!InChannel(teamChannel)) return;   // known but not joined yet

        teamMode = !teamMode;
        AudioManager.PlayMenuMove();   // audible tick so the toggle registers
        string target = teamMode ? teamChannel : proximityChannel;
        _ = VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, target);
        Debug.Log($"[VoiceService] {(teamMode ? "TEAM-direct voice (2D, teammates only)" : "Proximity voice only")}");
#endif
    }

    // =====================================================================
    //  Live settings: mic device, mutes, voice volume (GameSettings → Vivox)
    // =====================================================================

    void PollSettings()
    {
        if (!vivoxInitialized) return;
        if (Time.unscaledTime < nextSettingsPoll) return;
        nextSettingsPoll = Time.unscaledTime + 1f;
        ApplySettings(force: false);
    }

    /// <summary>Sends the saved mic, mutes and voice volume to Vivox — only while logged in, since Vivox
    /// sends them to the current login (see <see cref="EnsureLoggedInAsync"/>). A change made before the
    /// first login waits for it: the login applies everything with <paramref name="force"/>.</summary>
    void ApplySettings(bool force)
    {
        if (!vivoxInitialized) return;
        try
        {
            if (!VivoxService.Instance.IsLoggedIn) return;

            // Forced (a new login), each mute is first flipped the other way: Vivox skips a mute call
            // that matches the last one it was given — a memory that outlives the login it was given to.
            bool muteSelf = GameSettings.VoiceMuteSelf;
            if (force || muteSelf != appliedMuteSelf)
            {
                appliedMuteSelf = muteSelf;
                if (force) SetInputMuted(!muteSelf);
                SetInputMuted(muteSelf);
            }

            bool muteOthers = GameSettings.VoiceMuteOthers;
            if (force || muteOthers != appliedMuteOthers)
            {
                appliedMuteOthers = muteOthers;
                if (force) SetOutputMuted(!muteOthers);
                SetOutputMuted(muteOthers);
            }

            string mic = GameSettings.VoiceMicDevice ?? "";
            if (force || mic != appliedMic)
            {
                appliedMic = mic;
                ApplyMicDevice(mic);
            }

            ApplyVoiceVolume(force);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VoiceService] Applying voice settings failed: " + e.Message);
        }
    }

    /// <summary>Apply the saved VOICE volume to OTHER players' incoming audio — Vivox per-CHANNEL volume
    /// on every channel we're in. This is local-only playback gain on the REMOTE participants, so it can
    /// NEVER affect our own microphone, capture sensitivity, or what we transmit. Mapped onto the SDK's
    /// −50..+50 range: ≈0.5 = neutral, &gt;0.5 boosts quiet remote mics, &lt;0.5 quietens them. Guarded so
    /// the poll / live slider-drag only calls the SDK when the mapped step actually changes.</summary>
    void ApplyVoiceVolume(bool force)
    {
        if (!vivoxInitialized) return;
        int vol = MapVoiceVolume();
        if (!force && vol == appliedVoiceVolume) return;
        appliedVoiceVolume = vol;
        try
        {
            foreach (var channel in VivoxService.Instance.ActiveChannels.Keys)
                _ = VivoxService.Instance.SetChannelVolumeAsync(channel, vol);
        }
        catch (Exception e) { Debug.LogWarning("[VoiceService] Voice volume apply failed: " + e.Message); }
    }

    /// <summary>Apply the current VOICE volume to ONE freshly-joined channel — the change-guarded
    /// <see cref="ApplyVoiceVolume"/> won't re-touch existing channels on its behalf, so each join calls
    /// this so the new channel's remote players come in at the chosen level.</summary>
    void ApplyVoiceVolumeToChannel(string channel)
    {
        if (!vivoxInitialized || string.IsNullOrEmpty(channel)) return;
        try { _ = VivoxService.Instance.SetChannelVolumeAsync(channel, MapVoiceVolume()); }
        catch (Exception e) { Debug.LogWarning("[VoiceService] Voice volume apply (channel) failed: " + e.Message); }
    }

    // Saved 0..1 slider → Vivox's −50..+50 per-channel volume (0.5 ≈ neutral, 1 = max boost of others).
    static int MapVoiceVolume() => Mathf.RoundToInt(Mathf.Lerp(-50f, 50f, Mathf.Clamp01(GameSettings.VoiceVolume)));

    /// <summary>Live-apply the saved VOICE volume immediately — called by the AUDIO-menu voice slider as
    /// it drags, so the change is audible without waiting for the 1 s settings poll.</summary>
    public static void ApplyVoiceVolumeLive()
    {
        if (Instance != null) Instance.ApplyVoiceVolume(false);
    }

    static void SetInputMuted(bool muted)
    {
        if (muted) VivoxService.Instance.MuteInputDevice();
        else VivoxService.Instance.UnmuteInputDevice();
    }

    static void SetOutputMuted(bool muted)
    {
        if (muted) VivoxService.Instance.MuteOutputDevice();
        else VivoxService.Instance.UnmuteOutputDevice();
    }

    /// <summary>Vivox's own device entry that follows whatever Windows' default recording device is.</summary>
    const string SystemDefaultDevice = "Default System Device";

    void ApplyMicDevice(string deviceName)
    {
        // "" (the menus' DEFAULT) is set explicitly too, so picking DEFAULT after a named mic switches back.
        string wanted = string.IsNullOrEmpty(deviceName) ? SystemDefaultDevice : deviceName;
        foreach (var d in VivoxService.Instance.AvailableInputDevices)
        {
            if (d.DeviceName == wanted) { _ = SetMicAsync(d); return; }
        }
        Debug.LogWarning($"[VoiceService] Saved mic '{wanted}' not found among Vivox devices — mic left as it was.");
    }

    static async Task SetMicAsync(VivoxInputDevice device)
    {
        try
        {
            await VivoxService.Instance.SetActiveInputDeviceAsync(device);
            Debug.Log($"[VoiceService] Mic set to '{device.DeviceName}'.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VoiceService] Setting the mic to '{device.DeviceName}' failed: " + e.Message);
        }
    }

    // =====================================================================
    //  Team-speaker name list (upper-left, below the SD HUD)
    // =====================================================================

    void BuildSpeakerUI()
    {
        uiCanvas = new GameObject("VoiceChatCanvas");
        DontDestroyOnLoad(uiCanvas);
        var canvas = uiCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 140;   // under the gameplay HUDs (150)
        var scaler = uiCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var go = new GameObject("Speakers", typeof(RectTransform));
        go.transform.SetParent(uiCanvas.transform, false);
        speakerText = go.AddComponent<TextMeshProUGUI>();
        speakerText.fontSize = 26f;
        speakerText.fontStyle = FontStyles.Bold;
        speakerText.alignment = TextAlignmentOptions.TopLeft;
        speakerText.color = new Color(0.45f, 0.95f, 1f, 0.95f);
        speakerText.enableWordWrapping = false;
        var rt = speakerText.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(420f, 300f);
        rt.anchoredPosition = SpeakerListOffset;
        speakerText.text = "";

        UiLayer.Apply(uiCanvas);   // keep code-built UI off the Default layer (see UiLayer)
    }

    void UpdateSpeakerList()
    {
        if (speakerText == null) return;
        float now = Time.unscaledTime;

        // Refresh last-spoke times from the team channel's live participant list.
        if (matchActive && vivoxInitialized && teamChannel != null)
        {
            try
            {
                if (VivoxService.Instance.ActiveChannels.TryGetValue(teamChannel, out var participants))
                {
                    foreach (var p in participants)
                    {
                        if (!p.SpeechDetected) continue;
                        string name = p.IsSelf ? SelfName() : (string.IsNullOrEmpty(p.DisplayName) ? "PLAYER" : p.DisplayName);
                        teamSpeakers[p.PlayerId] = new SpeakerEntry { name = name, lastSpoke = now };
                    }
                }
            }
            catch { /* channel dictionary in flux — skip this frame */ }
        }

        // Render everyone still within the linger window; drop the stale ones.
        var lines = new List<string>();
        var expired = new List<string>();
        foreach (var kv in teamSpeakers)
        {
            if (now - kv.Value.lastSpoke > SpeakerLinger) { expired.Add(kv.Key); continue; }
            lines.Add("< " + kv.Value.name);
        }
        foreach (var key in expired) teamSpeakers.Remove(key);

        string render = string.Join("\n", lines);
        if (render == lastSpeakerRender) return;
        lastSpeakerRender = render;
        speakerText.text = render;
    }

    static string SelfName()
    {
        return NetworkSessionManager.Instance != null ? NetworkSessionManager.Instance.LocalPlayerName : "YOU";
    }

    // Vivox channel names must be URI-safe — keep alphanumerics only (session ids are GUID-like).
    static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "solo";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
        return sb.Length > 0 ? sb.ToString() : "solo";
    }
}
