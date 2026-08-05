using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Recolours the environment skybox to a random HUE each time a scene loads whose skybox is the
/// "SimpleSkybox" (a Skybox/Procedural material). It shifts the Sky Tint and Ground colours to one
/// fresh random hue, keeping their saturation, value, and every other skybox setting — so the sky
/// comes out a different colour every scene load.
///
/// On the custom Skybox/ProceduralSkyClouds shader it ALSO shifts the Horizon and lit Cloud colours.
/// Both are optional: each is applied only when the material actually has that property, so the plain
/// built-in Skybox/Procedural material keeps working untouched.
///
/// Works on an INSTANCE of the material, never the shared asset on disk (like TrackGenerator's road
/// material). RenderSettings.skybox is per-scene, so each load starts from the assigned SimpleSkybox
/// and gets a fresh random instance; the previous instance is freed. Persistent + self-bootstrapped,
/// so there's no scene setup — it just acts whenever a scene's skybox is the SimpleSkybox.
/// </summary>
public class SkyboxHueRandomizer : MonoBehaviour
{
    // The skybox material this applies to, matched by name (our instances keep this as a prefix).
    const string TargetName = "SimpleSkybox";

    // Optional properties, present only on Skybox/ProceduralSkyClouds. Guarded with HasProperty so the
    // built-in Skybox/Procedural material (which has neither) is unaffected.
    const string HorizonProp = "_HorizonColor";
    const string CloudProp = "_CloudColor";
    const string GroundCloudProp = "_GroundCloudColor";
    const string NightTintProp = "_NightTint";

    [Tooltip("Saturation floor applied when hue-shifting the lit CLOUD colour. Hue is meaningless on a " +
             "pure-white colour (saturation 0), so without this the default white clouds would never " +
             "visibly tint. 0 = faithfully keep the authored saturation, like the other colours.")]
    public float cloudMinSaturation = 0.2f;

    [Tooltip("How far the HORIZON hue sits from the sky hue (0..1 = the full colour wheel; 0.06 ≈ 22°). " +
             "Derived rather than rolled independently so the gradient always reads as one atmosphere " +
             "instead of occasionally clashing. 0 = identical hue to the sky.")]
    public float horizonHueOffset = 0.06f;

    [Tooltip("How far the GROUND TEXTURE hue sits from the ground hue. Same reasoning as the horizon " +
             "offset — it keeps the mottling reading as part of the ground rather than a separate layer.")]
    public float groundTextureHueOffset = 0.04f;

    [Tooltip("Saturation CEILING for the random NIGHT TINT. The tint MULTIPLIES the (already hue-shifted) " +
             "sky, so a strongly saturated tint on a clashing hue — red over green, say — cancels the " +
             "channels and collapses the night sky to near-black. Capping it keeps the cast readable. " +
             "1 = no cap (allows those very dark nights).")]
    public float nightTintMaxSaturation = 0.5f;

