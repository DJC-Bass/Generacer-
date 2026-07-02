using UnityEngine;

/// <summary>
/// Loops a vehicle's engine sound and raises its PITCH with speed for an arcade rev effect. Put this
/// on a car PREFAB and assign that car's own engine clip, so every vehicle sounds different — the
/// player car (swapped in from the Car-Selection pick) and AI/drone cars alike.
///
/// Horizontal speed is read from the vehicle's Rigidbody (vertical motion is ignored, so jumps don't
/// spike the rev) and mapped across [0 .. Speed For Max Pitch] to [Idle Pitch .. Max Pitch], with
/// light frame-rate-independent smoothing so the pitch glides. Volume ramps gently over the same
/// range and is scaled by the global SFX level (<see cref="AudioManager"/>) so the Audio menu can
/// govern it later.
///
/// Self-contained: it adds and drives its own looping AudioSource — no scene setup beyond assigning
/// the clip. Playback is tied to the component being enabled, so the inert Car-Selection preview
/// (which disables the car's scripts) stays silent.
/// </summary>
public class CarEngineAudio : MonoBehaviour
{
    [Header("Clip")]
    [Tooltip("This vehicle's unique looping engine sound. Empty = no engine audio.")]
    public AudioClip engineClip;

    [Header("Pitch vs. Speed")]
    [Tooltip("Pitch at a standstill (idle rev).")]
    public float idlePitch = 0.8f;
    [Tooltip("Pitch at (and above) Speed For Max Pitch (redline).")]
    public float maxPitch = 2.2f;
    [Tooltip("Horizontal speed, in m/s, at which the pitch reaches Max Pitch. (The car's own top " +
             "speed is far higher; a lower value here makes the rev sweep across normal driving.)")]
    public float speedForMaxPitch = 60f;

    [Header("Volume vs. Speed")]
    [Range(0f, 1f)] [Tooltip("Engine volume at idle.")]
    public float idleVolume = 0.5f;
    [Range(0f, 1f)] [Tooltip("Engine volume at (and above) Speed For Max Pitch.")]
    public float fullVolume = 0.9f;

    [Header("Feel / 3D")]
    [Tooltip("How quickly pitch & volume chase their target (higher = snappier).")]
    public float responsiveness = 6f;
    [Range(0f, 1f)] [Tooltip("0 = 2D (always full — good for the player car). 1 = 3D positional (good for AI cars).")]
    public float spatialBlend = 0f;
    [Tooltip("For 3D blend, distance in metres beyond which the engine fades out.")]
    public float maxDistance = 60f;

    private Rigidbody rb;
    private AudioSource source;

    void Awake()
    {
        rb = GetComponentInParent<Rigidbody>();

        source = gameObject.AddComponent<AudioSource>();
        source.clip = engineClip;
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = spatialBlend;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 3f;
        source.maxDistance = maxDistance;
        source.dopplerLevel = 0f;   // we drive the pitch ourselves; no doppler shift on top
        source.pitch = idlePitch;
        source.volume = idleVolume;
    }

    void OnEnable()
    {
        if (source != null && engineClip != null && !source.isPlaying)
            source.Play();
    }

    void OnDisable()
    {
        // Stops the engine whenever the component is disabled — e.g. the inert Car-Selection preview,
        // which disables all of the car's MonoBehaviours.
        if (source != null) source.Stop();
    }

    void Update()
    {
        if (source == null || engineClip == null) return;

        float t = speedForMaxPitch > 0.01f ? Mathf.Clamp01(HorizontalSpeed() / speedForMaxPitch) : 0f;
        float targetPitch = Mathf.Lerp(idlePitch, maxPitch, t);
        float targetVolume = Mathf.Lerp(idleVolume, fullVolume, t) * GlobalSfxVolume();

        // Frame-rate-independent easing toward the target, so the rev glides instead of stepping.
        float k = 1f - Mathf.Exp(-responsiveness * Time.deltaTime);
        source.pitch = Mathf.Lerp(source.pitch, targetPitch, k);
        source.volume = Mathf.Lerp(source.volume, targetVolume, k);
    }

    float HorizontalSpeed()
    {
        if (rb == null) return 0f;
        Vector3 v = rb.linearVelocity;
        v.y = 0f;                      // ignore jump/fall so airtime doesn't spike the rev
        return v.magnitude;
    }

    // Participates in the global SFX level (the future Audio menu); full volume if the manager is absent.
    static float GlobalSfxVolume() => AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
}
