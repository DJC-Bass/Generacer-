using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Phase 6: the floating tag over a remote player's car — "T" over your own team's cars, "R" over the
/// one opposing player the rival system assigned to YOU. Built entirely in code (a 3D TextMeshPro
/// facing the camera); RemoteCarManager.RefreshMarkers decides what each puppet shows for THIS
/// machine's viewer, so rival markers are inherently private — every client only ever labels its own
/// rival.
///
/// The tag keeps the same size ON SCREEN at any distance — the size of the credits readout — so a
/// teammate a kilometre down the track, long shrunk to a dot, still carries a readable letter. It is
/// still an ordinary depth-tested object out in the world, so the track, a hill or an obstacle between
/// you and the car covers the tag just as it covers the car.
///
/// Sized and placed per camera, just before that camera draws: by then every script has moved the cars
/// and the camera, and CameraFollow has set this frame's speed FOV. Only a camera drawing the SCREEN
/// shows tags (the player's view, the rear view, a Support Ship pilot's view); the hub's spectator TVs
/// render into textures and leave them out, since a tag sized for your screen means nothing on theirs.
/// </summary>
public class RemoteCarMarker : MonoBehaviour
{
    [Tooltip("Height of the tag above the car's origin (units), along the car's up.")]
    public float heightAboveCar = 3.5f;
    [Tooltip("Size of the tag on screen, in the HUD's font units (1920×1080 reference). 44 matches the credits readout.")]
    public float screenFontSize = 44f;

    /// <summary>A 3D TextMeshPro's em is a tenth of its font size in units, so at 10 the tag's scale IS
    /// its em in world units.</summary>
    const float LabelFontSize = 10f;

    static readonly List<RemoteCarMarker> active = new List<RemoteCarMarker>();

    private TextMeshPro label;
    private MeshRenderer labelRenderer;
    private RemoteCarPuppet puppet;
    private bool shown;

    void Awake()
    {
        puppet = GetComponent<RemoteCarPuppet>();
    }

    void EnsureLabel()
    {
        if (label != null) return;
        // Its own root rather than a child of the car: it takes no rotation or scale from the car, and
        // the car's scale (a placeholder cube is 2 × 1.5 × 4.5) would skew a camera-facing child.
        var go = new GameObject(name + "_Tag");
        DontDestroyOnLoad(go);   // like the car it rides — destroyed with it in OnDestroy

        label = go.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = LabelFontSize;
        label.fontStyle = FontStyles.Bold;
        label.rectTransform.sizeDelta = new Vector2(2f, 1.5f);
        label.enableWordWrapping = false;

        // Far off the tag is tens of metres across; it must not throw that as a shadow on the track.
        labelRenderer = go.GetComponent<MeshRenderer>();
        labelRenderer.shadowCastingMode = ShadowCastingMode.Off;
        labelRenderer.receiveShadows = false;
    }

    public void Show(string text, Color color)
    {
        EnsureLabel();
        shown = true;
        label.gameObject.SetActive(isActiveAndEnabled);
        label.text = text;
        label.color = color;
    }

    public void Hide()
    {
        shown = false;
        if (label != null) label.gameObject.SetActive(false);
    }

    void OnEnable()
    {
        if (active.Count == 0) RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        active.Add(this);
        if (label != null) label.gameObject.SetActive(shown);
    }

    void OnDisable()
    {
        active.Remove(this);
        if (active.Count == 0) RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (label != null) label.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (label != null) Destroy(label.gameObject);
    }

    static void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        bool drawsScreen = cam.cameraType == CameraType.Game && cam.targetTexture == null;
        for (int i = 0; i < active.Count; i++) active[i].PrepareFor(cam, drawsScreen);
    }

    void PrepareFor(Camera cam, bool drawsScreen)
    {
        if (label == null || !label.gameObject.activeSelf) return;
        if ((cam.cullingMask & (1 << label.gameObject.layer)) == 0) return;   // can't draw it anyway
        labelRenderer.enabled = drawsScreen && Place(cam);
    }

    /// <summary>Poses the tag for this camera. False when it shouldn't show at all.</summary>
    bool Place(Camera cam)
    {
        // A puppet parks 10 km down until its owner's first state lands — no tag floating out there.
        if (puppet != null && !puppet.HasState) return false;

        Transform view = cam.transform;
        Vector3 carPos = transform.position;
        float depth = Vector3.Dot(carPos - view.position, view.forward);
        // Behind us, or past the far clip (where a car in the OTHER area always is — see
        // MultiplayerWorld.HubAreaOffset).
        if (depth <= cam.nearClipPlane || depth > cam.farClipPlane) return false;

        // The em the HUD would draw, in pixels — CreditsHUD's canvas scales with the screen at a 0.5
        // width/height match on a 1920×1080 reference — then the world size of that many pixels at the
        // car's depth.
        float hudScale = Mathf.Sqrt(Screen.width / 1920f * (Screen.height / 1080f));
        float worldPerPixel = 2f * depth * Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad) / cam.pixelHeight;
        float em = screenFontSize * hudScale * worldPerPixel;

        // Raised by over half its own size too: far off, where the tag dwarfs the car, a fixed height
        // would sink it into the car and the road beneath it.
        label.transform.SetPositionAndRotation(carPos + transform.up * (heightAboveCar + 0.6f * em), view.rotation);
        label.transform.localScale = Vector3.one * em;
        return true;
    }
}