    private readonly System.Random rng = new System.Random();
    private Material instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("SkyboxHueRandomizer");
        go.AddComponent<SkyboxHueRandomizer>();
        DontDestroyOnLoad(go);
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    // sceneLoaded may not fire for the scene already active when we bootstrap, so also run once here.
    void Start() => Recolor();

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // An additive load (multiplayer's track area) doesn't change RenderSettings — those follow
        // the ACTIVE scene, handled by OnActiveSceneChanged when MultiplayerWorld teleports us.
        if (mode == LoadSceneMode.Additive) return;
        Recolor();
    }

    // Multiplayer teleports switch the active scene (hub ⇄ track area) — recolor the incoming sky.
    void OnActiveSceneChanged(Scene from, Scene to) => Recolor();

    void Recolor()
    {
        var sky = RenderSettings.skybox;
        if (sky == null) return;

        // Only our target SimpleSkybox (a Skybox/Procedural material with Sky Tint + Ground).
        if (!sky.name.StartsWith(TargetName)) return;
        if (!sky.HasProperty("_SkyTint") || !sky.HasProperty("_GroundColor")) return;

        // Copy the CURRENT skybox (the shared asset, or our own instance if the scene didn't reset it)
        // so the asset on disk is never modified.
        Material previous = instance;
        instance = new Material(sky) { name = TargetName + " (Random)" };

        // Independent random hue for each colour; each keeps its own saturation + value.
        // (0..1 == the full 0..360 hue wheel.) In a multiplayer round the hues derive from the
        // server's round seed, so every player sees the SAME sky — and repeated recolors within a
        // round (area teleports) are stable because the derived stream restarts identically.
        //
        // The three INDEPENDENT hues are drawn UP FRONT, before any HasProperty check, so the random
        // stream advances the same number of steps no matter which shader the skybox uses. Drawing them
        // lazily inside the optional branches would desync the seeded multiplayer hues the moment two
        // machines disagreed about the material.
        float hSky, hGround, hCloud, hNight;
        if (MultiplayerWorld.IsMultiplayerGame && MultiplayerWorld.CurrentRoundSeed != 0)
        {
            var seeded = MultiplayerWorld.DeriveRandom("skybox");
            hSky = (float)seeded.NextDouble();
            hGround = (float)seeded.NextDouble();
            hCloud = (float)seeded.NextDouble();
            hNight = (float)seeded.NextDouble();
        }
        else
        {
            hSky = (float)rng.NextDouble();
            hGround = (float)rng.NextDouble();
            hCloud = (float)rng.NextDouble();
            hNight = (float)rng.NextDouble();
        }

        // Horizon and ground-texture hues are DERIVED, not rolled: each sits a fixed step around the
        // wheel from the layer it belongs to, so the sky gradient and the ground always read as single
        // coherent surfaces instead of occasionally landing on clashing pairs.
        float hHorizon = Mathf.Repeat(hSky + horizonHueOffset, 1f);
        float hGroundTexture = Mathf.Repeat(hGround + groundTextureHueOffset, 1f);

        ShiftHue(instance, "_SkyTint", hSky);
        ShiftHue(instance, "_GroundColor", hGround);

        // Only on the ProceduralSkyClouds shader — the built-in procedural material has none of these.
        if (instance.HasProperty(HorizonProp)) ShiftHue(instance, HorizonProp, hHorizon);
        if (instance.HasProperty(CloudProp)) ShiftHue(instance, CloudProp, hCloud, cloudMinSaturation);
        if (instance.HasProperty(GroundCloudProp)) ShiftHue(instance, GroundCloudProp, hGroundTexture);
        if (instance.HasProperty(NightTintProp))
            ShiftHue(instance, NightTintProp, hNight, maxSaturation: nightTintMaxSaturation);

        RenderSettings.skybox = instance;
        DynamicGI.UpdateEnvironment();   // refresh ambient / reflections from the recoloured sky

        if (previous != null) Destroy(previous);   // free the prior instance (already copied from if current)
    }

    /// <summary>
    /// Re-hues one colour property, keeping its saturation and value. The two optional bounds exist for
    /// opposite reasons, and both default to "no clamping" so the original colours behave exactly as before:
    ///   • <paramref name="minSaturation"/> — hue has NO effect on a fully desaturated colour, so a white
    ///     (S=0) source would come back white whatever the hue. Raises the floor (used by the clouds).
    ///   • <paramref name="maxSaturation"/> — caps how vivid the result can get, for a colour that gets
    ///     MULTIPLIED into another (the night tint), where a strong clashing hue cancels channels to black.
    /// </summary>
    static void ShiftHue(Material mat, string prop, float hue,
                         float minSaturation = 0f, float maxSaturation = 1f)
    {
        Color c = mat.GetColor(prop);
        Color.RGBToHSV(c, out _, out float s, out float v);   // keep saturation + value

        float lo = Mathf.Clamp01(minSaturation);
        float hi = Mathf.Max(lo, Mathf.Clamp01(maxSaturation));   // a bad pair can't invert the range
        s = Mathf.Clamp(s, lo, hi);

        Color shifted = Color.HSVToRGB(hue, s, v);
        shifted.a = c.a;                                       // leave alpha untouched
        mat.SetColor(prop, shifted);
    }
}
