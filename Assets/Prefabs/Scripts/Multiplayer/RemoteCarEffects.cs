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
/// The state rides one extra byte on the existing 30 Hz CAR stream (see <see cref="Encode"/>): two flag
/// bits — turbo trails, flame flare — plus a 2-bit SD index. It is LEVEL-triggered (the owner's current
/// state, not edge events), so a dropped Unreliable packet self-heals on the next of ~30/s instead of
/// latching an effect on or off.
/// </summary>
public class RemoteCarEffects : MonoBehaviour
{
    // ---- Wire format: one byte carried by the CAR stream ----
    public const byte FlagTurbo = 0x01;   // bit 0: a rear tire is laying a turbo skid mark
    public const byte FlagFlame = 0x02;   // bit 1: the jet flame flare is showing
    const int SdShift = 2;                // bits 2-3: active SD index (0..3)
    const byte SdMask = 0x0C;

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

    // ---- Applied state (edge-detected so we never restart a running particle or thrash SetActive) ----
    private bool flameShown;
    private string sdShown;      // null == none
    private bool trailShown;

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

        BuildTrails(prefab);
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
    public void ApplyFlags(byte flags)
    {
        bool turbo = (flags & FlagTurbo) != 0;
        bool flame = (flags & FlagFlame) != 0;
        string sd = SdOrder[(flags & SdMask) >> SdShift];

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
