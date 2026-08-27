using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a remote car PUPPET's visual flourishes — turbo tire trails, the jet-jump flame flare, and
/// the per-SD activation particle burst — off the owner's replicated state, so every player sees
/// everyone else boost, jump-flame and pop their SD instead of only their own car.
///
/// A puppet is a stripped clone (<see cref="RemoteCarManager.StripPuppet"/>): CarController, JetFlames
/// and SDAbilityVFX are all destroyed, so nothing on it would ever switch these on. RemoteCarManager
/// captures the pieces this needs while the clone is still whole — the flame GameObjects and each SD's
/// particle system from the INSTANCE, the trail tuning + rear-wheel offsets from the car PREFAB asset —
/// and hands them to <see cref="Configure"/>. (The trail renderers don't exist on the prefab at all;
/// the owner builds them at runtime, so we rebuild them here.)
///
/// The state rides three extra bytes on the existing 30 Hz CAR stream: a flags byte (see
/// <see cref="Encode"/>) carrying turbo trails, flame flare and a 2-bit SD index, plus two bytes of
/// drift-screech drive. All of it is LEVEL-triggered (the owner's current state, not edge events), so a
/// dropped Unreliable packet self-heals on the next of ~30/s instead of latching an effect on or off.
///
/// This class also owns the remote car's "while active" ability LOOPS - shield, SD, drift screech.
/// One-shots relay as events on GNRC_CAR_SFX; a loop cannot, because it has to start, follow the car
/// for as long as the ability lasts, and stop, which is precisely what level-triggered state gives you
/// and an event does not.
/// </summary>
public class RemoteCarEffects : MonoBehaviour
{
    // ---- Wire format: one byte carried by the CAR stream ----
    public const byte FlagTurbo = 0x01;   // bit 0: a rear tire is laying a turbo skid mark
    public const byte FlagFlame = 0x02;   // bit 1: the jet flame flare is showing
    const int SdShift = 2;                // bits 2-3: active SD index (0..3)
    const byte SdMask = 0x0C;
    // bit 4 is AreaInTrackFlag, owned by RemoteCarManager (which area the owner is in).
    /// <summary>Bit 5: the owner's Shield is summoned. Replicating it does double duty — remote players
    /// SEE the shield, and on the HOST (where projectile hits are decided) the puppet's shield collider
    /// goes live, so it actually blocks incoming drone fire for its owner.</summary>
    public const byte FlagShield = 0x20;

    /// <summary>Bit 6: the owner's car is off the ground. Purely a TARGETING input - nothing visual
    /// reads it. Host-side hunters (lava boulders, drones) decide how to behave from the player's
    /// airborne state, and a puppet has no CarController to ask, so before this bit existed every one
    /// of them silently treated remote players as permanently grounded.</summary>
    public const byte FlagAirborne = 0x40;

    // Canonical SD ordering both ends agree on (index 0 = "none"). Names match the SD inventory items
    // and the SDAbilityVFX entries; a car simply has no captured particle system for an SD it can't show.
    static readonly string[] SdOrder = { null, "Fire SD", "Wind SD", "Lightning SD" };

    static int SdToIndex(string sd)
    {
        if (!string.IsNullOrEmpty(sd))
            for (int i = 1; i < SdOrder.Length; i++)
                if (SdOrder[i] == sd) return i;
        return 0;
    }

    /// <summary>Owner side: packs its current effect state into the byte the CAR stream carries.</summary>
    public static byte Encode(bool turboTrails, bool flameFlaring, string activeSd)
    {
        byte b = 0;
        if (turboTrails) b |= FlagTurbo;
        if (flameFlaring) b |= FlagFlame;
        b |= (byte)((SdToIndex(activeSd) & 0x3) << SdShift);
        return b;
    }

    // ---- Captured visuals (set once at build, before the puppet activates) ----
    private GameObject[] flameObjects;
    private Dictionary<string, ParticleSystem> sdSystems;
    private TrailRenderer[] trails;
    private GameObject shieldObject;   // the puppet's own Shield child (mesh + collider on the Shield layer)

