using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// A HUB-world TV that broadcasts the players racing in the TrackScene. Each TV is bound to a TEAM and
/// cycles, every <see cref="cycleSeconds"/>, through that team's players who are CURRENTLY in the track,
/// showing a chase-camera view of each in turn. One racer → it just shows that one until a teammate
/// joins; a racer returning to the hub drops out of the rotation immediately.
///
/// A remote player's literal camera can't cross the network (that'd be video streaming), so this stands
/// a LOCAL chase camera up on the viewer's own machine and points it at that player's puppet — the track
/// is generated identically everywhere, so it's a faithful live view. The follow feel is the game's own
/// <see cref="CameraFollow"/> (reused with swivel off, so its Position/Pitch/Roll/Rotation Smooth Times
/// all apply; its FOV kicks / swivel / barrier audio are inert on a script-less puppet).
///
/// MULTIPLAYER ONLY — in single-player there are no remotes, so the screen is left exactly as authored.
///
/// MAKING THE FEED LOOK LIKE THE GAME. A camera built in code comes up with none of the settings a
/// racer's own camera is authored with, so three are matched explicitly: the TRACK's skybox (per-camera,
/// re-hued to this round's seed), post-processing (the game grades through URP's Default Volume Profile,
/// which a code-built camera has switched OFF), and SMAA.
///
/// LIGHTING is the interesting one, because it could not be done the same way. Skyboxes,
/// post-processing and AA are PER-CAMERA settings; lights and ambient are GLOBAL — one set of enabled
/// lights and one ambient probe per frame, shared by every camera in it. The Support Ship pilot can
/// simply swap the globals while they fly, because their whole screen IS the track. A TV viewer is
/// looking at the hub AND the screen in the same frame, so the two need different lighting at once.
///
/// The way through is that the two cameras do not render at the same INSTANT, only in the same frame.
/// URP raises beginCameraRendering / endCameraRendering around each camera, so this swaps the world to
/// the track's lighting for the length of our feed's render and hands it straight back — the hub
/// camera's own render gets its own pair with everything normal. Ambient rides along as a cached
/// probe (see CacheTrackAmbient), because deriving it per frame would be far too expensive.
///
/// That is what makes a BLACKOUT round read as one on screen: SetAreaLights restores each light's
/// RECORDED state, so a track whose Directional Light this round's seed switched off stays off in the
/// feed, while the hub around the TV keeps its own lighting.
///
/// Setup: put this on each TV, set <see cref="team"/> (1 or 2), drag the screen face's Renderer into
/// <see cref="screenRenderer"/> (set <see cref="screenMaterialIndex"/> if the screen is one material of
/// several), and assign <see cref="trackSkybox"/>. The camera + render texture are built at runtime.
/// </summary>
public class HubSpectatorTV : MonoBehaviour
{
    [Header("Which team this TV shows")]
    [Tooltip("1 or 2 — this TV cycles through the players on this team who are currently in the TrackScene.")]
    public int team = 1;

    [Header("Screen")]
    [Tooltip("The renderer whose material is the TV's screen face (the live view is drawn onto it).")]
    public Renderer screenRenderer;
    [Tooltip("Which material slot on that renderer is the screen. 0 if the screen is its own object/material.")]
    public int screenMaterialIndex = 0;
    [Tooltip("Render resolution shown on the screen — match your screen's aspect (16:9 by default).")]
    public int renderWidth = 640;
    public int renderHeight = 360;
    [Tooltip("Flip the picture vertically — tick if the feed shows upside-down (a render-texture-on-mesh " +
             "quirk that varies by GPU / UV layout). Live-tunable in Play mode.")]
    public bool flipVertical;
    [Tooltip("Mirror the picture horizontally. Live-tunable in Play mode.")]
    public bool flipHorizontal;
    [Range(0, 3)]
    [Tooltip("Rotate the picture in 90° steps — number of quarter-turns. Use this if the feed is sideways " +
             "(e.g. the car hugs an edge and it looks top-down): try 1, then 2/3 until it's upright. " +
             "Combine with the flips for any orientation. Live-tunable in Play mode.")]
    public int uvQuarterTurns;
    [Tooltip("Scale (zoom) the projected picture on the glass, per-axis. >1 zooms OUT (fits more of the " +
             "view — use this if the screen crops/magnifies the feed); <1 zooms IN. If zooming out just " +
             "shows borders, the render itself is too tight — widen Field Of View instead. Live-tunable.")]
    public Vector2 uvScale = Vector2.one;
    [Tooltip("Pan the projected picture across the glass (X = left/right, Y = up/down) to centre it. Live-tunable.")]
    public Vector2 uvOffset = Vector2.zero;

