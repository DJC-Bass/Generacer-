using UnityEngine;

/// <summary>
/// Boulder audio: a 3D spawn one-shot, a looping "on fire" sound while the boulder is airborne, and
/// an impact one-shot on its first landing. Clips come from the shared <see cref="AudioLibrary"/>
/// (Boulder Spawn / Boulder Fly / Boulder Impact); the 3D playback settings are exposed here
/// (Spatial 3D Settings) for per-prefab tweaking.
///
/// Put this on the boulder prefab's ROOT (the object with the Rigidbody, so it receives collision
/// callbacks). It creates and manages its own AudioSources — no other setup needed. Because it runs
/// its own collision check, nothing in <see cref="BoulderObstacle"/> has to change.
/// </summary>
public class BoulderAudio : MonoBehaviour
{
    [Tooltip("3D playback settings shared by all three boulder sounds — tweak spatial blend, volume " +
             "and distance falloff here.")]
    public Spatial3DSettings spatial = new Spatial3DSettings();

    [Tooltip("Ignore collisions for this long after spawn, so the launch itself isn't mistaken for an " +
             "impact (seconds).")]
    public float impactArmDelay = 0.1f;

    /// <summary>Set on a client's PUPPET: do not judge impacts locally, wait to be told. A puppet's
    /// Rigidbody is kinematic, so it registers no contact against the STATIC track — where most
    /// boulders land — while still feeling the local player's dynamic car. Judging locally would
    /// therefore miss the common case and double up on the rare one.</summary>
    [HideInInspector] public bool impactsFromNetwork;

    private AudioSource loopSource;      // looping 'on fire' flight sound
    private AudioSource oneShotSource;   // spawn + impact one-shots (separate, so stopping the loop
                                         // on impact never cuts the impact sound)
    private float spawnTime;
    private bool impacted;

    /// <summary>Copies the tuning off another instance — used for a client's PUPPET, which is cloned
    /// from a prefab whose scripts were stripped and so comes up on code defaults. Same trick, and the
    /// same reason, as DroneDamageTint.CopyTuningFrom.</summary>
    public void CopyTuningFrom(BoulderAudio src)
    {
        if (src == null) return;
        spatial = src.spatial;
        impactArmDelay = src.impactArmDelay;
    }

    void Start()
    {
        spawnTime = Time.time;

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
            if (lib.boulderFly != null) { loopSource.clip = lib.boulderFly; loopSource.Play(); }
            if (lib.boulderSpawn != null) oneShotSource.PlayOneShot(lib.boulderSpawn);
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if (impactsFromNetwork) return;   // a puppet is told about impacts, it does not find them
        if (impacted || Time.time - spawnTime < impactArmDelay) return;   // still launching → not an impact
        impacted = true;

        if (loopSource != null) loopSource.Stop();   // no longer flying — cut the fire loop

        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (oneShotSource != null && lib != null && lib.boulderImpact != null)
            oneShotSource.PlayOneShot(lib.boulderImpact);

        // Host only (a no-op elsewhere): boulders are host-simulated, so every other machine's copy is
        // a puppet that will never feel this landing for itself.
        NpcReplicator.ReportNpcSound(gameObject, transform.position, NpcReplicator.NpcSound.BoulderImpact);
    }

    /// <summary>A landing we were TOLD about (client puppets): play it from THIS boulder's own source
    /// and cut the flight loop, so a boulder that has come to rest stops roaring wherever it is heard.
    /// Using the puppet's own source rather than a free-standing one-shot keeps the sound exactly on the
    /// rock and already carries the prefab's tuning.</summary>
    public void PlayNetworkImpact()
    {
        if (impacted) return;
        impacted = true;
        if (loopSource != null) loopSource.Stop();

        var lib = AudioManager.Instance != null ? AudioManager.Instance.Library : null;
        if (oneShotSource != null && lib != null && lib.boulderImpact != null)
            oneShotSource.PlayOneShot(lib.boulderImpact);
    }
}