    // ---- Applied state (edge-detected so we never restart a running particle or thrash SetActive) ----
    private bool flameShown;
    private string sdShown;      // null == none
    private bool trailShown;
    private bool shieldShown;

    /// <summary>Name of the shield child on the car prefab. Must match ShieldAbility.shieldChildName —
    /// puppets have their scripts stripped, so the shield can only be found by NAME here.</summary>
    const string ShieldChildName = "Shield";

    /// <summary>Hands over the captured flame objects + SD particle systems (from the puppet instance)
    /// and the car prefab (read for trail tuning + rear-wheel offsets). Call before the puppet activates.</summary>
    public void Configure(GameObject prefab, List<GameObject> flames,
                          List<KeyValuePair<string, ParticleSystem>> sds)
    {
        flameObjects = flames != null ? flames.ToArray() : System.Array.Empty<GameObject>();

        sdSystems = new Dictionary<string, ParticleSystem>();
        if (sds != null)
            foreach (var kv in sds)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    sdSystems[kv.Key] = kv.Value;

        // The shield rides on the puppet itself (StripPuppet keeps meshes + colliders), so we just find
        // and hide it; the replicated flag bit raises it.
        var shield = FindChildByName(transform, ShieldChildName);
        shieldObject = shield != null ? shield.gameObject : null;
        if (shieldObject != null) shieldObject.SetActive(false);

        BuildTrails(prefab);
        CaptureDriftTuning(prefab);
    }

    /// <summary>Copies the drift-screech tuning off the car PREFAB's CarController. The puppet's own
    /// copy was destroyed by the strip, and these numbers have to match the owner's exactly - otherwise
    /// the same drift would sound like a different car to every listener.</summary>
    void CaptureDriftTuning(GameObject prefab)
    {
        var cc = prefab != null ? prefab.GetComponentInChildren<CarController>(true) : null;
        if (cc == null) return;
        driftMaxVolume = cc.driftScreechMaxVolume;
        driftMinPitch = cc.driftScreechMinPitch;
        driftMaxPitch = cc.driftScreechMaxPitch;
        driftResponsiveness = cc.driftScreechResponsiveness;
        driftSpatialBlend = cc.driftScreechSpatialBlend;
        driftMaxDistance = cc.driftScreechMaxDistance;
    }

