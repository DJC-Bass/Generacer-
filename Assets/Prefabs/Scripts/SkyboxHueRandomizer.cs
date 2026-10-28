using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
    const string ZenithProp = "_ZenithColor";
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

    [Tooltip("How far the ZENITH hue sits from the sky hue (0..1 = the full colour wheel; 0.03 ≈ 11°). " +
             "Smaller than the horizon offset on purpose: the zenith is the SAME air seen deeper, not a " +
             "different layer, so it should read as the sky going darker rather than as a second " +
             "colour. Its darkness is not set here — the hue shift keeps whatever value the material " +
             "authored, so the zenith stays as far below the sky tint as it was painted.")]
    public float zenithHueOffset = 0.03f;

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
    private int builtSeed;      // the round seed `instance` was derived from (see CurrentSky)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("SkyboxHueRandomizer");
        go.AddComponent<SkyboxHueRandomizer>();
        DontDestroyOnLoad(go);
    }

    public static SkyboxHueRandomizer Instance { get; private set; }

    /// <summary>The live recoloured sky FOR THIS ROUND, or null if we don't have one. A camera that
    /// needs the TRACK's sky while the HUB is the active scene (the Support Ship pilot) reads this
    /// rather than RenderSettings, which always reflects the active scene.
    ///
    /// ⚠️ The round check is load-bearing, and its absence was a real bug (fixed 2026-08-24): the
    /// pilot's sky did not match the racer's. `instance` is only ever REPLACED by a successful
    /// recolour, and a recolour only succeeds on a SimpleSkybox — so returning to the hub, whose sky is
    /// Unity's Default-Skybox, leaves the LAST ROUND'S track sky sitting in `instance` indefinitely. A
    /// pilot who raced in round 1 and then flew from the hub in round 2 was handed round 1's colours,
    /// while the racers saw round 2's. Stamping the seed it was built for and refusing to serve a
    /// mismatch sends the caller to rebuild from the base asset, which lands on this round's hues
    /// because they derive from the shared seed.</summary>
    public static Material CurrentSky =>
        Instance != null && Instance.instance != null && Instance.builtSeed == RoundSeedNow
            ? Instance.instance : null;

    /// <summary>The seed the CURRENT sky should be derived from. 0 outside a seeded round, which is
    /// also what single-player uses, so the two agree without a special case.</summary>
    static int RoundSeedNow =>
        MultiplayerWorld.IsMultiplayerGame ? MultiplayerWorld.CurrentRoundSeed : 0;

    /// <summary>Builds a recoloured COPY of any base skybox with this round's hues — for a camera that
    /// needs a sky the active scene isn't using. The caller owns the returned material and must destroy
    /// it. Null if the randomizer isn't up yet or the material isn't a SimpleSkybox.</summary>
    public static Material BuildRoundSky(Material baseSky) =>
        Instance != null ? Instance.BuildRecoloured(baseSky) : null;

    /// <summary>THIS ROUND'S track sky, for any camera that has to show a scene it isn't standing in —
    /// the Support Ship pilot's chase cam and the hub Spectator TVs. Both need the identical material
    /// the racers are under, and both are looking at it from the hub.
    ///
    /// Prefers the LIVE instance when it belongs to this round (then every camera is literally sharing
    /// one material), and otherwise builds a private copy from <paramref name="baseSky"/> — which lands
    /// on the same colours anyway, because the hues derive from the shared round seed.
    ///
    /// <paramref name="owned"/> is the caller's cache for that private copy: passed by ref, replaced
    /// (and the previous one destroyed) whenever a fresh build is needed, and the caller must destroy
    /// whatever is left in it. Rebuilding rather than caching forever is deliberate — the seed changes
    /// between rounds, and going live is exactly when a stale sky would be noticed.</summary>
    public static Material ResolveRoundSky(Material baseSky, ref Material owned)
    {
        Material live = CurrentSky;
        if (live != null) return live;

        if (baseSky == null) return null;

        if (owned != null) Destroy(owned);
        owned = BuildRoundSky(baseSky);
        return owned != null ? owned : baseSky;
    }

    void Awake() => Instance = this;

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
        Material recoloured = BuildRecoloured(RenderSettings.skybox);
        if (recoloured == null) return;

        Material previous = instance;
        instance = recoloured;
        builtSeed = RoundSeedNow;   // so CurrentSky can tell this round's sky from a leftover
        RenderSettings.skybox = instance;
        DynamicGI.UpdateEnvironment();   // refresh ambient / reflections from the recoloured sky

        if (previous != null) Destroy(previous);   // free the prior instance (already copied from if current)
    }

    /// <summary>Returns a recoloured COPY of <paramref name="sky"/>, or null if it isn't a SimpleSkybox.
    /// Split out from <see cref="Recolor"/> so a camera showing a scene it isn't standing in — the
    /// Support Ship pilot, framing the track from the hub — can get that scene's sky with THIS round's
    /// hues instead of whatever the active scene happens to be using.</summary>
    public Material BuildRecoloured(Material sky)
    {
        if (sky == null) return null;

        // Only our target SimpleSkybox (a Skybox/Procedural material with Sky Tint + Ground).
        if (!sky.name.StartsWith(TargetName)) return null;
        if (!sky.HasProperty("_SkyTint") || !sky.HasProperty("_GroundColor")) return null;

        // Copy the SOURCE (the shared asset, or an existing instance) so the asset on disk is never
        // modified.
        var instance = new Material(sky) { name = TargetName + " (Random)" };

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

        // Horizon, zenith and ground-texture hues are DERIVED, not rolled: each sits a fixed step
        // around the wheel from the layer it belongs to, so the sky gradient and the ground always read
        // as single coherent surfaces instead of occasionally landing on clashing pairs.
        //
        // ⚠️ Deriving rather than rolling is also what keeps this safe to extend. The independent draws
        // above must stay the same NUMBER of draws forever: a fifth roll slipped in here would shift
        // every hue after it, so a build with one more roll than another would disagree about a sky
        // both machines derived from the same seed. A derived hue costs zero draws.
        float hHorizon = Mathf.Repeat(hSky + horizonHueOffset, 1f);
        float hZenith = Mathf.Repeat(hSky + zenithHueOffset, 1f);
        float hGroundTexture = Mathf.Repeat(hGround + groundTextureHueOffset, 1f);

        ShiftHue(instance, "_SkyTint", hSky);
        ShiftHue(instance, "_GroundColor", hGround);

        // Only on the ProceduralSkyClouds shader — the built-in procedural material has none of these.
        if (instance.HasProperty(HorizonProp)) ShiftHue(instance, HorizonProp, hHorizon);
        if (instance.HasProperty(ZenithProp)) ShiftHue(instance, ZenithProp, hZenith);
        if (instance.HasProperty(CloudProp)) ShiftHue(instance, CloudProp, hCloud, cloudMinSaturation);
        if (instance.HasProperty(GroundCloudProp)) ShiftHue(instance, GroundCloudProp, hGroundTexture);
        if (instance.HasProperty(NightTintProp))
            ShiftHue(instance, NightTintProp, hNight, maxSaturation: nightTintMaxSaturation);

        return instance;
    }

    // -------------------------------------------------------
    //  A sky's ambient light, measured without touching the world
    // -------------------------------------------------------

    const int AmbientFaceSize = 16;

    static readonly Quaternion[] CubeFaces =
    {
        Quaternion.LookRotation(Vector3.right, Vector3.up),
        Quaternion.LookRotation(Vector3.left, Vector3.up),
        Quaternion.LookRotation(Vector3.forward, Vector3.up),
        Quaternion.LookRotation(Vector3.back, Vector3.up),
        Quaternion.LookRotation(Vector3.up, Vector3.back),
        Quaternion.LookRotation(Vector3.down, Vector3.forward),
    };

    /// <summary>The AMBIENT light <paramref name="sky"/> gives off, worked out without touching the
    /// world's lighting. For the cameras that show the track from the hub (the Spectator TVs, the Support
    /// Ship pilot): they swap the result into RenderSettings.ambientProbe for their own view only.
    ///
    /// ⚠️ Do NOT get it by setting RenderSettings.skybox and calling DynamicGI.UpdateEnvironment(). That
    /// REBUILDS the world's ambient and reflections from whatever sky is assigned, and putting the hub's
    /// sky back afterwards rebuilds the hub's from ITS sky — the purple-blue space HDRI — which the hub is
    /// never otherwise lit by (it runs on Unity's default, neutral environment). That is how a TV going
    /// live, or a pilot handing back the controls, tinted the whole hub purple.
    ///
    /// Instead a private camera draws the sky alone into the six faces of a small cube, and every texel
    /// is added to a spherical harmonic as light arriving from its direction, weighted by the solid angle
    /// it covers, then scaled so a plain white sky comes out at exactly 1 — Unity's own convention for
    /// the ambient probe. A procedural sky takes its sun from the lights enabled while this runs, so a
    /// caller wanting the track's sky lights the track first.</summary>
    public static bool TryMeasureAmbient(Material sky, out SphericalHarmonicsL2 probe)
    {
        probe = default;
        const int n = AmbientFaceSize;
        var light = new SphericalHarmonicsL2();
        var white = new SphericalHarmonicsL2();
        bool drawn = RenderSkyFaces(sky, CubeFaces, n, (face, pixels) =>
        {
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                float r2 = 1f + u * u + v * v;
                float solidAngle = 4f / (n * n * r2 * Mathf.Sqrt(r2));
                Vector3 dir = face * new Vector3(u, v, 1f).normalized;
                light.AddDirectionalLight(dir, pixels[y * n + x], solidAngle);
                white.AddDirectionalLight(dir, Color.white, solidAngle);
            }
        });
        if (!drawn) return false;

        var unit = new Color[1];
        white.Evaluate(new[] { Vector3.up }, unit);
        if (unit[0].r <= 1e-6f) return false;
        probe = light * (1f / unit[0].r);
        return true;
    }

    /// <summary>Draws <paramref name="sky"/> into <paramref name="cube"/>, a cube RenderTexture: the sky in
    /// every direction, linear HDR, exactly as the sky shader draws it before post-processing. TrackFog
    /// fades distant scenery into it, each pixel into the sky in its own direction — the track's sky is
    /// green below the horizon, a white band at it and paler above, and no single fog colour matches all
    /// of that. Like <see cref="TryMeasureAmbient"/>, a procedural sky takes its sun from the lights
    /// enabled while this runs.</summary>
    public static bool TryRenderSkyCube(Material sky, RenderTexture cube)
    {
        if (sky == null || cube == null) return false;
        var go = CreateSkyProbe(sky, out Camera cam);
        try
        {
            return cam.RenderToCubemap(cube);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[SkyboxHueRandomizer] Couldn't draw the sky '{sky.name}' into a cube: {e.Message}");
            return false;
        }
        finally
        {
            Destroy(go);
        }
    }

    /// <summary>Where the sky is measured from: 50 km up, above both play areas, so a camera that sees ten
    /// metres has nothing to see but the sky.</summary>
    static readonly Vector3 SkyProbePosition = new Vector3(0f, 50000f, 0f);

    /// <summary>The private camera the sky is measured with: hidden, switched off (it only renders when
    /// asked), 90° square, HDR and ungraded — the sky's raw light, not a picture of it.
    ///
    /// ⚠️ It must SEE the scene's lights. The track's sky decides day or night from URP's main light — no
    /// main light, night — and a camera culls lights by layer just as it culls renderers. This camera used
    /// to have an empty culling mask to show "the sky alone", so it measured the track's sky as night, at
    /// about 1% of its real brightness — the hub TVs' and the pilot's ambient light with it. It sees every
    /// layer now, and is kept clear of the scenery by distance instead.</summary>
    static GameObject CreateSkyProbe(Material sky, out Camera cam)
    {
        var go = new GameObject("SkyAmbientProbe") { hideFlags = HideFlags.HideAndDontSave };
        go.transform.position = SkyProbePosition;
        cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.cullingMask = ~0;                      // every layer, for the lights (see above)
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = 90f;
        cam.aspect = 1f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 10f;
        cam.allowHDR = true;
        cam.allowMSAA = false;
        go.AddComponent<Skybox>().material = sky;
        var urp = cam.GetUniversalAdditionalCameraData();
        if (urp != null)
        {
            urp.renderPostProcessing = false;
            urp.antialiasing = AntialiasingMode.None;
            urp.renderShadows = false;
        }
        return go;
    }

    /// <summary>Draws <paramref name="sky"/> alone, from the private probe camera, into a small square for
    /// each of <paramref name="faces"/> (90° views, <paramref name="n"/> pixels square), and hands
    /// <paramref name="perFace"/> each one's pixels — linear HDR, bottom row first. Nothing in the world is
    /// touched.</summary>
    static bool RenderSkyFaces(Material sky, Quaternion[] faces, int n, System.Action<Quaternion, Color[]> perFace)
    {
        if (sky == null) return false;

        var go = CreateSkyProbe(sky, out Camera cam);
        RenderTexture target = null;
        Texture2D readback = null;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            target = RenderTexture.GetTemporary(n, n, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            readback = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true);
            var request = new RenderPipeline.StandardRequest { destination = target };
            bool viaRequest = RenderPipeline.SupportsRenderRequest(cam, request);
            if (!viaRequest) cam.targetTexture = target;

            foreach (Quaternion face in faces)
            {
                go.transform.rotation = face;
                if (viaRequest) RenderPipeline.SubmitRenderRequest(cam, request);
                else cam.Render();

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
                perFace(face, readback.GetPixels());
            }
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[SkyboxHueRandomizer] Couldn't measure the sky '{sky.name}': {e.Message}");
            return false;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (readback != null) Destroy(readback);
            Destroy(go);
        }
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
