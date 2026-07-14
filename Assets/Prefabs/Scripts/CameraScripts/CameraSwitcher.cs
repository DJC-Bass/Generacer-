using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Switches between the main (forward) follow camera and a dedicated rear-view follow camera
/// on R3. Both cameras keep running their own CameraFollow every frame, so each stays correctly
/// positioned at all times — only the active one renders, which makes the switch instant (no
/// swivel). Set the rear-view camera's CameraFollow to "Rear View" so it sits in front of the
/// car looking back.
/// </summary>
public class CameraSwitcher : MonoBehaviour
{
    [Tooltip("The normal forward-facing follow camera.")]
    public Camera mainCamera;
    [Tooltip("The dedicated rear-view follow camera (its CameraFollow should have Rear View ticked).")]
    public Camera rearViewCamera;

    private GeneracerControls controls;
    private bool rearActive;

    void OnEnable()
    {
        if (controls == null)
        {
            controls = new GeneracerControls();
            InputRebinding.ApplyOverridesTo(controls.asset);   // honour the player's saved control rebinds
        }
        controls.Driving.Enable();
        InputRebinding.OverridesChanged += ReapplyOverrides;   // pick up an in-game rebind immediately
    }

    void OnDisable()
    {
        InputRebinding.OverridesChanged -= ReapplyOverrides;
        controls?.Driving.Disable();
    }

    void ReapplyOverrides() { if (controls != null) InputRebinding.ApplyOverridesTo(controls.asset); }

    void Start()
    {
        // Audio always comes from the main camera, so make sure the rear-view camera isn't a
        // second AudioListener (which would spam "multiple listeners" warnings).
        if (rearViewCamera != null)
        {
            var listener = rearViewCamera.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = false;
        }

        Apply();
    }

    void Update()
    {
        // R3 toggles which camera is live. triggered fires once per press, not held.
        if (controls.Driving.RearView.triggered)
        {
            rearActive = !rearActive;
            Apply();
        }
    }

    void Apply()
    {
        // Exactly one camera renders at a time. The other keeps following but stays dark.
        if (mainCamera != null) mainCamera.enabled = !rearActive;
        if (rearViewCamera != null) rearViewCamera.enabled = rearActive;
    }
}
