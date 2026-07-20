using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// In-game VOICE CHAT, built on the same custom-message layer as everything else (deliberately no
/// Vivox/dashboard dependency). Two channels:
///
///  • PROXIMITY (default, open mic): your voice plays out of your CAR as 3D audio — a streaming
///    AudioSource on your puppet on every other machine, with distance falloff like the engine
///    sound. Anyone close enough hears you, teammate or not.
///  • TEAM (LB toggles): press Left Bumper to switch to team-direct — 2D audio the host relays ONLY
///    to your teammates; press LB again to drop back to proximity-only. While teammates (including
///    yourself) are team-talking, their names stack in the upper-left of the screen (below the SD
///    HUD), appearing while speaking and vanishing when they stop.
///
/// Capture: Unity Microphone at 16 kHz mono, chopped into 40 ms frames, gated by a simple RMS
/// voice-activity detector (plus hangover) so silence costs zero bandwidth (~32 KB/s only WHILE
/// talking). Playback: per-sender ring buffers feeding streaming AudioClips (audio-thread reads,
/// zero-fill on underrun). The host relays frames — proximity to everyone else, team to the sender's
/// teammates only (it knows teams from the hello roster).
///
/// Settings (both menus' AUDIO panels, persisted in GameSettings): MICROPHONE device picker,
/// MUTE MY MIC (stop transmitting entirely), MUTE PLAYERS (stop hearing everyone). Polled live, so
/// changes apply mid-game.
/// </summary>
public class VoiceChat : MonoBehaviour
{
    const string MsgVoice = "GNRC_VOICE";   // {senderId, mode, sampleCount, pcm16 bytes}

    const int SampleRate = 16000;
    const int FrameSamples = 640;           // 40 ms per frame ⇒ ~25 packets/s while talking
    const byte ModeProximity = 0;
    const byte ModeTeam = 1;

    [Header("Voice Activity Detection")]
    [Tooltip("RMS level a 40 ms frame must exceed to count as speech.")]
    public float vadThreshold = 0.02f;
    [Tooltip("Seconds transmission continues after the level drops (so words aren't clipped).")]
    public float vadHangover = 0.35f;

    [Header("Proximity Playback")]
    [Tooltip("3D falloff distance for proximity voice coming out of the car (units).")]
    public float proximityMaxDistance = 90f;

    [Header("Team-Speaker List (upper-left)")]
    [Tooltip("Screen offset of the speaker list from the top-left corner (below the SD HUD).")]
    public Vector2 speakerListOffset = new Vector2(24f, -190f);
    [Tooltip("Seconds after the last received team frame a name stays on the list.")]
    public float speakerLinger = 0.4f;

    // ---- Capture state ----
    private AudioClip micClip;
    private string micDevice;         // resolved device name ("" = system default)
    private int micReadPos;
    private bool teamMode;            // LB toggle: true = team-direct, false = proximity
    private float vadOpenUntil;
    private float lastSelfTeamSpeak = -10f;
    private float nextSettingsPoll;
    private readonly float[] frame = new float[FrameSamples];
    private float[] micScratch = new float[FrameSamples * 4];

    // ---- Playback state (one per remote sender) ----
    private class RemoteVoice
    {
        public VoiceStream proximity;   // 3D, attached to the sender's puppet car
        public VoiceStream team;        // 2D, attached to the VoiceChat object
        public float lastTeamSpeak = -10f;
    }
    private readonly Dictionary<ulong, RemoteVoice> voices = new Dictionary<ulong, RemoteVoice>();

    // ---- Speaker list UI ----
    private GameObject uiCanvas;
    private TextMeshProUGUI speakerText;
    private string lastSpeakerRender = "";

    static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    static ulong LocalClientId => NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
    static CustomMessagingManager Msg => NetworkManager.Singleton != null ? NetworkManager.Singleton.CustomMessagingManager : null;

    void Start()
    {
        RegisterHandler();
        BuildSpeakerUI();
        RestartMicrophone();
    }

