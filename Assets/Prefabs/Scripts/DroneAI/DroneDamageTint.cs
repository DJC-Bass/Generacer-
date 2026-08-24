using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visual damage feedback for a drone: a white HIT FLASH the instant something lands, over a colour
/// TINT that deepens as its health pool empties. Together they answer the gunner's two questions —
/// "did that shot connect?" and "how close is this thing to going down?" — without a health bar.
///
/// It works by overriding the renderers' colour REGISTERS through a <see cref="MaterialPropertyBlock"/>,
/// never by touching materials. That distinction matters: reading <c>Renderer.material</c> silently
/// clones the material, which leaks one instance per drone per round and drops them all out of
/// batching. A property block writes the override into the draw call instead — no clone, no leak, and
/// the shared material on disk is never modified.
///
/// The original colours are cached PER RENDERER PER MATERIAL SLOT at startup, so the tint is applied
/// relative to whatever the model was actually authored as rather than assuming everything starts
/// white. A drone with a red panel and a grey hull stays recognisably itself while both darken.
///
/// Multiplayer: drones are host-simulated and clients see stripped puppets, so a client's copy has no
/// DronePlane to tell it anything. <see cref="NpcReplicator"/> therefore streams a damage EVENT and
/// adds this component to the puppet on arrival — which matters because the Support Ship gunner is
/// very often a client, and they are the whole audience for this.
/// </summary>
[DisallowMultipleComponent]
public class DroneDamageTint : MonoBehaviour
{
    [Header("Damage Tint (deepens as health drops)")]
    [Tooltip("Colour the drone is tinted TOWARD as its health pool empties. At full health there is no " +
             "tint at all; at zero it is this colour by Max Tint Strength.")]
    public Color damageColor = new Color(1f, 0.22f, 0.12f, 1f);
    [Range(0f, 1f)]
    [Tooltip("How far toward Damage Colour a drone on its last hit is pushed. 1 replaces the model's " +
             "colours entirely, which reads clearly but loses the silhouette's own palette; ~0.8 keeps " +
             "a hint of the original.")]
    public float maxTintStrength = 0.8f;

    [Header("Hit Flash (one per hit landed)")]
    [Tooltip("Colour flashed the instant a hit lands, on top of the damage tint. White reads on almost " +
             "any model; the drones are dark, so a bright flash is what actually registers at distance.")]
    public Color flashColor = Color.white;
    [Tooltip("Seconds the flash takes to fade out. Short — this is a punctuation mark, not a state.")]
    public float flashDuration = 0.12f;
    [Range(0f, 1f)]
    [Tooltip("How completely the flash overrides the current colour at its peak.")]
    public float flashStrength = 1f;

    [Header("Emission")]
    [Tooltip("Also drive the material's emission, so the tint and flash GLOW rather than just recolour. " +
             "⚠️ Only visible on materials that already have Emission enabled — a property block cannot " +
             "switch the shader keyword on, so a material with emission off will ignore this entirely.")]
    public bool boostEmission = true;
    [Tooltip("Multiplier on the emission added by the tint and flash. Above 1 pushes into HDR bloom.")]
    public float emissionIntensity = 2f;

    // One entry per renderer per material slot — a renderer with three materials needs three, since a
    // property block is applied per slot and each slot has its own authored colour to lerp from.
    private struct Slot
    {
        public Renderer renderer;
        public int index;
        public int colorId;        // _BaseColor (URP) or _Color (built-in), whichever this shader has
        public Color baseColor;
        public bool hasColor;
        public Color baseEmission;
        public bool hasEmission;
    }
    private readonly List<Slot> slots = new List<Slot>();
    private MaterialPropertyBlock block;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    private float damage01;      // 0 = untouched, 1 = one hit from going down
    private float flashUntil = -999f;
    private bool dirty;

    void Awake()
    {
        block = new MaterialPropertyBlock();
        CacheSlots();
    }

    /// <summary>Records every renderer/material slot and the colours it was authored with. Inactive
    /// renderers are included: a drone's conditional visuals may switch on later, and a slot cached
    /// now costs nothing if it never renders.</summary>
    void CacheSlots()
    {
        slots.Clear();

        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                var slot = new Slot { renderer = renderer, index = i };

                if (material.HasProperty(BaseColorId))
                {
                    slot.colorId = BaseColorId;
                    slot.baseColor = material.GetColor(BaseColorId);
                    slot.hasColor = true;
                }
                else if (material.HasProperty(LegacyColorId))
                {
                    slot.colorId = LegacyColorId;
                    slot.baseColor = material.GetColor(LegacyColorId);
                    slot.hasColor = true;
                }

                if (material.HasProperty(EmissionId))
                {
                    slot.baseEmission = material.GetColor(EmissionId);
                    slot.hasEmission = true;
                }

                if (slot.hasColor || slot.hasEmission) slots.Add(slot);
            }
        }
    }

    /// <summary>Copies the tuning off another instance — used for a client's PUPPET, which is cloned
    /// from a prefab whose scripts were stripped and so comes up on code defaults. Same trick
    /// RemoteCarAudio and SupportShip use for the same reason.</summary>
    public void CopyTuningFrom(DroneDamageTint src)
    {
        if (src == null) return;
        damageColor = src.damageColor;
        maxTintStrength = src.maxTintStrength;
        flashColor = src.flashColor;
        flashDuration = src.flashDuration;
        flashStrength = src.flashStrength;
        boostEmission = src.boostEmission;
        emissionIntensity = src.emissionIntensity;
    }

    /// <summary>Sets how damaged the drone reads as: 0 untouched, 1 one hit from going down.</summary>
    public void SetDamage(float normalized)
    {
        float clamped = Mathf.Clamp01(normalized);
        if (Mathf.Approximately(clamped, damage01)) return;
        damage01 = clamped;
        dirty = true;
    }

    /// <summary>Punches the flash for <see cref="flashDuration"/>. Call once per hit landed.</summary>
    public void Flash()
    {
        flashUntil = Time.time + Mathf.Max(0.01f, flashDuration);
        dirty = true;
    }

    /// <summary>Convenience for the common "a hit just landed" case.</summary>
    public void RegisterHit(int hitsTaken, int maxHits)
    {
        SetDamage(maxHits > 1 ? (float)hitsTaken / maxHits : 1f);
        Flash();
    }

    void LateUpdate()
    {
        bool flashing = Time.time < flashUntil;
        if (!dirty && !flashing) return;   // nothing moving: leave the draw calls alone

        Apply(flashing);
        dirty = flashing;   // one more pass after the flash ends, to settle back to the plain tint
    }

    void Apply(bool flashing)
    {
        float flash = flashing
            ? flashStrength * Mathf.Clamp01((flashUntil - Time.time) / Mathf.Max(0.01f, flashDuration))
            : 0f;
        float tint = damage01 * maxTintStrength;

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.renderer == null) continue;

            slot.renderer.GetPropertyBlock(block, slot.index);

            if (slot.hasColor)
            {
                Color c = Color.Lerp(slot.baseColor, damageColor, tint);
                c = Color.Lerp(c, flashColor, flash);
                block.SetColor(slot.colorId, c);
            }

            if (slot.hasEmission && boostEmission)
            {
                // ADDED to whatever the model already emits, so a drone with glowing panels keeps them
                // and simply runs hotter as it takes damage.
                Color glow = (damageColor * tint + flashColor * flash) * emissionIntensity;
                block.SetColor(EmissionId, slot.baseEmission + glow);
            }

            slot.renderer.SetPropertyBlock(block, slot.index);
        }
    }
}
