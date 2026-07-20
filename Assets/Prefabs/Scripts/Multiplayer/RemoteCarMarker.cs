using TMPro;
using UnityEngine;

/// <summary>
/// Phase 6: the floating label over a remote player's car — "TEAMMATE" over your own team's cars,
/// "RIVAL" over the one opposing player the rival system assigned to YOU. Built entirely in code
/// (a 3D TextMeshPro billboarded to the camera each frame); RemoteCarManager.RefreshMarkers decides
/// what each puppet shows for THIS machine's viewer, so rival markers are inherently private — every
/// client only ever labels its own rival.
/// </summary>
public class RemoteCarMarker : MonoBehaviour
{
    [Tooltip("Height of the label above the car's origin (units).")]
    public float heightAboveCar = 3.5f;

    private TextMeshPro label;

    void EnsureLabel()
    {
        if (label != null) return;
        var go = new GameObject("Marker");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * heightAboveCar;
        go.transform.localScale = Vector3.one * 0.35f;

        label = go.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 36f;
        label.fontStyle = FontStyles.Bold;
        label.rectTransform.sizeDelta = new Vector2(14f, 3f);
        label.enableWordWrapping = false;
    }

    public void Show(string text, Color color)
    {
        EnsureLabel();
        label.gameObject.SetActive(true);
        label.text = text;
        label.color = color;
    }

    public void Hide()
    {
        if (label != null) label.gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        if (label == null || !label.gameObject.activeSelf) return;
        var cam = Camera.main;
        if (cam == null) return;
        // Billboard: face the viewing camera (after CameraFollow's LateUpdate has settled it).
        label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
    }
}
