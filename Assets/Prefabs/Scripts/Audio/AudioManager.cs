using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persistent audio hub for the whole game. Self-bootstrapped before the first scene loads (no
/// scene setup, no component to place), it owns one looping MUSIC source and one one-shot SFX
/// source and pulls every clip from <c>Resources/AudioLibrary</c>.
///
/// Music is driven per-scene: the menu scenes play the looping menu song; every other scene stops
/// it (gameplay music can be slotted in later). <see cref="PlayMusic"/> is a no-op when the song
/// asked for is already playing, so moving Main Menu &lt;-&gt; Car Selection keeps the track going
/// seamlessly instead of restarting it.
///
/// SFX are fire-and-forget via <see cref="PlaySfx"/> and the static PlayMenu* helpers the menu
/// controllers call. Everything is null-safe, so the game runs (with one warning) until the
/// AudioLibrary asset and its clips are assigned.
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    public AudioLibrary Library { get; private set; }

    private AudioSource musicSource;
    private AudioSource sfxSource;
    private AudioClip currentMusic;
    private AudioClip lastTrackMusic;   // last TrackScene song, so a random re-entry avoids repeating it

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("AudioManager");
        go.AddComponent<AudioManager>();   // sets Instance in Awake
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        Library = Resources.Load<AudioLibrary>("AudioLibrary");
        if (Library == null)
            Debug.LogWarning("[AudioManager] No Resources/AudioLibrary asset found — audio is silent. " +
                             "Create one (Assets > Create > Generacer > Audio Library) inside a Resources " +
                             "folder and assign your clips.");

        musicSource = gameObject.AddComponent<AudioSource>();
        musicSource.playOnAwake = false;
        musicSource.loop = true;
        musicSource.spatialBlend = 0f;                 // 2D — full volume regardless of listener position
        musicSource.volume = Library != null ? Library.musicVolume : 0.6f;

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        sfxSource.loop = false;
        sfxSource.spatialBlend = 0f;                   // 2D
        sfxSource.volume = Library != null ? Library.sfxVolume : 0.9f;
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // sceneLoaded may not fire for the scene already active when we bootstrap, so also apply the
    // starting scene's music policy once here. PlayMusic is idempotent, so a double-call is harmless.
    void Start() => ApplyMusicForScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyMusicForScene(scene.name);

    void ApplyMusicForScene(string sceneName)
    {
        PlayMusic(MusicForScene(sceneName));   // PlayMusic(null) stops the music
    }

    /// <summary>Re-evaluates and applies the CURRENT scene's music. Call after a state change that
    /// should swap the track without a scene reload (e.g. the Drone ending starting while the player
    /// is already in the hub). Idempotent — no-op if the right track is already playing.</summary>
    public void RefreshCurrentSceneMusic() => ApplyMusicForScene(SceneManager.GetActiveScene().name);
    public static void RefreshSceneMusic() { if (Instance != null) Instance.RefreshCurrentSceneMusic(); }

    /// <summary>The looping track for a scene, or null for silence. Extend this as more scenes get
    /// their own music (gameplay, endings, ...).</summary>
    AudioClip MusicForScene(string sceneName)
    {
        if (Library == null) return null;
        switch (sceneName)
        {
            case "MainMenu":         return Library.mainMenuMusic;
            case "CarSelection":     return Library.carSelectionMusic;
            case "HubWorld":         return HubMusic();
            case "GeneracersEnding": return Library.generacersEndingMusic;
            case "ClipperEnding":    return Library.clipperEndingMusic;
            case "TrackScene":       return PickTrackMusic();
            default:                 return null;
        }
    }

    /// <summary>Hub music: the Drone-ending track during that game-over swarm, the player-victory track
    /// during the BOTS DEFEATED sequence, otherwise the normal hub song.</summary>
    AudioClip HubMusic()
    {
        if (Library == null) return null;
        var gm = GameLoopManager.Instance;
        if (gm != null)
        {
            if (gm.DroneEndingActive && Library.droneEndingMusic   != null) return Library.droneEndingMusic;
            if (gm.PlayerWinActive   && Library.playerVictoryMusic != null) return Library.playerVictoryMusic;
        }
        return Library.hubMusic;
    }

    /// <summary>A random TrackScene song from the pool, never the one played last time (so repeated
    /// entries vary). Null/empty pool → silence; null entries in the pool are skipped.</summary>
    AudioClip PickTrackMusic()
    {
        var clips = Library != null ? Library.trackMusic : null;
        if (clips == null) return null;

        var options = new List<AudioClip>();
        foreach (var c in clips) if (c != null) options.Add(c);
        if (options.Count == 0) return null;

        AudioClip chosen = options[Random.Range(0, options.Count)];
        for (int i = 0; i < 8 && options.Count > 1 && chosen == lastTrackMusic; i++)
            chosen = options[Random.Range(0, options.Count)];

        lastTrackMusic = chosen;
        return chosen;
    }

    // ---------------- Music ----------------

    /// <summary>Plays a looping music clip. No-op if that clip is already the one playing, so a
    /// shared track continues seamlessly across scene loads instead of restarting.</summary>
    public void PlayMusic(AudioClip clip)
    {
        if (clip == null) { StopMusic(); return; }
        if (currentMusic == clip && musicSource != null && musicSource.isPlaying) return;

        currentMusic = clip;
        if (musicSource == null) return;
        musicSource.clip = clip;
        musicSource.loop = true;   // guarantee looping at the play site, regardless of source reuse
        musicSource.Play();
    }

    public void StopMusic()
    {
        currentMusic = null;
        if (musicSource != null) musicSource.Stop();
    }

    // ---------------- SFX ----------------

    /// <summary>Fire-and-forget one-shot; multiple can overlap. Null clip is ignored.</summary>
    public void PlaySfx(AudioClip clip)
    {
        if (clip == null || sfxSource == null) return;
        sfxSource.PlayOneShot(clip);
    }

    /// <summary>Fire-and-forget 3D one-shot at a WORLD position (obstacles, world events). Spawns a
    /// temporary positional AudioSource that cleans itself up, scaled by the global SFX level, so
    /// distant events are quieter than nearby ones. Pass <paramref name="settings"/> to control the
    /// 3D falloff (min/max distance, rolloff, spatial blend, volume); null uses sensible defaults.
    /// Null clip is ignored.</summary>
    public void PlaySfxAt(AudioClip clip, Vector3 position, Spatial3DSettings settings = null)
    {
        if (clip == null) return;

        var go = new GameObject("SFX_" + clip.name);
        go.transform.position = position;
        var src = go.AddComponent<AudioSource>();
        src.clip = clip;

        float sfx = sfxSource != null ? sfxSource.volume : 1f;
        if (settings != null)
        {
            settings.ApplyTo(src, sfx);                 // tweakable per-emitter 3D settings
        }
        else
        {
            src.spatialBlend = 1f;                      // 3D — positioned in the world
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 8f;
            src.maxDistance = 150f;
            src.dopplerLevel = 0f;
            src.volume = sfx;
        }

        src.Play();
        Destroy(go, clip.length + 0.1f);
    }

    // Null-safe static shortcuts the menu controllers use for the three shared UI sounds.
    public static void PlayMenuMove()   => PlayLibrarySfx(Lib != null ? Lib.menuMove   : null);
    public static void PlayMenuSelect() => PlayLibrarySfx(Lib != null ? Lib.menuSelect : null);
    public static void PlayMenuBack()   => PlayLibrarySfx(Lib != null ? Lib.menuBack   : null);

    // Store menu SFX (2D — separate clips from the main-menu buttons).
    public static void PlayStoreMove()   => PlayLibrarySfx(Lib != null ? Lib.storeMove   : null);
    public static void PlayStoreSelect() => PlayLibrarySfx(Lib != null ? Lib.storeSelect : null);
    public static void PlayStoreDenied() => PlayLibrarySfx(Lib != null ? Lib.storeDenied : null);

    // Universal vehicle one-shots (same clip for every car), fired AT the car by the player
    // CarController — 3D so other players can hear them (2D is reserved for menu SFX + music).
    public static void PlayTurbo(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.turboBoost : null, position);
    public static void PlayJump(Vector3 position)  => PlayLibrarySfxAt(Lib != null ? Lib.jump       : null, position);
    public static void PlayCarLanding(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.carLanding : null, position);
    public static void PlayTurboCrafted(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.turboCrafted : null, position);
    public static void PlayJetCrafted(Vector3 position)   => PlayLibrarySfxAt(Lib != null ? Lib.jetCrafted   : null, position);

    // SD ability one-shots (3D at the car). The "while active" loop is managed by SDAbilityController.
    public static void PlaySdActivate(Vector3 position)   => PlayLibrarySfxAt(Lib != null ? Lib.sdActivate   : null, position);
    public static void PlaySdDeactivate(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.sdDeactivate : null, position);

    // Positional obstacle one-shots (3D, at the event's world location). Optional Spatial3DSettings
    // let the caller (e.g. the LightningSpawner) tweak the 3D falloff.
    public static void PlayLightningWarning(Vector3 position, Spatial3DSettings settings = null) => PlayLibrarySfxAt(Lib != null ? Lib.lightningWarning : null, position, settings);
    public static void PlayLightningStrike(Vector3 position, Spatial3DSettings settings = null)  => PlayLibrarySfxAt(PickRandom(Lib != null ? Lib.lightningStrike : null), position, settings);

    // Drone projectile one-shots (3D). Distinct sound for hitting the player vs the environment.
    // Optional Spatial3DSettings let the DronePissBall prefab tweak the 3D falloff.
    public static void PlayDroneShoot(Vector3 position, Spatial3DSettings settings = null)              => PlayLibrarySfxAt(Lib != null ? Lib.droneShoot               : null, position, settings);
    public static void PlayProjectileHitEnvironment(Vector3 position, Spatial3DSettings settings = null) => PlayLibrarySfxAt(Lib != null ? Lib.projectileHitEnvironment : null, position, settings);
    public static void PlayProjectileHitPlayer(Vector3 position, Spatial3DSettings settings = null)      => PlayLibrarySfxAt(Lib != null ? Lib.projectileHitPlayer      : null, position, settings);

    // Boost Gate one-shots (3D, at the gate). Optional Spatial3DSettings let the gate prefab tweak
    // the 3D falloff.
    public static void PlayBoostGateSpawn(Vector3 position, Spatial3DSettings settings = null) => PlayLibrarySfxAt(Lib != null ? Lib.boostGateSpawn : null, position, settings);
    public static void PlayBoostGateBoost(Vector3 position, Spatial3DSettings settings = null) => PlayLibrarySfxAt(Lib != null ? Lib.boostGateBoost : null, position, settings);

    // Player-victory banner stinger (2D — screen UI, not a world event). Fired the moment the
    // BOTS DEFEATED text begins its fade-in.
    public static void PlayVictoryBanner() => PlayLibrarySfx(Lib != null ? Lib.victoryBanner : null);

    static AudioLibrary Lib => Instance != null ? Instance.Library : null;
    static void PlayLibrarySfx(AudioClip clip) { if (Instance != null) Instance.PlaySfx(clip); }
    static void PlayLibrarySfxAt(AudioClip clip, Vector3 position, Spatial3DSettings settings = null) { if (Instance != null) Instance.PlaySfxAt(clip, position, settings); }

    /// <summary>A random non-null clip from a pool (empty/null pool → null). Gives per-event variety,
    /// e.g. a different lightning-strike sound each strike.</summary>
    static AudioClip PickRandom(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return null;
        var chosen = clips[Random.Range(0, clips.Length)];
        if (chosen != null) return chosen;
        foreach (var c in clips) if (c != null) return c;   // fallback if the random slot was empty
        return null;
    }

    // ---------------- Volume (for the future Audio submenu) ----------------

    public void SetMusicVolume(float v) { if (musicSource != null) musicSource.volume = Mathf.Clamp01(v); }
    public void SetSfxVolume(float v)   { if (sfxSource   != null) sfxSource.volume   = Mathf.Clamp01(v); }
    public float MusicVolume => musicSource != null ? musicSource.volume : 0f;
    public float SfxVolume   => sfxSource   != null ? sfxSource.volume   : 0f;
}
