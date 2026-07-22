using System.Collections.Generic;
using UnityEngine;

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
/// Setup: put this on each TV, set <see cref="team"/> (1 or 2) and drag the screen face's Renderer into
/// <see cref="screenRenderer"/> (set <see cref="screenMaterialIndex"/> if the screen is one material of
/// several). The camera + render texture are built at runtime; nothing else to wire.
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

    void Update()
    {
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

    void OnDestroy()
    {
        if (screenRenderer != null && originalMats != null) screenRenderer.sharedMaterials = originalMats;
        if (rt != null) { rt.Release(); Destroy(rt); }
        if (screenMaterial != null) Destroy(screenMaterial);
    }
}