    [Header("Cycling")]
    [Tooltip("Seconds each racer is shown before the TV switches to the next teammate in the track.")]
    public float cycleSeconds = 5f;

    [Header("Chase camera (mirrors the in-game camera feel)")]
    [Tooltip("Copy the LOCAL player's live camera rig (offset, smooth times, look-ahead, roll-blend) so the " +
             "TV frames the car like the player sees it. Field Of View is NOT copied — it's your zoom knob " +
             "below. Uncheck to hand-place the camera with the Offset/smooth fields.")]
    public bool matchPlayerCamera = true;
    [Tooltip("Camera offset behind/above the car, in the car's local frame — same as CameraFollow.")]
    public Vector3 offset = new Vector3(0f, 2.5f, -7f);
    public float positionSmoothTime = 0.50f;   // yaw/orbit lag
    public float pitchSmoothTime = 0.25f;
    public float rollSmoothTime = 0.25f;
    public float rotationSmoothTime = 0.1f;     // aim lag when not swiveling
    public float lookAheadDistance = 5f;
    public float fieldOfView = 70f;
    [Tooltip("Spectator camera near clip plane.")]
    public float nearClip = 0.3f;
    [Tooltip("Spectator camera far clip plane — raise it so distant track / loops aren't culled from the feed.")]
    public float farClip = 20000f;

    [Header("Picture quality (matching a racer's own camera)")]
    [Tooltip("The TrackScene's skybox material (SimpleSkybox) — the SAME asset the TrackScene's lighting " +
             "settings use. The TV stands in the HUB, whose sky is Unity's plain default, but it is " +
             "LOOKING at the track, so the feed is given the track's sky per-camera and re-hued to this " +
             "round's seed. Leave blank and the screen shows the track under a flat grey hub sky.")]
    public Material trackSkybox;
    [Tooltip("Render post-processing on the feed, so the screen is graded like a racer's own view. The " +
             "game grades through URP's DEFAULT VOLUME PROFILE, which needs no Volume in any scene — a " +
             "code-built camera comes up with this FALSE, which is why the feed looked raw next to the " +
             "game it is showing.")]
    public bool enablePostProcessing = true;
    [Tooltip("Edge anti-aliasing for the feed. SMAA matches what the car cameras are authored with; a " +
             "code-built camera defaults to None and looks noticeably more jagged.")]
    public AntialiasingMode antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
    [Tooltip("Quality of the above. Ignored when Antialiasing is None or FXAA.")]
    public AntialiasingQuality antialiasingQuality = AntialiasingQuality.High;
    [Range(0.1f, 1f)]
    [Tooltip("Auto-framing: how much of the view the filmed car fills, derived from its bounds so EVERY car " +
             "model frames the same (fixes cars whose pivot/size differ). Higher = car appears larger/closer. " +
             "Re-tune this + Field Of View once for the look you want; it then applies to all cars.")]
    public float boundsFillFraction = 0.7f;

    // ---- runtime rig (built lazily the first time there's someone to show) ----
    private Camera specCam;
    private CameraFollow follow;
    private RenderTexture rt;
    private Material screenMaterial;      // the live-view material we swap onto the screen slot
    private Skybox camSkybox;             // per-camera sky override (the TRACK's, not the hub's)
    private Material ownedSky;            // our own recoloured copy, when we had to build one
    private bool warnedNoSky;
    private SphericalHarmonicsL2 trackAmbient;   // ambient the TRACK's sky produces
    private SphericalHarmonicsL2 parkedAmbient;  // the world's own, held during our render
    private bool hasTrackAmbient;
    private bool lightingPushed;
    private Material[] liveMats;          // screen materials with our slot swapped in
    private Material[] originalMats;      // the screen as authored, restored in standby
    private bool rigBuilt;
    private bool screenLive;
    private bool appliedFlipV, appliedFlipH;
    private int appliedTurns = -1;
    private Vector2 appliedScale = Vector2.one, appliedOffset;

    // ---- auto-framing cache (recomputed only when the filmed car changes) ----
    private GameObject framedCar;
    private Vector3 framedFocus;    // the car's centre of gravity, as a scaled local offset from its origin
    private float framedRadius;     // the car's bounding-sphere radius in world units

