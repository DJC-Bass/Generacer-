using System.Collections;
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

    // Interior music layer: a 2nd looping music source that crossfades OVER the scene music while the
    // player is inside an interior zone (e.g. the Windows building), ducking the scene theme to silence
    // until they leave. Both play at once; interiorBlend just picks which is audible, so the scene theme
    // keeps running continuously behind the interior track.
    private AudioSource interiorSource;
    private bool interiorActive;            // player currently inside an interior zone
    private float interiorBlend;            // 0 = scene music only, 1 = interior music only
    private float musicBaseVolume = 0.6f;   // intended music level; the crossfade splits it between the two sources

    // Armed by a portal just before it loads its destination scene; consumed once on arrival to play
    // the 3D "portal exit" sound at the player's spawn point. Static so the portal can set it without
    // touching the instance, and so it survives the scene load.
    private static bool portalExitPending;

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

        musicBaseVolume = Library != null ? Library.musicVolume : 0.6f;

        musicSource = gameObject.AddComponent<AudioSource>();
        musicSource.playOnAwake = false;
        musicSource.loop = true;
        musicSource.spatialBlend = 0f;                 // 2D — full volume regardless of listener position
        musicSource.volume = musicBaseVolume;

        // Second music source for interior override tracks (silent until an interior zone is entered).
        interiorSource = gameObject.AddComponent<AudioSource>();
        interiorSource.playOnAwake = false;
        interiorSource.loop = true;
        interiorSource.spatialBlend = 0f;              // 2D
        interiorSource.volume = 0f;

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        sfxSource.loop = false;
        sfxSource.spatialBlend = 0f;                   // 2D
        sfxSource.volume = Library != null ? Library.sfxVolume : 0.9f;

        // Apply any persisted player volume overrides (Main Menu > Settings > Audio) on top of the
        // AudioLibrary's authored defaults, so the player's last choice carries across sessions.
        if (GameSettings.HasMusicVolume) SetMusicVolume(GameSettings.MusicVolume);
        if (GameSettings.HasSfxVolume)   SetSfxVolume(GameSettings.SfxVolume);
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // sceneLoaded may not fire for the scene already active when we bootstrap, so also apply the
    // starting scene's music policy once here. PlayMusic is idempotent, so a double-call is harmless.
    void Start() => ApplyMusicForScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // An ADDITIVE load never changes where the local player is (only the multiplayer world loads
        // additively) — music follows the ACTIVE scene, swapped by MultiplayerWorld on teleport.
        if (mode == LoadSceneMode.Additive) return;

        ApplyMusicForScene(scene.name);

        // Portals only lead to gameplay scenes; the arriving player car plays the "portal exit" sound
        // off itself (PortalExitAudio) when it spawns. If we reach a NON-gameplay scene (a menu) with
        // the flag still armed — e.g. a portal led to a car-less ending — clear it so a later car
        // spawn can't play a stale portal-exit.
        if (portalExitPending && !GameplayHud.VisibleInScene(scene.name))
            portalExitPending = false;
    }

    /// <summary>Arms the one-shot 3D "portal exit" sound. Call from a portal right before it loads its
    /// destination scene; the arriving player car (PortalExitAudio) consumes it on spawn.</summary>
    public static void ArmPortalExit() => portalExitPending = true;

    /// <summary>If the player just arrived via a portal, plays the Portal Exit sound off the given
    /// emitter (the player car — like the speed-barrier stingers) and clears the pending flag. Called
    /// by PortalExitAudio on the car prefab when it spawns into a scene.</summary>
    public static void TryPlayPortalExit(Transform emitter)
    {
        if (!portalExitPending) return;
        portalExitPending = false;
        PlayPortalExit(emitter);
    }

    /// <summary>Plays the Portal Exit sound off the given emitter UNCONDITIONALLY (no pending-flag
    /// check). Used by the Drone-ending swarm, where each drone spawning at the portal is itself a
    /// portal exit.</summary>
    public static void PlayPortalExit(Transform emitter)
    {
        if (Instance == null || emitter == null) return;
        var lib = Instance.Library;
        if (lib == null || lib.portalExit == null) return;
        Instance.PlaySfxFollow(lib.portalExit, emitter, lib.portalExitAudio3D, false, lib.portalExitVolume);
    }

    // Crossfades the scene music and the interior override every frame. Both sources share
    // musicBaseVolume; interiorBlend eases toward 1 while inside an interior zone and back to 0 outside.
    void Update()
    {
        float target = interiorActive ? 1f : 0f;
        if (interiorBlend != target)
        {
            float fade = Library != null ? Library.interiorMusicCrossfadeSeconds : 0.3f;
            float step = fade > 0.001f ? Time.deltaTime / fade : 1f;
            interiorBlend = Mathf.MoveTowards(interiorBlend, target, step);
        }

        if (musicSource != null)    musicSource.volume    = musicBaseVolume * (1f - interiorBlend);
        if (interiorSource != null) interiorSource.volume = musicBaseVolume * interiorBlend;
    }

    void ApplyMusicForScene(string sceneName)
    {
        ResetInterior();                       // a new scene clears any interior override from the last one
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
            case "Tutorial":         return Library.tutorialMusic;
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

    // ---------------- Interior override music ----------------

    /// <summary>Ducks the scene music and crossfades in a looping interior track (e.g. inside the
    /// Windows building). The scene theme keeps playing (silently) behind it, so exiting resumes it
    /// seamlessly. No-op for a null clip. Call <see cref="StopInteriorMusic"/> to return to the scene
    /// theme.</summary>
    public void PlayInteriorMusic(AudioClip clip)
    {
        if (clip == null || interiorSource == null) return;

        // Start (or swap to) the interior clip. Once playing it keeps looping — even while silent
        // outside — so re-entering the zone picks the track back up smoothly instead of restarting it.
        if (interiorSource.clip != clip)
        {
            interiorSource.clip = clip;
            interiorSource.Play();
        }
        else if (!interiorSource.isPlaying)
        {
            interiorSource.Play();
        }
        interiorActive = true;   // Update() crossfades scene -> interior
    }

    /// <summary>Crossfades back from the interior track to the scene music (leaving the interior source
    /// looping silently so a quick re-entry resumes it).</summary>
    public void StopInteriorMusic() => interiorActive = false;

    /// <summary>Immediately clears any interior override — used on scene change so the new scene starts
    /// on its own music with the interior source stopped and the scene theme un-ducked.</summary>
    void ResetInterior()
    {
        interiorActive = false;
        interiorBlend = 0f;
        if (interiorSource != null)
        {
            interiorSource.Stop();
            interiorSource.clip = null;
            interiorSource.volume = 0f;
        }
        if (musicSource != null) musicSource.volume = musicBaseVolume;
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

    /// <summary>Fire-and-forget 3D one-shot that RIDES a moving transform, so a very fast emitter
    /// (e.g. a car past the speed barrier, which outruns a world-anchored sound) keeps it audible.
    /// <paramref name="bypassListenerEffects"/> lets the sound skip the AudioListener's filters — used
    /// so the speed-barrier stingers are heard CLEAN over the broken-barrier low-pass muffle. Null
    /// clip / follow are ignored.</summary>
    public void PlaySfxFollow(AudioClip clip, Transform follow, Spatial3DSettings settings = null,
                              bool bypassListenerEffects = false, float volumeScale = 1f)
    {
        if (clip == null || follow == null) return;

        var go = new GameObject("SFX_" + clip.name);
        go.transform.SetParent(follow, false);         // ride along so a fast emitter can't outrun it
        go.transform.localPosition = Vector3.zero;

        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.bypassListenerEffects = bypassListenerEffects;

        float sfx = sfxSource != null ? sfxSource.volume : 1f;
        if (settings != null)
        {
            settings.ApplyTo(src, sfx);
        }
        else
        {
            src.spatialBlend = 1f;                      // 3D — positioned in the world
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 8f;
            src.maxDistance = 150f;
            src.dopplerLevel = 0f;                      // no pitch bend from the car's speed
            src.volume = sfx;
        }

        src.volume *= Mathf.Clamp01(volumeScale);       // per-clip Inspector volume, on top of the global SFX level

        src.Play();
        Destroy(go, clip.length + 0.1f);
    }

    // Null-safe static shortcuts the menu controllers use for the shared UI sounds.
    public static void PlayMenuMove()   => PlayLibrarySfx(Lib != null ? Lib.menuMove   : null);
    public static void PlayMenuSelect() => PlayLibrarySfx(Lib != null ? Lib.menuSelect : null);
    public static void PlayMenuBack()   => PlayLibrarySfx(Lib != null ? Lib.menuBack   : null);
    // Full-screen menu open/close (shared by the Start menu + Inventory).
    public static void PlayMenuOpen()   => PlayLibrarySfx(Lib != null ? Lib.menuOpen   : null);
    public static void PlayMenuClose()  => PlayLibrarySfx(Lib != null ? Lib.menuClose  : null);

    // Store menu SFX (2D — separate clips from the main-menu buttons).
    public static void PlayStoreMove()   => PlayLibrarySfx(Lib != null ? Lib.storeMove   : null);
    public static void PlayStoreSelect() => PlayLibrarySfx(Lib != null ? Lib.storeSelect : null);
    public static void PlayStoreDenied() => PlayLibrarySfx(Lib != null ? Lib.storeDenied : null);
    public static void PlayStoreOpen()   => PlayLibrarySfx(Lib != null ? Lib.storeOpen   : null);
    public static void PlayStoreClose()  => PlayLibrarySfx(Lib != null ? Lib.storeClose  : null);

    // Upgrade Ramp menu open/close (2D).
    public static void PlayRampOpen()    => PlayLibrarySfx(Lib != null ? Lib.rampOpen    : null);
    public static void PlayRampClose()   => PlayLibrarySfx(Lib != null ? Lib.rampClose   : null);

    // Universal vehicle one-shots (same clip for every car), fired AT the car by the player
    // CarController — 3D so other players can hear them (2D is reserved for menu SFX + music).
    public static void PlayTurbo(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.turboBoost : null, position);
    public static void PlayJump(Vector3 position)  => PlayLibrarySfxAt(Lib != null ? Lib.jump       : null, position);
    public static void PlayCarLanding(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.carLanding : null, position);
    public static void PlayLoopBoost(Vector3 position)  => PlayLibrarySfxAt(Lib != null ? Lib.loopBoost  : null, position);

    // Speed-barrier stingers (3D, riding the car so they stay audible at 750+ mph). They bypass the
    // AudioListener's low-pass so they're heard CLEAN over the broken-barrier muffle.
    public static void PlaySpeedBarrierBreak(Transform car) => PlayLibrarySfxFollow(Lib != null ? Lib.speedBarrierBreak : null, car, Lib != null ? Lib.speedBarrierBreakVolume : 1f);
    public static void PlaySpeedBarrierLeave(Transform car) => PlayLibrarySfxFollow(Lib != null ? Lib.speedBarrierLeave : null, car, Lib != null ? Lib.speedBarrierLeaveVolume : 1f);
    public static void PlayTurboCrafted(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.turboCrafted : null, position);
    public static void PlayJetCrafted(Vector3 position)   => PlayLibrarySfxAt(Lib != null ? Lib.jetCrafted   : null, position);
    public static void PlayShieldCrafted(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.shieldCrafted : null, position);
    public static void PlayGrappleCrafted(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.grappleCrafted : null, position);

    // SD ability one-shots (3D at the car). The "while active" loop is managed by SDAbilityController.
    public static void PlaySdActivate(Vector3 position)   => PlayLibrarySfxAt(Lib != null ? Lib.sdActivate   : null, position);
    public static void PlaySdDeactivate(Vector3 position) => PlayLibrarySfxAt(Lib != null ? Lib.sdDeactivate : null, position);

    // Shield ability one-shots (3D at the car), tuned by AudioLibrary.shieldAudio3D. The "while up"
    // loop is managed by ShieldAbility, which reads the same 3D block.
    public static void PlayShieldActivate(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.shieldActivate : null, position, Lib != null ? Lib.shieldAudio3D : null);
    public static void PlayShieldDeactivate(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.shieldDeactivate : null, position, Lib != null ? Lib.shieldAudio3D : null);

    // Grappling hook one-shots (3D), all tuned by AudioLibrary.grappleAudio3D. Attach plays at the HIT
    // POINT rather than the car, so a long catch is audible out where it landed.
    public static void PlayGrappleFire(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.grappleFire : null, position, Lib != null ? Lib.grappleAudio3D : null);
    public static void PlayGrappleAttach(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.grappleAttach : null, position, Lib != null ? Lib.grappleAudio3D : null);
    public static void PlayGrappleRelease(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.grappleRelease : null, position, Lib != null ? Lib.grappleAudio3D : null);

    // Support Ship one-shots (3D), all tuned by AudioLibrary.supportShipAudio3D. The "while out" engine
    // loop is managed by SupportShipAbility, which reads the same 3D block.
    public static void PlaySupportShipActivate(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipActivate : null, position, Lib != null ? Lib.supportShipAudio3D : null);
    public static void PlaySupportShipDeactivate(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipDeactivate : null, position, Lib != null ? Lib.supportShipAudio3D : null);
    public static void PlaySupportShipDestroyed(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipDestroyed : null, position, Lib != null ? Lib.supportShipAudio3D : null);

    // The gun. Firing is at the muzzle (so it shares the ship's 3D tuning); the IMPACT can be hundreds
    // of metres downrange, so that one carries its own settings off the laser prefab.
    public static void PlaySupportShipLaserFire(Vector3 position) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipLaserFire : null, position, Lib != null ? Lib.supportShipAudio3D : null);
    // Impacts come in two flavours so the gunner can HEAR whether a shot counted: a dull tick off the
    // scenery, or a solid hit on something that reacted. Each carries its own 3D block off the laser
    // prefab, since a miss should not carry as far as a kill.
    public static void PlaySupportShipLaserHitEnvironment(Vector3 position, Spatial3DSettings settings = null) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipLaserHitEnvironment : null, position, settings);
    public static void PlaySupportShipLaserHitEntity(Vector3 position, Spatial3DSettings settings = null) =>
        PlayLibrarySfxAt(Lib != null ? Lib.supportShipLaserHitEntity : null, position, settings);

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

    // Windows trigger one-shots (3D, at the volume). Optional Spatial3DSettings tweak the 3D falloff.
    public static void PlayWindowsEnter(Vector3 position, Spatial3DSettings settings = null) => PlayLibrarySfxAt(Lib != null ? Lib.windowsEnter : null, position, settings);
    public static void PlayWindowsExit(Vector3 position, Spatial3DSettings settings = null)  => PlayLibrarySfxAt(Lib != null ? Lib.windowsExit  : null, position, settings);

    // Windows interior background music: ducks the scene theme and crossfades in the interior loop
    // while the player is inside the building, restoring the scene theme on exit.
    public static void EnterWindowsInterior() { if (Instance != null && Lib != null) Instance.PlayInteriorMusic(Lib.windowsInteriorMusic); }
    public static void ExitWindowsInterior()  { if (Instance != null) Instance.StopInteriorMusic(); }

    // Player-victory banner stinger (2D — screen UI, not a world event). Fired the moment the
    // BOTS DEFEATED text begins its fade-in.
    public static void PlayVictoryBanner() => PlayLibrarySfx(Lib != null ? Lib.victoryBanner : null);

    // Knock-off bounty (2D) — the player earned credits by knocking a Drone / Challenger car into
    // the kill floor.
    public static void PlayKnockoffBounty() => PlayLibrarySfx(Lib != null ? Lib.knockoffBounty : null);

    static AudioLibrary Lib => Instance != null ? Instance.Library : null;
    static void PlayLibrarySfx(AudioClip clip) { if (Instance != null) Instance.PlaySfx(clip); }
    static void PlayLibrarySfxAt(AudioClip clip, Vector3 position, Spatial3DSettings settings = null) { if (Instance != null) Instance.PlaySfxAt(clip, position, settings); }
    // Rides the emitter and bypasses listener effects — for the speed-barrier stingers.
    static void PlayLibrarySfxFollow(AudioClip clip, Transform follow, float volumeScale = 1f) { if (Instance != null) Instance.PlaySfxFollow(clip, follow, null, true, volumeScale); }

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

    // Music volume is the shared base level; Update() splits it between the scene + interior sources
    // via the crossfade, so set the base here rather than the source volume directly.
    public void SetMusicVolume(float v) { musicBaseVolume = Mathf.Clamp01(v); }
    public void SetSfxVolume(float v)   { if (sfxSource != null) sfxSource.volume = Mathf.Clamp01(v); }
    public float MusicVolume => musicBaseVolume;
    public float SfxVolume   => sfxSource != null ? sfxSource.volume : 0f;
}
