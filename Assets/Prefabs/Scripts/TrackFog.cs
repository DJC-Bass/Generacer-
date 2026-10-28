using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Height fog over the TrackScene: the far course fades into the sky behind it, thickest low down and
/// thinning with altitude. The hub gets none. Tuned on Resources/TrackFogSettings.
///
/// Not every round: each TrackScene load rolls Fog Chance for a FOG ROUND, the way
/// RoundDirectionalLightToggle rolls a blackout round — from the server's round seed in multiplayer, so
/// every player, Spectator TV and pilot agrees. The two are independent, so a fog round can be at night.
///
/// Distant scenery fades into the SKY IN ITS OWN DIRECTION, not into one fog colour. Unity's built-in fog
/// (the first version of this) can only fade to a single colour, and the track's sky is green below the
/// horizon, a white band at it and paler above — whatever colour was picked, far track stood out against
/// the rest as solid shapes. So this is a full-screen pass instead (TrackHeightFog.shader) that blends
/// each pixel toward a cube render of this round's sky, sampled in that pixel's direction, by the fog its
/// ray has gathered on the way there. One pass over the screen after the opaque scenery: about the cost of
/// a single post effect, nothing like a raymarched volume.
///
/// The same pass hazes the sky's own GROUND: under the horizon it fades up into the sky's horizon colour
/// (Ground Haze degrees deep) instead of meeting it in a hard line, and far track fades into that hazed
/// sky. It belongs here, not in the sky shader: the track's ambient light is taken from the sky material,
/// and a hazed ground there lit every side and underside ~40% brighter.
///
/// The pass is added per CAMERA, just before each draws, for any camera looking at the track — the
/// racers', the hub Spectator TVs', a Support Ship pilot's. "Which scene is active" can't decide it: in a
/// multiplayer game the hub and the track are loaded side by side, and the TVs and the pilot look at the
/// track from the hub. It goes in before the transparent pass, so particles, trails and the R/T tags are
/// drawn over it unfogged — a full-screen pass only knows the depth of what's solid, and fogging a near
/// spark as if it were the track behind it would be worse.
/// </summary>
public class TrackFog : MonoBehaviour
{
    TrackFogSettings settings;
    Material material;
    HeightFogPass pass;
    bool fogRound = true;   // this round rolled fog — and always, outside a game-loop run
    static readonly System.Random rng = new System.Random();   // single-player rolls; leaves Unity's Random alone

    // This round's sky in every direction, and which sky (and round) it was drawn from.
    RenderTexture skyCube;
    bool haveSky;
    Material drawnSky;
    int drawnSeed;
    Material ownedSky;   // a private copy of this round's sky, until a live one exists
    bool hazeable;           // that sky has a horizon and ground colour to haze between
    Color horizon, ground;   // ...and here they are, as it draws them

    const int SkyCubeSize = 128;
    /// <summary>Which mip of the sky cube the fog samples: one level down, so clouds and the sun disc
    /// soften in the haze instead of being printed sharp onto distant track.</summary>
    const float SkyMip = 1f;

