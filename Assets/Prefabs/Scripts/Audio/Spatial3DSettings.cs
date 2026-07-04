using UnityEngine;

/// <summary>
/// Inspector-tweakable 3D playback settings for a positional AudioSource: spatialisation, distance
/// falloff, volume and doppler. Drop it as a public field on any component that plays world-positioned
/// audio, then call <see cref="ApplyTo"/> on the AudioSource. Kept separate so several emitters can
/// share the same tunables and every 3D sound is adjustable from the Inspector.
/// </summary>
[System.Serializable]
public class Spatial3DSettings
{
    [Range(0f, 1f)] [Tooltip("0 = 2D (no spatialisation), 1 = fully 3D / positional.")]
    public float spatialBlend = 1f;
    [Range(0f, 1f)] [Tooltip("Base volume. Also scaled by the game's global SFX level at play time.")]
    public float volume = 0.9f;
    [Tooltip("Within this distance (metres) the sound plays at full volume.")]
    public float minDistance = 8f;
    [Tooltip("Beyond this distance (metres) the sound is inaudible.")]
    public float maxDistance = 150f;
    [Tooltip("How the volume falls off between the min and max distance.")]
    public AudioRolloffMode rolloffMode = AudioRolloffMode.Linear;
    [Range(0f, 5f)] [Tooltip("Pitch shift from relative motion. 0 = none (best for steady looping sounds).")]
    public float dopplerLevel = 0f;

    /// <summary>Applies these settings to an AudioSource. <paramref name="globalSfxScale"/> folds in the
    /// game's SFX volume so the source sits in the overall mix.</summary>
    public void ApplyTo(AudioSource src, float globalSfxScale = 1f)
    {
        if (src == null) return;
        src.spatialBlend = spatialBlend;
        src.volume = volume * globalSfxScale;
        src.minDistance = minDistance;
        src.maxDistance = maxDistance;
        src.rolloffMode = rolloffMode;
        src.dopplerLevel = dopplerLevel;
    }
}
