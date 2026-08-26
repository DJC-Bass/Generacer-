using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visual damage feedback for a drone or a Support Ship: a red HIT FLASH the instant something lands,
/// and a red WRECK TINT that appears only once the thing is actually going down.
///
/// The split is deliberate. A flash is punctuation — it answers "did that shot connect?" and then gets
/// out of the way. A tint is a state, and a state meaning "still alive, just hurt" is the wrong thing
/// to paint on a drone: a sky full of permanently red planes stops meaning anything, and the colour is
/// no longer readable as the kill it eventually becomes. So health-pool progress is carried entirely by
/// the flashes the gunner counts, and red-and-staying-red means dead.
///
/// It works by overriding the renderers' colour REGISTERS through a <see cref="MaterialPropertyBlock"/>,
/// never by touching materials. That distinction matters: reading <c>Renderer.material</c> silently
/// clones the material, which leaks one instance per drone per round and drops them all out of
/// batching. A property block writes the override into the draw call instead — no clone, no leak, and
/// the shared material on disk is never modified.
///
/// It is also why the override is REMOVED again the moment there is nothing to draw (see
/// <see cref="ClearOverrides"/>). A renderer carrying a property block is excluded from the SRP
/// Batcher for as long as it carries one, so a tint that persisted through a drone's whole life would
/// quietly cost every one of its draw calls the batch for the rest of the round. Flash, settle, clear.
///
/// The original colours are cached PER RENDERER PER MATERIAL SLOT at startup, so the wreck tint is
/// applied relative to whatever the model was actually authored as rather than assuming everything
/// starts white. A drone with a red panel and a grey hull stays recognisably itself while both darken.
///
/// Multiplayer: drones are host-simulated and clients see stripped puppets, so a client's copy has no
/// DronePlane to tell it anything. <see cref="NpcReplicator"/> therefore streams a damage EVENT and
/// adds this component to the puppet on arrival — which matters because the Support Ship gunner is
/// very often a client, and they are the whole audience for this.
/// </summary>
[DisallowMultipleComponent]
public class DroneDamageTint : MonoBehaviour
{
    [Header("Hit Flash (one per hit landed)")]
    [Tooltip("Colour flashed the instant a hit lands. Red so a connecting shot reads as damage at " +
             "distance — this is the ONLY feedback a surviving drone gives, so it has to carry.")]
    public Color flashColor = new Color(1f, 0.16f, 0.1f, 1f);
    [Tooltip("Seconds the flash takes to fade out. Short — this is a punctuation mark, not a state.")]
    public float flashDuration = 0.12f;
    [Range(0f, 1f)]
    [Tooltip("How completely the flash overrides the model's own colour at its peak.")]
    public float flashStrength = 1f;

    [Header("Wreck Tint (downed / ragdolling only)")]
    [Tooltip("Colour the wreck is tinted toward once it goes down. Held for the whole ragdoll, so a " +
             "kill stays legible while the thing tumbles away.")]
    public Color downedColor = new Color(1f, 0.22f, 0.12f, 1f);
    [Range(0f, 1f)]
    [Tooltip("How far toward Downed Colour a wreck is pushed. 1 replaces the model's colours entirely, " +
             "which reads clearly but loses the silhouette's own palette; ~0.8 keeps a hint of it.")]
    public float downedTintStrength = 0.8f;

    [Header("Emission")]
    [Tooltip("Also drive the material's emission, so the flash and wreck tint GLOW rather than just " +
             "recolour. ⚠️ Only visible on materials that already have Emission enabled — a property " +
             "block cannot switch the shader keyword on, so a material with emission off ignores this.")]
    public bool boostEmission = true;
    [Tooltip("Multiplier on the emission added by the flash and wreck tint. Above 1 pushes into HDR bloom.")]
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

    private bool downed;             // wrecked: hold the tint until this object is destroyed
    private float flashUntil = -999f;
    private bool dirty;
    private bool overrideActive;     // our property block is currently installed on the renderers

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
        flashColor = src.flashColor;
        flashDuration = src.flashDuration;
        flashStrength = src.flashStrength;
        downedColor = src.downedColor;
        downedTintStrength = src.downedTintStrength;
        boostEmission = src.boostEmission;
        emissionIntensity = src.emissionIntensity;
    }

    /// <summary>Punches the flash for <see cref="flashDuration"/>. Call once per hit landed.</summary>
    public void Flash()
    {
        flashUntil = Time.time + Mathf.Max(0.01f, flashDuration);
        dirty = true;
    }

    /// <summary>Paints and HOLDS the wreck tint. Call when the thing goes down / enters its ragdoll;
    /// there is no way back, since the object is destroyed at the end of it.
    ///
    /// Deliberately does not also flash: the flash colour and the wreck tint are both red, so a flash
    /// laid over a full-strength tint would be invisible. The tint appearing IS the punctuation.</summary>
    public void MarkDowned()
    {
        if (downed) return;
        downed = true;
        dirty = true;
    }

    /// <summary>Convenience for "a hit just landed", and the one entry point the network path uses.
    /// A report where the pool is spent (<paramref name="hitsTaken"/> at or past
    /// <paramref name="maxHits"/>) is a KILL, not a hit, and paints the wreck tint instead.</summary>
    public void RegisterHit(int hitsTaken, int maxHits)
    {
        if (hitsTaken >= Mathf.Max(1, maxHits)) { MarkDowned(); return; }
        Flash();
    }

    void LateUpdate()
    {
        bool flashing = Time.time < flashUntil;
        if (!dirty && !flashing) return;   // nothing moving: leave the draw calls alone

        Apply(flashing);
        dirty = flashing;   // one more pass after the flash ends, to settle back down
    }

    void Apply(bool flashing)
    {
        float flash = flashing
            ? flashStrength * Mathf.Clamp01((flashUntil - Time.time) / Mathf.Max(0.01f, flashDuration))
            : 0f;
        float tint = downed ? Mathf.Clamp01(downedTintStrength) : 0f;

        // Nothing left to override — hand the renderers back to the batcher rather than pinning them
        // with a block that only restates the colours they already have.
        if (flash <= 0f && tint <= 0f) { ClearOverrides(); return; }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.renderer == null) continue;

            slot.renderer.GetPropertyBlock(block, slot.index);

            if (slot.hasColor)
            {
                Color c = Color.Lerp(slot.baseColor, downedColor, tint);
                c = Color.Lerp(c, flashColor, flash);
                block.SetColor(slot.colorId, c);
            }

            if (slot.hasEmission && boostEmission)
            {
                // ADDED to whatever the model already emits, so a drone with glowing panels keeps them
                // and simply runs hotter when it is hit or wrecked.
                Color glow = (downedColor * tint + flashColor * flash) * emissionIntensity;
                block.SetColor(EmissionId, slot.baseEmission + glow);
            }

            slot.renderer.SetPropertyBlock(block, slot.index);
        }

        overrideActive = true;
    }

    /// <summary>Removes our override entirely, restoring the material's own colours.
    ///
    /// Passing NULL is what actually does it — an emptied-and-reapplied block still leaves the renderer
    /// flagged as carrying per-instance overrides, which is the thing that keeps it out of the SRP
    /// Batcher. This is the whole reason a surviving drone costs nothing between flashes.</summary>
    void ClearOverrides()
    {
        if (!overrideActive) return;
        overrideActive = false;

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.renderer != null) slot.renderer.SetPropertyBlock(null, slot.index);
        }
    }
}
