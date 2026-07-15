using UnityEngine;

/// <summary>
/// Per-SD activation particle effect, living on the PlayerCar root (where the particle systems are
/// parented). Each SD card has its own particle system; while that SD's ability is active the matching
/// system plays and every other one stays off. Driven by <see cref="SDAbilityController"/>, which calls
/// <see cref="Show"/> on activation (and whenever it re-acquires a freshly spawned car) and
/// <see cref="Hide"/> on deactivation. Everything starts OFF regardless of a system's Play-On-Awake setting.
///
/// Setup: put this on the PlayerCar root and fill <see cref="effects"/> with one entry per SD — the SD's
/// exact inventory name ("Fire SD", "Wind SD", "Lightning SD") and its particle system.
/// </summary>
public class SDAbilityVFX : MonoBehaviour
{
    [System.Serializable]
    public struct Entry
    {
        [Tooltip("The SD card's exact inventory name, e.g. \"Fire SD\", \"Wind SD\", \"Lightning SD\".")]
        public string sdItemName;
        [Tooltip("The particle system that plays while that SD's ability is active.")]
        public ParticleSystem particleSystem;
    }

    [Tooltip("One entry per SD card. When that SD's ability activates its particle system plays; " +
             "every other SD's system stays off.")]
    public Entry[] effects;

    void Awake() => Hide();   // start with everything off, even if a system has Play-On-Awake ticked

    /// <summary>Plays the particle system mapped to <paramref name="sdName"/> and stops every other one.
    /// Idempotent — safe to call repeatedly without restarting an already-playing system.</summary>
    public void Show(string sdName)
    {
        if (effects == null) return;
        foreach (var e in effects)
            SetPlaying(e.particleSystem, e.sdItemName == sdName);
    }

    /// <summary>Stops every SD particle system.</summary>
    public void Hide()
    {
        if (effects == null) return;
        foreach (var e in effects)
            SetPlaying(e.particleSystem, false);
    }

    static void SetPlaying(ParticleSystem ps, bool on)
    {
        if (ps == null) return;
        var go = ps.gameObject;
        if (on)
        {
            if (!go.activeSelf) go.SetActive(true);
            if (!ps.isPlaying) ps.Play(true);   // covers Play-On-Awake off; no restart if already running
        }
        else if (go.activeSelf)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            go.SetActive(false);
        }
    }
}
