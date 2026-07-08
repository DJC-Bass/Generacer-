using UnityEngine;

/// <summary>
/// Uniform audio for any portal, independent of the script that drives its scene change
/// (PortalTrigger, ReturnPortalTrigger, MainMenuReturnTrigger, ...). Put this on the portal object
/// that holds the trigger collider. It plays:
///   • a spawn one-shot + starts a looping "active" hum when it appears,
///   • a collision one-shot when a car / physics object passes into the portal,
///   • a despawn one-shot when the portal is explicitly destroyed (e.g. it times out) — but NOT when
///     the portal simply vanishes with a scene load (i.e. the player travelling through it).
///
/// Clips come from the shared <see cref="AudioLibrary"/>; the 3D playback settings are exposed here
/// for tweaking. Self-contained — it creates and manages its own AudioSources.
/// </summary>
public class PortalAudio : MonoBehaviour
{
    [Tooltip("3D playback settings shared by all portal sounds — spatial blend, volume, min/max distance.")]
    public Spatial3DSettings spatial = new Spatial3DSettings();

    [Tooltip("Minimum seconds between collision sounds, so many cars entering at once (e.g. the drone " +
             "swarm spawning at the portal) don't stack into a buzz.")]
    public float collisionCooldown = 0.1f;

    private AudioSource loopSource;      // looping 'active' hum
    private AudioSource oneShotSource;   // spawn + collision one-shots
    private float lastCollisionTime = -999f;

    void Start()
    {
        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;

        loopSource = gameObject.AddComponent<AudioSource>();
        loopSource.playOnAwake = false;
        loopSource.loop = true;
        spatial.ApplyTo(loopSource, sfx);

        oneShotSource = gameObject.AddComponent<AudioSource>();
        oneShotSource.playOnAwake = false;
        oneShotSource.loop = false;
        spatial.ApplyTo(oneShotSource, sfx);

        if (lib != null)
        {
            if (lib.portalActiveLoop != null) { loopSource.clip = lib.portalActiveLoop; loopSource.Play(); }
            if (lib.portalSpawn != null) oneShotSource.PlayOneShot(lib.portalSpawn);
        }
    }

    void OnTriggerEnter(Collider other) => PlayCollision(other.attachedRigidbody);
    void OnCollisionEnter(Collision collision) => PlayCollision(collision.rigidbody);

    void PlayCollision(Rigidbody who)
    {
        if (who == null) return;                                    // only physics objects (cars, projectiles)
        if (Time.time - lastCollisionTime < collisionCooldown) return;
        lastCollisionTime = Time.time;

        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (oneShotSource != null && lib != null && lib.portalCollision != null)
            oneShotSource.PlayOneShot(lib.portalCollision);
    }

    void OnDestroy()
    {
        // An EXPLICIT despawn (portal Destroy()'d while its scene stays loaded — e.g. a hub timeout)
        // plays the despawn sound via an independent source that outlives us. When the portal vanishes
        // because its scene is unloading (the player travelled through it), scene.isLoaded is false and
        // we stay silent.
        if (!gameObject.scene.isLoaded) return;

        var am = AudioManager.Instance;
        var lib = am != null ? am.Library : null;
        if (am != null && lib != null && lib.portalDespawn != null)
            am.PlaySfxAt(lib.portalDespawn, transform.position, spatial);
    }
}