    void OnDestroy()
    {
        var msg = Msg;
        if (msg != null) msg.UnregisterNamedMessageHandler(MsgVoice);
        StopMicrophone();
        if (uiCanvas != null) Destroy(uiCanvas);
    }

    void Update()
    {
        PollSettings();
        PollTeamToggle();
        PumpMicrophone();
        UpdatePlaybackVolumes();
        UpdateSpeakerList();
    }

    // -------------------------------------------------------
    //  Settings (live: device switch + mutes from either menu)
    // -------------------------------------------------------

    void PollSettings()
    {
        if (Time.unscaledTime < nextSettingsPoll) return;
        nextSettingsPoll = Time.unscaledTime + 1f;

        string wanted = GameSettings.VoiceMicDevice;
        bool shouldCapture = !GameSettings.VoiceMuteSelf;
        bool capturing = micClip != null;

        if (shouldCapture && (!capturing || wanted != micDevice)) RestartMicrophone();
        else if (!shouldCapture && capturing) StopMicrophone();
    }

    void RestartMicrophone()
    {
        StopMicrophone();
        if (GameSettings.VoiceMuteSelf) return;
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[VoiceChat] No microphone detected — voice transmission disabled.");
            return;
        }

        micDevice = GameSettings.VoiceMicDevice;
        string device = string.IsNullOrEmpty(micDevice) ? null : micDevice;   // null = system default
        micClip = Microphone.Start(device, true, 1, SampleRate);
        micReadPos = 0;
        if (micClip == null) Debug.LogWarning("[VoiceChat] Microphone failed to start.");
        else Debug.Log($"[VoiceChat] Microphone capturing ({(device ?? "default device")}).");
    }

    void StopMicrophone()
    {
        if (micClip == null) return;
        Microphone.End(string.IsNullOrEmpty(micDevice) ? null : micDevice);
        micClip = null;
    }

    // -------------------------------------------------------
    //  LB: team-direct toggle
    // -------------------------------------------------------

    void PollTeamToggle()
    {
#if ENABLE_INPUT_SYSTEM
        if (MenuState.AnyOpen) return;
        var gp = Gamepad.current;
        if (gp == null || !gp.leftShoulder.wasPressedThisFrame) return;
        teamMode = !teamMode;
        AudioManager.PlayMenuMove();   // audible tick so the toggle registers
        Debug.Log($"[VoiceChat] {(teamMode ? "TEAM-direct voice (2D, teammates only)" : "Proximity voice only")}");
#endif
    }

    // -------------------------------------------------------
    //  Capture → VAD → frames out
    // -------------------------------------------------------

    void PumpMicrophone()
    {
        if (micClip == null) return;
        int micPos = Microphone.GetPosition(string.IsNullOrEmpty(micDevice) ? null : micDevice);
        if (micPos < 0) return;

        int available = micPos - micReadPos;
        if (available < 0) available += micClip.samples;   // looped
        if (available < FrameSamples) return;
        if (micScratch.Length < available) micScratch = new float[available];

        // Pull everything new since last pump (handles the ring wrap), then emit whole frames.
        if (micReadPos + available <= micClip.samples)
        {
            micClip.GetData(micScratch, micReadPos);
        }
        else
        {
            int tail = micClip.samples - micReadPos;
            var tailBuf = new float[tail];
            micClip.GetData(tailBuf, micReadPos);
            var headBuf = new float[available - tail];
            micClip.GetData(headBuf, 0);
            System.Array.Copy(tailBuf, 0, micScratch, 0, tail);
            System.Array.Copy(headBuf, 0, micScratch, tail, headBuf.Length);
        }

        int frames = available / FrameSamples;
        for (int f = 0; f < frames; f++)
        {
            System.Array.Copy(micScratch, f * FrameSamples, frame, 0, FrameSamples);
            ProcessOutgoingFrame(frame);
        }
        micReadPos = (micReadPos + frames * FrameSamples) % micClip.samples;
    }

    void ProcessOutgoingFrame(float[] samples)
    {
        // Simple RMS gate with hangover: silence is never transmitted.
        float sum = 0f;
        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
        float rms = Mathf.Sqrt(sum / samples.Length);
        if (rms >= vadThreshold) vadOpenUntil = Time.unscaledTime + vadHangover;
        if (Time.unscaledTime > vadOpenUntil) return;

        byte mode = teamMode ? ModeTeam : ModeProximity;
        if (mode == ModeTeam) lastSelfTeamSpeak = Time.unscaledTime;

        var payload = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            payload[i * 2] = (byte)(s & 0xFF);
            payload[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        if (IsServer) RelayFrame(LocalClientId, mode, payload);   // host: straight to the recipients
        else SendFrameToServer(mode, payload);
    }

    void SendFrameToServer(byte mode, byte[] payload)
    {
        var msg = Msg;
        if (msg == null) return;
        using var writer = new FastBufferWriter(payload.Length + 16, Allocator.Temp);
        writer.WriteValueSafe(LocalClientId);
        writer.WriteValueSafe(mode);
        writer.WriteValueSafe((ushort)payload.Length);
        writer.WriteBytesSafe(payload, payload.Length);
        msg.SendNamedMessage(MsgVoice, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
    }

    // -------------------------------------------------------
    //  Host relay + reception
    // -------------------------------------------------------

    void RegisterHandler()
    {
        var msg = Msg;
        if (msg == null) { Debug.LogWarning("[VoiceChat] No CustomMessagingManager — is NGO running?"); return; }
        msg.RegisterNamedMessageHandler(MsgVoice, (sender, reader) =>
        {
            reader.ReadValueSafe(out ulong senderId);
            reader.ReadValueSafe(out byte mode);
            reader.ReadValueSafe(out ushort length);
            var payload = new byte[length];
            reader.ReadBytesSafe(ref payload, length);

            if (IsServer && sender == senderId) RelayFrame(senderId, mode, payload);
            if (senderId != LocalClientId) PlayIncomingFrame(senderId, mode, payload);
        });
    }

    /// <summary>Host: forward a frame to its audience — proximity to every OTHER client (their 3D
    /// falloff decides who actually hears it), team only to the sender's teammates.</summary>
    void RelayFrame(ulong senderId, byte mode, byte[] payload)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null) return;
        var carManager = GetComponent<RemoteCarManager>();
        int senderTeam = carManager != null ? carManager.TeamOfServer(senderId) : 0;

        using var writer = new FastBufferWriter(payload.Length + 16, Allocator.Temp);
        writer.WriteValueSafe(senderId);
        writer.WriteValueSafe(mode);
        writer.WriteValueSafe((ushort)payload.Length);
        writer.WriteBytesSafe(payload, payload.Length);

        foreach (var id in nm.ConnectedClientsIds)
        {
            if (id == nm.LocalClientId || id == senderId) continue;
            if (mode == ModeTeam && carManager != null && carManager.TeamOfServer(id) != senderTeam) continue;
            nm.CustomMessagingManager.SendNamedMessage(MsgVoice, id, writer, NetworkDelivery.Unreliable);
        }

        // The host is a listener too (its own copy never loops back through the network).
        if (senderId != nm.LocalClientId)
        {
            bool hostMayHear = mode == ModeProximity
                || (carManager != null && carManager.TeamOfServer(nm.LocalClientId) == senderTeam);
            if (hostMayHear) PlayIncomingFrame(senderId, mode, payload);
        }
    }

    void PlayIncomingFrame(ulong senderId, byte mode, byte[] payload)
    {
        if (GameSettings.VoiceMuteOthers) return;

        if (!voices.TryGetValue(senderId, out var voice))
            voices[senderId] = voice = new RemoteVoice();

        int count = payload.Length / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            short s = (short)(payload[i * 2] | (payload[i * 2 + 1] << 8));
            samples[i] = s / (float)short.MaxValue;
        }

        if (mode == ModeTeam)
        {
            voice.lastTeamSpeak = Time.unscaledTime;
            if (voice.team == null) voice.team = new VoiceStream();
            if (voice.team.Source == null) voice.team.Attach(gameObject, spatial: false, proximityMaxDistance);
            voice.team.Enqueue(samples);
        }
        else
        {
            // Proximity voice comes OUT OF THE CAR: attach (lazily, and re-attach if the puppet was
            // rebuilt) to the sender's puppet so Unity's 3D falloff does the "proximity" part.
            var remote = PlayerRegistry.FindRemote(senderId);
            if (remote == null || remote.Car == null) return;   // no car to speak from — drop
            if (voice.proximity == null) voice.proximity = new VoiceStream();
            if (voice.proximity.Source == null) voice.proximity.Attach(remote.Car, spatial: true, proximityMaxDistance);
            voice.proximity.Enqueue(samples);
        }
    }

    void UpdatePlaybackVolumes()
    {
        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
        foreach (var voice in voices.Values)
        {
            if (voice.proximity?.Source != null) voice.proximity.Source.volume = sfx;
            if (voice.team?.Source != null) voice.team.Source.volume = sfx;
        }
    }

    // -------------------------------------------------------
    //  Team-speaker name list (upper-left, below the SD HUD)
    // -------------------------------------------------------

    void BuildSpeakerUI()
    {
        uiCanvas = new GameObject("VoiceChatCanvas");
        DontDestroyOnLoad(uiCanvas);
        var canvas = uiCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 140;   // under the gameplay HUDs (150)
        var scaler = uiCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
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
        rt.anchoredPosition = speakerListOffset;
        speakerText.text = "";
    }

    void UpdateSpeakerList()
    {
        if (speakerText == null) return;

        var lines = new List<string>();
        float now = Time.unscaledTime;

        if (now - lastSelfTeamSpeak <= speakerLinger)
        {
            string self = NetworkSessionManager.Instance != null
                ? NetworkSessionManager.Instance.LocalPlayerName : "YOU";
            lines.Add("< " + self);
        }
        foreach (var kv in voices)
        {
            if (now - kv.Value.lastTeamSpeak > speakerLinger) continue;
            var remote = PlayerRegistry.FindRemote(kv.Key);
            lines.Add("< " + (remote != null && !string.IsNullOrEmpty(remote.Name) ? remote.Name : "PLAYER"));
        }

        string render = string.Join("\n", lines);
        if (render == lastSpeakerRender) return;
        lastSpeakerRender = render;
        speakerText.text = render;
    }

    // -------------------------------------------------------
    //  Streaming playback: ring buffer → streaming AudioClip (audio-thread reads)
    // -------------------------------------------------------

    class VoiceStream
    {
        public AudioSource Source { get; private set; }

        private readonly float[] ring = new float[SampleRate * 4];   // 4 s of headroom
        private int readPos, writePos, buffered;
        private readonly object gate = new object();

        public void Attach(GameObject host, bool spatial, float maxDistance)
        {
            Source = host.AddComponent<AudioSource>();
            var clip = AudioClip.Create("VoiceStream", SampleRate, 1, SampleRate, true, OnPcmRead);
            Source.clip = clip;
            Source.loop = true;
            Source.playOnAwake = false;
            Source.spatialBlend = spatial ? 1f : 0f;
            Source.rolloffMode = AudioRolloffMode.Linear;
            Source.minDistance = 4f;
            Source.maxDistance = maxDistance;
            Source.dopplerLevel = 0f;
            Source.Play();
        }

        public void Enqueue(float[] samples)
        {
            lock (gate)
            {
                foreach (var s in samples)
                {
                    if (buffered >= ring.Length) break;   // overflow: drop newest
                    ring[writePos] = s;
                    writePos = (writePos + 1) % ring.Length;
                    buffered++;
                }
            }
        }

        // Audio thread: no Unity API in here.
        void OnPcmRead(float[] data)
        {
            lock (gate)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (buffered > 0)
                    {
                        data[i] = ring[readPos];
                        readPos = (readPos + 1) % ring.Length;
                        buffered--;
                    }
                    else
                    {
                        data[i] = 0f;   // underrun: silence
                    }
                }
            }
        }
    }
}
