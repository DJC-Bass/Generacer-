using UnityEngine;

/// <summary>
/// Phase 6: engine audio for REMOTE cars, driven off replicated speed. The prefab's own
/// <see cref="CarEngineAudio"/> can't run on a puppet (its scripts are stripped, and it reads a live
/// Rigidbody the puppet doesn't simulate) — so RemoteCarManager copies that component's tuning
/// (each car's unique clip, pitch/volume curves, 3D settings) into this one at puppet build, and the
/// speed comes from <see cref="RemoteCarPuppet"/>'s last replicated velocity instead. Identical
/// mapping to the local version: horizontal speed → [idle..max] pitch and volume, eased, scaled by
/// the global SFX level, fully 3D — this is the payoff of the "every world sound is 3D so remote
/// players can hear each other" audio groundwork.
/// </summary>
public class RemoteCarAudio : MonoBehaviour
{
    private AudioClip engineClip;
    private float idlePitch = 0.8f;
    private float maxPitch = 2.2f;
    private float speedForMaxPitch = 60f;
    private float idleVolume = 0.5f;
    private float fullVolume = 0.9f;
    private float responsiveness = 6f;
    private float spatialBlend = 1f;
    private float maxDistance = 60f;

    private AudioSource source;
    private RemoteCarPuppet puppet;

    /// <summary>Copies the tuning off the car PREFAB's CarEngineAudio (the asset — never a live
    /// instance), so every remote car sounds like itself. Call before the puppet activates.</summary>
    public void Configure(CarEngineAudio template)
    {
        if (template == null) return;
        engineClip = template.engineClip;
        idlePitch = template.idlePitch;
        maxPitch = template.maxPitch;
        speedForMaxPitch = template.speedForMaxPitch;
        idleVolume = template.idleVolume;
        fullVolume = template.fullVolume;
        responsiveness = template.responsiveness;
        spatialBlend = template.spatialBlend;
        maxDistance = template.maxDistance;
    }

    void Start()
    {
        puppet = GetComponentInParent<RemoteCarPuppet>();
        if (engineClip == null) return;

        source = gameObject.AddComponent<AudioSource>();
        source.clip = engineClip;
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = spatialBlend;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 3f;
        source.maxDistance = maxDistance;
        source.dopplerLevel = 0f;   // pitch is driven from speed; no doppler on top (matches the local car)
        source.pitch = idlePitch;
        source.volume = idleVolume;
        source.Play();
    }

    void Update()
    {
        if (source == null) return;

        Vector3 velocity = puppet != null ? puppet.CurrentVelocity : Vector3.zero;
        velocity.y = 0f;   // ignore vertical motion so jumps don't spike the rev (same as the local car)
        float speed = velocity.magnitude;

        float t = speedForMaxPitch > 0.01f ? Mathf.Clamp01(speed / speedForMaxPitch) : 0f;
        float targetPitch = Mathf.Lerp(idlePitch, maxPitch, t);
        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
        float targetVolume = Mathf.Lerp(idleVolume, fullVolume, t) * sfx;

        float k = 1f - Mathf.Exp(-responsiveness * Time.deltaTime);
        source.pitch = Mathf.Lerp(source.pitch, targetPitch, k);
        source.volume = Mathf.Lerp(source.volume, targetVolume, k);
    }
}
