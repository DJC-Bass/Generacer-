using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Recolours the environment skybox to a random HUE each time a scene loads whose skybox is the
/// "SimpleSkybox" (a Skybox/Procedural material). It shifts the Sky Tint and Ground colours to one
/// fresh random hue, keeping their saturation, value, and every other skybox setting — so the sky
/// comes out a different colour every scene load.
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

    private readonly System.Random rng = new System.Random();
    private Material instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("SkyboxHueRandomizer");
        go.AddComponent<SkyboxHueRandomizer>();
        DontDestroyOnLoad(go);
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // sceneLoaded may not fire for the scene already active when we bootstrap, so also run once here.
    void Start() => Recolor();
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Recolor();

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
        // (0..1 == the full 0..360 hue wheel.)
        ShiftHue(instance, "_SkyTint", (float)rng.NextDouble());
        ShiftHue(instance, "_GroundColor", (float)rng.NextDouble());

        RenderSettings.skybox = instance;
        DynamicGI.UpdateEnvironment();   // refresh ambient / reflections from the recoloured sky

        if (previous != null) Destroy(previous);   // free the prior instance (already copied from if current)
    }

    static void ShiftHue(Material mat, string prop, float hue)
    {
        Color c = mat.GetColor(prop);
        Color.RGBToHSV(c, out _, out float s, out float v);   // keep saturation + value
        Color shifted = Color.HSVToRGB(hue, s, v);
        shifted.a = c.a;                                       // leave alpha untouched
        mat.SetColor(prop, shifted);
    }
}