    static readonly int SkyId = Shader.PropertyToID("_TrackFogSky");
    static readonly int DensityId = Shader.PropertyToID("_TrackFogDensity");
    static readonly int StartId = Shader.PropertyToID("_TrackFogStart");
    static readonly int SkyMipId = Shader.PropertyToID("_TrackFogSkyMip");
    static readonly int HazeId = Shader.PropertyToID("_TrackFogHaze");
    static readonly int GroundId = Shader.PropertyToID("_TrackFogGround");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        var settings = Resources.Load<TrackFogSettings>("TrackFogSettings");
        if (settings == null || settings.heightFogShader == null) return;   // no settings asset — no fog
        var go = new GameObject("TrackFog");
        go.AddComponent<TrackFog>().settings = settings;
        DontDestroyOnLoad(go);
    }

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // The TrackScene loads fresh every round (additively in multiplayer, after the round seed is set).
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (settings != null && scene.name == TrackSceneName()) RollFogRound();
    }

    /// <summary>Is this a fog round? Like the blackout roll, only during a game-loop run: with no
    /// GameLoopManager (the TrackScene opened on its own) the fog stays on.</summary>
    void RollFogRound()
    {
        var glm = GameLoopManager.Instance;
        if (glm == null) { fogRound = true; return; }

        fogRound = (MultiplayerWorld.IsMultiplayerGame
            ? MultiplayerWorld.DeriveRandom("fog").NextDouble()
            : rng.NextDouble()) < settings.fogChance;
        Debug.Log($"[TrackFog] Round {glm.RoundNumber}: fog {(fogRound ? "ON (fog round)" : "off")}.");
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
        if (skyCube != null) skyCube.Release();
        if (ownedSky != null) Destroy(ownedSky);
    }

    /// <summary>Keeps the sky cube in step with this round's sky, and the fog's settings with the asset.
    /// Out here rather than in the camera callback: drawing the sky takes a camera of its own, and one
    /// camera can't render inside another's.</summary>
    void LateUpdate()
    {
        if (settings == null || !settings.enableFog || !fogRound || !TrackSceneLoaded()) return;

        if (material == null)
        {
            material = new Material(settings.heightFogShader) { hideFlags = HideFlags.HideAndDontSave };
            pass = new HeightFogPass(material);
        }

        int seed = MultiplayerWorld.IsMultiplayerGame ? MultiplayerWorld.CurrentRoundSeed : 0;
        Material live = SkyboxHueRandomizer.CurrentSky;   // the sky racers are under, when it's this round's
        if (live != null)
        {
            if (live != drawnSky)
            {
                DrawSky(live, seed);
                if (ownedSky != null) { Destroy(ownedSky); ownedSky = null; }
            }
        }
        else if (drawnSky == null || drawnSeed != seed)
        {
            // Nobody on this machine has raced this round yet (a TV or pilot looking in from the hub):
            // build the same sky from the round's seed.
            if (ownedSky != null) Destroy(ownedSky);
            ownedSky = SkyboxHueRandomizer.BuildRoundSky(settings.trackSkybox);
            DrawSky(ownedSky != null ? ownedSky : settings.trackSkybox, seed);
        }

        // Density at the reference altitude for 95% at Full Fog Distance on a level ray there, and the
        // falloff that halves it every Half Height.
        float reach = Mathf.Max(1f, settings.fullFogDistance - settings.startDistance);
        material.SetVector(DensityId, new Vector4(-Mathf.Log(0.05f) / reach, Mathf.Log(2f) / settings.halfHeight,
                                                  settings.referenceAltitude, settings.maxOpacity));
        material.SetFloat(StartId, settings.startDistance);
        material.SetFloat(SkyMipId, SkyMip);
        material.SetTexture(SkyId, skyCube);
        float hazeDepth = hazeable ? Mathf.Sin(settings.groundHaze * Mathf.Deg2Rad) : 0f;
        material.SetVector(HazeId, new Vector4(horizon.r, horizon.g, horizon.b, hazeDepth));
        material.SetVector(GroundId, ground);
    }

    void DrawSky(Material sky, int seed)
    {
        drawnSky = sky;   // even on failure, so a sky that can't be drawn isn't retried every frame
        drawnSeed = seed;

        if (skyCube == null)
        {
            skyCube = new RenderTexture(SkyCubeSize, SkyCubeSize, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
                name = "TrackFogSky",
            };
            skyCube.Create();
        }

        // A procedural sky's sun — and whether it's night at all — comes from the lights that are on: the
        // track's, as the racers see it, blackout round included.
        MultiplayerWorld.PushTrackLighting();
        haveSky = SkyboxHueRandomizer.TryRenderSkyCube(sky, skyCube);
        float night = SkyNight(sky);
        MultiplayerWorld.PopTrackLighting();

        // The horizon and ground colours the sky draws, for the ground haze, by ProceduralSkyClouds' own
        // sums: the colour taken toward night (darkened and tinted), times the day or night exposure.
        // Material colours are stored gamma; the shader gets them linear.
        hazeable = sky.HasProperty("_HorizonColor") && sky.HasProperty("_GroundColor");
        if (!hazeable) return;
        Color nightShade = Color.Lerp(Color.white, ColorOf(sky, "_NightTint", Color.white) * FloatOf(sky, "_NightDarkness", 1f), night);
        float exposure = Mathf.Lerp(FloatOf(sky, "_Exposure", 1f), FloatOf(sky, "_NightExposure", 1f), night);
        horizon = sky.GetColor("_HorizonColor").linear * nightShade * exposure;
        ground = sky.GetColor("_GroundColor").linear * nightShade * exposure;
    }

    /// <summary>How far into night <paramref name="sky"/> draws under the lights that are on now, by
    /// ProceduralSkyClouds' own rule: its Night Blend when set to Manual, otherwise from the main
    /// directional light's brightness — no light at all (a blackout round) is full night.</summary>
    static float SkyNight(Material sky)
    {
        if (FloatOf(sky, "_NightSource", 0f) > 0.5f) return Mathf.Clamp01(FloatOf(sky, "_NightBlend", 0f));
        float t = Mathf.Clamp01(MainLightLuminance() / Mathf.Max(FloatOf(sky, "_NightLightThreshold", 0.05f), 1e-4f));
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>The brightness of the light URP makes the main light: the scene's Sun if it's on, else the
    /// brightest directional light that's on.</summary>
    static float MainLightLuminance()
    {
        Light main = null;
        foreach (var light in FindObjectsByType<Light>())
        {
            if (light.type != LightType.Directional || !light.isActiveAndEnabled) continue;
            if (light == RenderSettings.sun) { main = light; break; }
            if (main == null || light.intensity > main.intensity) main = light;
        }
        if (main == null) return 0f;
        Color c = main.color.linear * main.intensity;
        if (main.useColorTemperature) c *= Mathf.CorrelatedColorTemperatureToRGB(main.colorTemperature);
        return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }

    static float FloatOf(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;
    static Color ColorOf(Material m, string name, Color fallback) => m.HasProperty(name) ? m.GetColor(name).linear : fallback;

    void OnBeginCamera(ScriptableRenderContext context, Camera cam)
    {
        if (!haveSky || pass == null || !settings.enableFog || !fogRound || !ShowsTrack(cam)) return;
        var renderer = cam.GetUniversalAdditionalCameraData().scriptableRenderer;
        if (renderer != null) renderer.EnqueuePass(pass);
    }

    /// <summary>Is this camera looking at the track? Only while the TrackScene is loaded — and in a
    /// multiplayer game, where the hub sits 30 km below the track, only up in the track's area. Never the
    /// hidden camera that draws the sky itself.</summary>
    static bool ShowsTrack(Camera cam)
    {
        if (cam.cameraType != CameraType.Game) return false;   // not the editor's Scene view or previews
        if ((cam.gameObject.hideFlags & HideFlags.DontSave) != 0) return false;
        if (!TrackSceneLoaded()) return false;
        return !MultiplayerWorld.IsMultiplayerGame
            || cam.transform.position.y > MultiplayerWorld.HubAreaOffset.y * 0.5f;
    }

    static bool TrackSceneLoaded() => SceneManager.GetSceneByName(TrackSceneName()).isLoaded;

    static string TrackSceneName()
    {
        var glm = GameLoopManager.Instance;
        return glm != null ? glm.trackSceneName : "TrackScene";
    }

    /// <summary>Copies the camera's colour aside, then draws it back through the fog shader, which reads
    /// the copy and the depth texture.</summary>
    class HeightFogPass : ScriptableRenderPass
    {
        readonly Material material;

        class PassData
        {
            public TextureHandle source;
            public Material material;
        }

        public HeightFogPass(Material material)
        {
            this.material = material;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
            profilingSampler = new ProfilingSampler("Track Height Fog");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;

            var desc = renderGraph.GetTextureDesc(resources.cameraColor);
            desc.name = "_TrackHeightFogSource";
            desc.clearBuffer = false;
            TextureHandle copy = renderGraph.CreateTexture(desc);
            renderGraph.AddBlitPass(resources.activeColorTexture, copy, Vector2.one, Vector2.zero,
                                    passName: "Track Height Fog (copy)");

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Track Height Fog", out var data, profilingSampler))
            {
                data.source = copy;
                data.material = material;
                builder.UseTexture(copy, AccessFlags.Read);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                    Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, 0));
            }
        }
    }
}
