using UnityEngine;

/// <summary>
/// Player-facing game settings, backed by PlayerPrefs so choices persist across sessions. Holds the
/// Audio volumes (Music + SFX) and the Video/Graphics options (resolution, display mode, quality,
/// vsync) set from the Main Menu's SETTINGS screen. Audio is applied by <see cref="AudioManager"/> at
/// startup; Video is applied here by <see cref="ApplyVideoSettings"/> (a bootstrap). Mirrors
/// <see cref="TutorialSettings"/>'s PlayerPrefs pattern; add Control keys here as that screen lands.
///
/// A setting is only WRITTEN once the player changes it (its Has* stays false until then), so the
/// AudioManager keeps the AudioLibrary asset's authored default and Unity keeps the project's default
/// quality/resolution until the player actually overrides them.
/// </summary>
public static class GameSettings
{
    const string MusicKey = "audio.musicVolume";
    const string SfxKey   = "audio.sfxVolume";

    /// <summary>True once the player has set a Music volume, so callers can prefer an authored default
    /// until then.</summary>
    public static bool HasMusicVolume => PlayerPrefs.HasKey(MusicKey);
    /// <summary>True once the player has set an SFX volume. See <see cref="HasMusicVolume"/>.</summary>
    public static bool HasSfxVolume => PlayerPrefs.HasKey(SfxKey);

    /// <summary>Persisted Music volume (0..1). The fallback is only returned when nothing is stored yet
    /// (<see cref="HasMusicVolume"/> is false); the AudioManager uses the AudioLibrary default then.</summary>
    public static float MusicVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(MusicKey, 0.6f));
        set { PlayerPrefs.SetFloat(MusicKey, Mathf.Clamp01(value)); PlayerPrefs.Save(); }
    }

    /// <summary>Persisted SFX volume (0..1). See <see cref="MusicVolume"/> for the fallback caveat.</summary>
    public static float SfxVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(SfxKey, 0.9f));
        set { PlayerPrefs.SetFloat(SfxKey, Mathf.Clamp01(value)); PlayerPrefs.Save(); }
    }

    // ---- Video / Graphics (applied live from the menu + re-applied at boot via ApplyVideoSettings) ----

    const string ResWKey       = "video.resWidth";
    const string ResHKey       = "video.resHeight";
    const string FullscreenKey = "video.fullscreenMode";
    const string QualityKey    = "video.qualityLevel";
    const string VSyncKey      = "video.vsync";

    /// <summary>True once the player has picked a resolution. Fallback getters return the live screen size.</summary>
    public static bool HasResolution   => PlayerPrefs.HasKey(ResWKey) && PlayerPrefs.HasKey(ResHKey);
    public static int  ResolutionWidth  => PlayerPrefs.GetInt(ResWKey, Screen.width);
    public static int  ResolutionHeight => PlayerPrefs.GetInt(ResHKey, Screen.height);
    public static void SetResolution(int width, int height)
    {
        PlayerPrefs.SetInt(ResWKey, width);
        PlayerPrefs.SetInt(ResHKey, height);
        PlayerPrefs.Save();
    }

    /// <summary>Persisted <see cref="FullScreenMode"/> as an int; fallback is the live mode.</summary>
    public static bool HasFullScreenMode => PlayerPrefs.HasKey(FullscreenKey);
    public static int FullScreenModeValue
    {
        get => PlayerPrefs.GetInt(FullscreenKey, (int)Screen.fullScreenMode);
        set { PlayerPrefs.SetInt(FullscreenKey, value); PlayerPrefs.Save(); }
    }

    /// <summary>Persisted quality level index; fallback is the live level.</summary>
    public static bool HasQualityLevel => PlayerPrefs.HasKey(QualityKey);
    public static int QualityLevel
    {
        get => PlayerPrefs.GetInt(QualityKey, QualitySettings.GetQualityLevel());
        set { PlayerPrefs.SetInt(QualityKey, value); PlayerPrefs.Save(); }
    }

    /// <summary>Persisted VSync (0 = off, 1 = on); fallback is the live vSyncCount.</summary>
    public static bool HasVSync => PlayerPrefs.HasKey(VSyncKey);
    public static int VSync
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(VSyncKey, QualitySettings.vSyncCount), 0, 1);
        set { PlayerPrefs.SetInt(VSyncKey, Mathf.Clamp(value, 0, 1)); PlayerPrefs.Save(); }
    }

    /// <summary>Applies the persisted Video/Graphics settings in a safe order (quality → vsync →
    /// resolution/fullscreen). Runs automatically before the first scene loads, and can be called again
    /// after a change. Each step is a no-op until the player has set it, so the project/editor defaults
    /// stand otherwise. (Resolution/fullscreen changes are largely no-ops in the editor — they take real
    /// effect in a player build.)</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void ApplyVideoSettings()
    {
        if (HasQualityLevel)
            QualitySettings.SetQualityLevel(Mathf.Clamp(QualityLevel, 0, Mathf.Max(0, QualitySettings.names.Length - 1)), true);
        if (HasVSync)
            QualitySettings.vSyncCount = VSync;
        if (HasFullScreenMode || HasResolution)
        {
            FullScreenMode mode = HasFullScreenMode ? (FullScreenMode)FullScreenModeValue : Screen.fullScreenMode;
            int w = HasResolution ? ResolutionWidth  : Screen.width;
            int h = HasResolution ? ResolutionHeight : Screen.height;
            Screen.SetResolution(w, h, mode);
        }
    }
}