    /// <summary>Depth-first child search by name, including INACTIVE objects — the shield sits inactive
    /// on the prefab, so an active-only search would never find it.</summary>
    static Transform FindChildByName(Transform root, string name)
    {
        foreach (Transform child in root)
        {
            if (string.Equals(child.name, name, System.StringComparison.OrdinalIgnoreCase)) return child;
            var deeper = FindChildByName(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>Recreates the two rear-tire turbo trails the owner's CarController builds at runtime
    /// (they aren't authored into the prefab). Tuning + rear-wheel local offsets come off the prefab's
    /// CarController; the puppet has no live suspension, so the trail sits at a fixed offset under each
    /// rear wheel rather than a raycast ground-contact — indistinguishable at speed.</summary>
    void BuildTrails(GameObject prefab)
    {
        var cc = prefab != null ? prefab.GetComponentInChildren<CarController>(true) : null;
        if (cc == null || !cc.turboTrails) return;
        if (cc.wheelRL == null || cc.wheelRR == null) return;

        Material mat = cc.turboTrailMaterial != null
            ? cc.turboTrailMaterial
            : new Material(Shader.Find("Sprites/Default"));

        Transform root = prefab.transform;
        Vector3 rl = root.InverseTransformPoint(cc.wheelRL.transform.position);
        Vector3 rr = root.InverseTransformPoint(cc.wheelRR.transform.position);
        rl.y -= cc.wheelRL.radius;   // drop to the tire's contact patch
        rr.y -= cc.wheelRR.radius;

        trails = new[]
        {
            BuildOneTrail("RemoteTurboTrailRL", rl, mat, cc),
            BuildOneTrail("RemoteTurboTrailRR", rr, mat, cc),
        };
    }

    TrailRenderer BuildOneTrail(string trailName, Vector3 localPos, Material mat, CarController cc)
    {
        var go = new GameObject(trailName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;

        var tr = go.AddComponent<TrailRenderer>();
        tr.time = Mathf.Max(0.01f, cc.turboTrailTime);
        tr.startWidth = cc.turboTrailWidth;
        tr.endWidth = cc.turboTrailWidth * 0.6f;
        tr.minVertexDistance = 0.05f;
        tr.numCornerVertices = 2;
        tr.numCapVertices = 2;
        tr.autodestruct = false;
        tr.emitting = false;
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tr.receiveShadows = false;
        tr.material = mat;

        // Solid at the tire, fading to transparent at the tail — matches the owner's trail.
        var grad = new Gradient();
        Color c = cc.turboTrailColor;
        grad.SetKeys(
            new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
            new[] { new GradientAlphaKey(c.a, 0f), new GradientAlphaKey(0f, 1f) });
        tr.colorGradient = grad;
        return tr;
    }

    /// <summary>Applies a freshly received flags byte to the puppet's visuals. Cheap when nothing changed.</summary>
    // ---- "while active" LOOPS for a remote player's abilities ----
    //
    // ⚠️ The one-shots ride GNRC_CAR_SFX, but a LOOP cannot: it has to start, follow the car for as
    // long as the ability lasts, and stop. So it is driven off the FLAGS instead, which are already
    // replicated, already level-triggered, and already self-healing — exactly the properties a loop
    // needs and a one-shot event does not. Same choice as re-adding BoulderAudio to a boulder puppet
    // rather than relaying its flight sound.
    //
    // The sources live on THIS object, which is the puppet, so they follow the remote car for free.
    private AudioSource shieldLoop, sdLoop;
    private bool sdLoopOn;

    // ---- Drift screech: the third loop, and the only one with a CONTINUOUS drive ----
    //
    // Shield and SD are on/off, so a flag bit says everything there is to say. A tire screech is not:
    // its volume and pitch ride the owner's steering and speed from moment to moment, which is why it
    // gets two quantised bytes on the state stream instead of a bit. Only the TARGETS travel; the
    // easing below runs locally at the owner's own responsiveness, so 30 packets a second still come
    // out as one continuous screech rather than 30 audible steps.
    private AudioSource driftSource;
    private float driftLevel, driftSteer;
    private float driftMaxVolume = 1f, driftMinPitch = 0.9f, driftMaxPitch = 1.5f;
    private float driftResponsiveness = 10f, driftSpatialBlend = 1f, driftMaxDistance = 80f;

    /// <summary>Feeds the replicated screech drives: <paramref name="level01"/> is steering x speed
    /// (zero unless the owner is drifting on the ground), <paramref name="steer01"/> is the raw stick.</summary>
    public void ApplyDrift(float level01, float steer01)
    {
        driftLevel = level01;
        driftSteer = steer01;
    }

    void Update()
    {
        UpdateDriftAudio();
    }

    /// <summary>Mirrors CarController.UpdateDriftAudio for a remote car. The AudioSource is built the
    /// first time this car actually screeches near us - a car that never drifts in earshot never costs
    /// us a voice.</summary>
    void UpdateDriftAudio()
    {
        if (driftSource == null)
        {
            if (driftLevel <= 0f) return;
            var clip = Lib != null ? Lib.driftScreech : null;
            if (clip == null) return;

            driftSource = gameObject.AddComponent<AudioSource>();
            driftSource.clip = clip;
            driftSource.loop = true;
            driftSource.playOnAwake = false;
            driftSource.spatialBlend = driftSpatialBlend;   // 3D: it is THEIR tires, not ours
            driftSource.rolloffMode = AudioRolloffMode.Linear;
            driftSource.minDistance = 5f;
            driftSource.maxDistance = driftMaxDistance;
            driftSource.dopplerLevel = 0f;   // pitch is their steering; our closing speed must not shift it
            driftSource.volume = 0f;
            driftSource.pitch = driftMinPitch;
            driftSource.Play();
        }

        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
        float targetVol = driftMaxVolume * driftLevel * sfx;
        float targetPitch = Mathf.Lerp(driftMinPitch, driftMaxPitch, driftSteer);

        float k = 1f - Mathf.Exp(-driftResponsiveness * Time.deltaTime);
        driftSource.volume = Mathf.Lerp(driftSource.volume, targetVol, k);
        driftSource.pitch = Mathf.Lerp(driftSource.pitch, targetPitch, k);
    }

    static AudioLibrary Lib => AudioManager.Instance != null ? AudioManager.Instance.Library : null;

    /// <summary>Starts or stops one looping ability sound on this puppet, creating its AudioSource the
    /// first time it is actually needed — most remote cars never shield or SD at all.</summary>
    void ApplyLoop(ref AudioSource source, bool on, AudioClip clip, Spatial3DSettings tuning)
    {
        if (!on)
        {
            if (source != null) source.Stop();
            return;
        }
        if (clip == null) return;

        if (source == null)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
        }
        source.clip = clip;
        float sfx = AudioManager.Instance != null ? AudioManager.Instance.SfxVolume : 1f;
        if (tuning != null) tuning.ApplyTo(source, sfx);
        else
        {
            source.spatialBlend = 1f;   // 3D: it belongs to THEIR car, not ours
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 8f;
            source.maxDistance = 150f;
            source.dopplerLevel = 0f;
            source.volume = sfx;
        }
        source.Play();
    }

    public void ApplyFlags(byte flags)
    {
        bool turbo = (flags & FlagTurbo) != 0;
        bool flame = (flags & FlagFlame) != 0;
        string sd = SdOrder[(flags & SdMask) >> SdShift];
        bool sdActive = !string.IsNullOrEmpty(sd);
        if (sdActive != sdLoopOn)
        {
            sdLoopOn = sdActive;
            ApplyLoop(ref sdLoop, sdActive, Lib != null ? Lib.sdActiveLoop : null, null);
        }

        // Shield: level-triggered like the rest, so a dropped packet self-heals on the next update.
        bool shield = (flags & FlagShield) != 0;
        if (shield != shieldShown)
        {
            shieldShown = shield;
            if (shieldObject != null) shieldObject.SetActive(shield);
            ApplyLoop(ref shieldLoop, shield, Lib != null ? Lib.shieldActiveLoop : null,
                      Lib != null ? Lib.shieldAudio3D : null);
        }

        // The flame flare's rising edge IS a jump — break the trail there, exactly as the owner does
        // when it leaves the ground, so a jet-jump doesn't smear a straight line across the arc.
        if (flame && !flameShown) ClearTrails();

        if (flame != flameShown)
        {
            flameShown = flame;
            if (flameObjects != null)
                foreach (var f in flameObjects)
                    if (f != null) f.SetActive(flame);
        }

        if (sd != sdShown)
        {
            SetSd(sdShown, false);
            SetSd(sd, true);
            sdShown = sd;
        }

        if (turbo != trailShown)
        {
            trailShown = turbo;
            SetTrailsEmitting(turbo);
        }
    }

    // Play/stop one SD's particle system, activating/deactivating its GameObject — mirrors SDAbilityVFX.
    void SetSd(string sd, bool on)
    {
        if (string.IsNullOrEmpty(sd) || sdSystems == null) return;
        if (!sdSystems.TryGetValue(sd, out var ps) || ps == null) return;

        var go = ps.gameObject;
        if (on)
        {
            if (!go.activeSelf) go.SetActive(true);
            if (!ps.isPlaying) ps.Play(true);
        }
        else if (go.activeSelf)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            go.SetActive(false);
        }
    }

    void SetTrailsEmitting(bool on)
    {
        if (trails == null) return;
        foreach (var tr in trails)
            if (tr != null) tr.emitting = on;
    }

    /// <summary>Wipes the laid-down ribbon so a teleport / area-change / jump doesn't streak a line
    /// across the gap. Called by <see cref="RemoteCarPuppet"/> on every snap.</summary>
    public void ClearTrails()
    {
        if (trails == null) return;
        foreach (var tr in trails)
            if (tr != null) tr.Clear();
    }
}
