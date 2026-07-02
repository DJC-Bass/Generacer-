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

    /// <summary>The looping track for a scene, or null for silence. Extend this as more scenes get
    /// their own music (gameplay, endings, ...).</summary>
    AudioClip MusicForScene(string sceneName)
    {
        if (Library == null) return null;
        switch (sceneName)
        {
            case "MainMenu":         return Library.mainMenuMusic;
            case "CarSelection":     return Library.carSelectionMusic;
            case "HubWorld":         return Library.hubMusic;
            case "GeneracersEnding": return Library.generacersEndingMusic;
            case "ClipperEnding":    return Library.clipperEndingMusic;
            case "TrackScene":       return PickTrackMusic();
            default:                 return null;
        }
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

    // Null-safe static shortcuts the menu controllers use for the three shared UI sounds.
    public static void PlayMenuMove()   => PlayLibrarySfx(Lib != null ? Lib.menuMove   : null);
    public static void PlayMenuSelect() => PlayLibrarySfx(Lib != null ? Lib.menuSelect : null);
    public static void PlayMenuBack()   => PlayLibrarySfx(Lib != null ? Lib.menuBack   : null);

    // Universal vehicle one-shots (same clip for every car), fired by the player CarController.
    public static void PlayTurbo() => PlayLibrarySfx(Lib != null ? Lib.turboBoost : null);
    public static void PlayJump()  => PlayLibrarySfx(Lib != null ? Lib.jump       : null);

    static AudioLibrary Lib => Instance != null ? Instance.Library : null;
    static void PlayLibrarySfx(AudioClip clip) { if (Instance != null) Instance.PlaySfx(clip); }

    // ---------------- Volume (for the future Audio submenu) ----------------

    public void SetMusicVolume(float v) { if (musicSource != null) musicSource.volume = Mathf.Clamp01(v); }
    public void SetSfxVolume(float v)   { if (sfxSource   != null) sfxSource.volume   = Mathf.Clamp01(v); }
    public float MusicVolume => musicSource != null ? musicSource.volume : 0f;
    public float SfxVolume   => sfxSource   != null ? sfxSource.volume   : 0f;
}