    // ---- cycling state ----
    private ulong currentClientId;
    private bool hasCurrent;
    private float cycleTimer;
    private readonly List<PlayerRegistry.RemotePlayer> teamRacers = new List<PlayerRegistry.RemotePlayer>();

    void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        PopWorldLighting();   // never leave the hub lit by the track
    }

    /// <summary>Lights the TRACK for our feed's render only, then hands the world straight back in
    /// <see cref="OnEndCameraRendering"/>.
    ///
    /// Lights and ambient are GLOBAL — one set per frame, shared by every camera in it — so the only
    /// way to light a TV differently from the hub around it is to change the world BETWEEN the two
    /// renders. URP fires this immediately before each camera is culled and drawn, which is exactly
    /// that gap. Nothing else sees the swapped state: the hub camera's own render gets its own
    /// begin/end pair with the world back to normal.
    ///
    /// This is what makes a BLACKOUT round read as one on screen: SetAreaLights restores each
    /// light's recorded state, so a track whose Directional Light the seed switched off stays off
    /// here too, while the hub around the TV keeps its own lighting.</summary>
    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam != specCam || !screenLive || specCam == null) return;

        MultiplayerWorld.PushTrackLighting();
        parkedAmbient = RenderSettings.ambientProbe;
        if (hasTrackAmbient) RenderSettings.ambientProbe = trackAmbient;
        lightingPushed = true;
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam != specCam) return;
        PopWorldLighting();
    }

    /// <summary>Puts the world's lighting back. Idempotent, and also called from Update as a
    /// self-heal: if the end callback were ever missed, the hub would otherwise be left lit by the
    /// track (or unlit entirely, on a blackout round) with nothing to correct it.</summary>
    void PopWorldLighting()
    {
        if (!lightingPushed) return;
        lightingPushed = false;

        RenderSettings.ambientProbe = parkedAmbient;
        MultiplayerWorld.PopTrackLighting();
    }

    void Update()
    {
        PopWorldLighting();   // self-heal (see above); a no-op in the normal case
        // Single-player (or before the MP session exists): leave the screen as authored.
        if (!MultiplayerWorld.IsMultiplayerGame) { GoStandby(); return; }

        // This team's players who are currently in the track (and have a puppet to film).
        teamRacers.Clear();
        foreach (var r in PlayerRegistry.Remotes)
            if (r.Team == team && r.InTrack && r.Car != null)
                teamRacers.Add(r);

        if (teamRacers.Count == 0) { GoStandby(); return; }   // nobody from this team racing

        EnsureRig();
        if (follow == null) return;   // no screen renderer assigned — can't display

        if (screenMaterial != null &&
            (flipVertical != appliedFlipV || flipHorizontal != appliedFlipH || uvQuarterTurns != appliedTurns
             || uvScale != appliedScale || uvOffset != appliedOffset))
            ApplyScreenTransform();   // flips / rotation / zoom / pan — all tunable live in Play mode

        if (follow != null) follow.baseFOV = follow.maxFOV = fieldOfView;   // Field Of View = live zoom knob
        if (specCam != null) { specCam.nearClipPlane = nearClip; specCam.farClipPlane = farClip; }

        // Keep filming the same racer (tracked by id, so the list reordering doesn't jump) until the
        // timer elapses; if they left the track, drop straight to another.
        int idx = hasCurrent ? teamRacers.FindIndex(r => r.ClientId == currentClientId) : -1;
        if (idx < 0)
        {
            idx = 0;
            currentClientId = teamRacers[0].ClientId;
            hasCurrent = true;
            cycleTimer = 0f;
        }

        cycleTimer += Time.deltaTime;
        if (teamRacers.Count > 1 && cycleTimer >= cycleSeconds)
        {
            idx = (idx + 1) % teamRacers.Count;   // advance to the next teammate in the track
            currentClientId = teamRacers[idx].ClientId;
            cycleTimer = 0f;
        }

        FrameCar(teamRacers[idx].Car);
        GoLive();
    }

    // Aims the spectator camera at the car's CENTRE OF GRAVITY and sets the follow distance from the
    // car's bounds, so every car model — whatever its pivot or size — frames identically. Both are a
    // fixed property of the model, so they're computed once per car and cached.
    void FrameCar(GameObject car)
    {
        if (car != framedCar)
        {
            framedCar = car;
            Vector3 s = car.transform.lossyScale;
            Bounds local = ComputeLocalBounds(car);

            // The car rig defines its centre of gravity as a child object named "CenterOfMass"
            // (CarController feeds it to Rigidbody.centerOfMass). That child survives the puppet strip,
            // so we aim at it directly. Fall back to the rigidbody CoM, then the bounds centre.
            Transform com = FindCenterOfMass(car);
            Vector3 centreLocal;
            if (com != null) centreLocal = car.transform.InverseTransformPoint(com.position);
            else { var rb = car.GetComponent<Rigidbody>(); centreLocal = rb != null ? rb.centerOfMass : local.center; }

            framedFocus = Vector3.Scale(centreLocal, s);                  // CoM → scaled local offset
            framedRadius = Mathf.Max(0.1f, local.extents.magnitude * Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
        }

        follow.target = car.transform;
        follow.focusLocalOffset = framedFocus;   // frame around the car's centre of gravity

        // Distance so the car's bounding sphere fills ~boundsFillFraction of the (vertical) view. Keeps
        // the tuned camera ANGLE (offset direction); only the distance is size-driven.
        float halfFov = Mathf.Max(1f, fieldOfView) * 0.5f * Mathf.Deg2Rad;
        float subtend = Mathf.Clamp(halfFov * Mathf.Clamp01(boundsFillFraction), 0.02f, 1.4f);
        float distance = framedRadius / Mathf.Sin(subtend);
        Vector3 dir = offset.sqrMagnitude > 1e-4f ? offset.normalized : new Vector3(0f, 0.35f, -1f).normalized;
        follow.offset = dir * distance;
    }

    // The "CenterOfMass" child the car rig defines its centre of gravity with (searches all descendants,
    // including inactive). Null if the model doesn't have one.
    static Transform FindCenterOfMass(GameObject car)
    {
        foreach (var t in car.GetComponentsInChildren<Transform>(true))
            if (t.name == "CenterOfMass") return t;
        return null;
    }

    // The car's tight bounding box in its own local space (rotation-INDEPENDENT — built from mesh bounds
    // transformed through the hierarchy to the root, so the car's live world pose doesn't matter). Skips
    // inactive objects (hidden flames/SD VFX), trails/particles and the floating name label.
    static Bounds ComputeLocalBounds(GameObject car)
    {
        Transform root = car.transform;
        bool has = false;
        Bounds local = default;

        void Add(Mesh mesh, Transform t)
        {
            Vector3 c = mesh.bounds.center, e = mesh.bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                Vector3 rootLocal = root.InverseTransformPoint(t.TransformPoint(corner));
                if (!has) { local = new Bounds(rootLocal, Vector3.zero); has = true; }
                else local.Encapsulate(rootLocal);
            }
        }

        foreach (var mf in car.GetComponentsInChildren<MeshFilter>())   // excludes inactive by default
        {
            if (mf.sharedMesh == null) continue;
            if (mf.GetComponent<TMPro.TextMeshPro>() != null) continue;   // skip the RIVAL/TEAMMATE label
            Add(mf.sharedMesh, mf.transform);
        }
        foreach (var smr in car.GetComponentsInChildren<SkinnedMeshRenderer>())
            if (smr.sharedMesh != null) Add(smr.sharedMesh, smr.transform);

        return has ? local : new Bounds(Vector3.zero, Vector3.one);
    }

    void EnsureRig()
    {
        if (rigBuilt) return;
        rigBuilt = true;

        if (screenRenderer == null)
        {
            Debug.LogWarning($"[HubSpectatorTV] Team {team} TV has no Screen Renderer assigned — nothing to display on.");
            return;
        }

        rt = new RenderTexture(Mathf.Max(64, renderWidth), Mathf.Max(64, renderHeight), 24)
        {
            name = $"SpectatorRT_Team{team}"
        };
        rt.Create();

        var camGo = new GameObject($"SpectatorCam_Team{team}");
        camGo.transform.SetParent(transform, false);   // CameraFollow drives world pose each frame — parent is just for tidy lifecycle
        specCam = camGo.AddComponent<Camera>();
        specCam.targetTexture = rt;
        specCam.clearFlags = CameraClearFlags.Skybox;
        specCam.fieldOfView = fieldOfView;
        specCam.nearClipPlane = nearClip;
        specCam.farClipPlane = farClip;
        specCam.enabled = false;   // rendered only while live
        // NO AudioListener — only the local player owns the one active listener.

        // Match a racer's own camera: they are authored with post-processing on and SMAA, while a
        // camera built in code comes up with neither — which is why the feed resolved edges more
        // jaggedly than the game it is showing.
        var urp = specCam.GetUniversalAdditionalCameraData();
        if (urp != null)
        {
            urp.renderPostProcessing = enablePostProcessing;
            urp.antialiasing = antialiasing;
            urp.antialiasingQuality = antialiasingQuality;
            urp.renderShadows = true;
        }

        // PER-CAMERA SKYBOX. RenderSettings.skybox follows the ACTIVE scene, which for everyone
        // watching a TV is the hub — and the hub's sky is Unity's built-in default while the track's is
        // the procedural SimpleSkybox. Without this the feed shows the track under a plain grey sky
        // while the racer on screen is under the real one. A Skybox COMPONENT overrides RenderSettings
        // for this camera alone, so the hub around the TV is untouched.
        camSkybox = camGo.AddComponent<Skybox>();

        follow = camGo.AddComponent<CameraFollow>();
        follow.enableSwivel = false;               // spectator: no look-around (and the puppet has no CarController anyway)
        follow.offset = offset;
        follow.positionSmoothTime = positionSmoothTime;
        follow.pitchSmoothTime = pitchSmoothTime;
        follow.rollSmoothTime = rollSmoothTime;
        follow.rotationSmoothTime = rotationSmoothTime;
        follow.lookAheadDistance = lookAheadDistance;
        follow.baseFOV = follow.maxFOV = fieldOfView;   // puppet reports zero speed → constant FOV
        if (matchPlayerCamera) CopyPlayerCameraSettings();   // frame it exactly like the driver's own view

        // Build the live-view material and the swapped material array; keep the authored array to restore.
        Shader sh = Shader.Find("Generacer/HubScreen");   // supports UV rotation (+ flips via tiling)
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Texture");
        screenMaterial = new Material(sh) { name = $"SpectatorScreen_Team{team}", mainTexture = rt };
        ApplyScreenTransform();

        originalMats = screenRenderer.sharedMaterials;
        liveMats = (Material[])originalMats.Clone();
        if (screenMaterialIndex >= 0 && screenMaterialIndex < liveMats.Length)
            liveMats[screenMaterialIndex] = screenMaterial;
        else
            Debug.LogWarning($"[HubSpectatorTV] Team {team} TV: Screen Material Index {screenMaterialIndex} is out of range " +
                             $"(renderer has {liveMats.Length} material slots).");
    }

    // Orients the picture: horizontal/vertical flips via the material's tiling (negated scale, offset
    // back into 0-1), and 90° rotation steps via the HubScreen shader's _UVRotation. No extra render pass.
    void ApplyScreenTransform()
    {
        if (screenMaterial == null) return;
        screenMaterial.mainTextureScale = new Vector2(flipHorizontal ? -1f : 1f, flipVertical ? -1f : 1f);
        screenMaterial.mainTextureOffset = new Vector2(flipHorizontal ? 1f : 0f, flipVertical ? 1f : 0f);
        if (screenMaterial.HasProperty("_UVRotation"))
            screenMaterial.SetFloat("_UVRotation", (((uvQuarterTurns % 4) + 4) % 4) * 90f);
        if (screenMaterial.HasProperty("_UVScale"))
            screenMaterial.SetVector("_UVScale", new Vector4(uvScale.x, uvScale.y, 0f, 0f));
        if (screenMaterial.HasProperty("_UVOffset"))
            screenMaterial.SetVector("_UVOffset", new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
        appliedFlipV = flipVertical;
        appliedFlipH = flipHorizontal;
        appliedTurns = uvQuarterTurns;
        appliedScale = uvScale;
        appliedOffset = uvOffset;
    }

    // Copies the local player's live camera rig onto the spectator follow so the TV frames each car
    // exactly like its driver sees it. Camera.main is the player's forward camera (our spectator cams are
    // untagged, so they're never Camera.main); if it isn't up yet we keep the Inspector values.
    void CopyPlayerCameraSettings()
    {
        var main = Camera.main;
        var src = main != null ? main.GetComponent<CameraFollow>() : null;
        if (src == null) return;
        // Offset is NOT copied — FrameCar drives it from the filmed car's bounds each frame.
        follow.positionSmoothTime = src.positionSmoothTime;
        follow.pitchSmoothTime = src.pitchSmoothTime;
        follow.rollSmoothTime = src.rollSmoothTime;
        follow.rotationSmoothTime = src.rotationSmoothTime;
        follow.lookAheadDistance = src.lookAheadDistance;
        follow.rollBlendStart = src.rollBlendStart;
        follow.rollBlendFull = src.rollBlendFull;
        // FOV is intentionally NOT copied — `fieldOfView` is the manual zoom knob, applied live in Update.
    }

    void GoLive()
    {
        if (screenLive) return;
        screenLive = true;
        ApplyTrackSkybox();   // re-resolved per go-live: the round (and its hues) may have turned over
        if (specCam != null) specCam.enabled = true;
        if (screenRenderer != null && liveMats != null) screenRenderer.sharedMaterials = liveMats;
    }

    void GoStandby()
    {
        if (screenLive)
        {
            screenLive = false;
            if (specCam != null) specCam.enabled = false;
            if (follow != null) follow.target = null;
            if (screenRenderer != null && originalMats != null) screenRenderer.sharedMaterials = originalMats;
        }
        hasCurrent = false;
        cycleTimer = 0f;
    }

    /// <summary>Points the feed at the TRACK's sky with this round's hues. Shares one resolver with the
    /// Support Ship pilot's camera, which needs exactly the same thing for exactly the same reason —
    /// and which is where the round-staleness trap is documented (see SkyboxHueRandomizer.CurrentSky).
    ///
    /// ⚠️ This is the SKY ONLY. Lighting is global state and cannot be given per camera: the feed is
    /// lit by whatever the hub is lit by, ambient included. See the class comment.</summary>
    void ApplyTrackSkybox()
    {
        if (camSkybox == null) return;

        Material sky = SkyboxHueRandomizer.ResolveRoundSky(trackSkybox, ref ownedSky);
        if (sky != null) { camSkybox.material = sky; CacheTrackAmbient(sky); }
        else if (trackSkybox == null && !warnedNoSky)
        {
            warnedNoSky = true;
            Debug.LogWarning($"[HubSpectatorTV] Team {team} TV has no Track Skybox assigned — the feed " +
                             "will show the track under the hub's plain default sky. Assign the same " +
                             "SimpleSkybox material the TrackScene's lighting settings use.");
        }
    }

    /// <summary>Works out what AMBIENT light the track's sky produces, and remembers it.
    ///
    /// Both scenes use Ambient Mode = SKYBOX, so ambient is generated from RenderSettings.skybox — a
    /// global that follows the ACTIVE scene, which for everyone watching a TV is the hub. Without this
    /// the feed shows a night track lit by the hub's bright default sky, exactly as the Support Ship
    /// pilot's view did before ApplyTrackAmbient fixed it there.
    ///
    /// Computed ONCE and cached as a probe, deliberately. Deriving ambient means assigning the sky and
    /// calling DynamicGI.UpdateEnvironment(), which is far too heavy to do twice a frame per TV; but
    /// RenderSettings.ambientProbe is directly assignable, so paying that cost once per go-live buys a
    /// per-frame swap that is a struct copy. The world's own sky is restored immediately either way.
    ///
    /// Once per go-live is enough: a TV only goes live when its team has someone racing, and it goes
    /// back to standby at round end — so a new round always re-enters through here with the new sky.</summary>
    void CacheTrackAmbient(Material sky)
    {
        Material previous = RenderSettings.skybox;
        if (previous == sky)
        {
            // Already the world's sky (a Support Ship pilot on this machine has swapped it, or we are
            // somehow in the track) — the probe standing in RenderSettings is the one we want.
            trackAmbient = RenderSettings.ambientProbe;
            hasTrackAmbient = true;
            return;
        }

        RenderSettings.skybox = sky;
        DynamicGI.UpdateEnvironment();
        trackAmbient = RenderSettings.ambientProbe;
        hasTrackAmbient = true;

        RenderSettings.skybox = previous;
        DynamicGI.UpdateEnvironment();   // hand the hub its own sky (and ambient) straight back
    }

    void OnDestroy()
    {
        if (screenRenderer != null && originalMats != null) screenRenderer.sharedMaterials = originalMats;
        if (rt != null) { rt.Release(); Destroy(rt); }
        if (screenMaterial != null) Destroy(screenMaterial);
        // We built this copy ourselves (SkyboxHueRandomizer owns the ones IT makes), so we free it.
        if (ownedSky != null) Destroy(ownedSky);
    }
}
