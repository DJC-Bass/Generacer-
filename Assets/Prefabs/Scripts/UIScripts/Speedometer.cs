using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Bottom-right MPH readout. Drop on any GameObject in the scene. If no car or
/// label is assigned it auto-finds the CarController and builds its own Canvas
/// + TextMeshProUGUI anchored bottom-right.
/// </summary>
public class Speedometer : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Car to read speed from. Auto-found at Start if left empty.")]
    public CarController car;

    [Header("Label")]
    [Tooltip("Optional pre-existing TMP label. If empty, one is created at Start " +
             "inside an auto-built Canvas anchored to the bottom-right.")]
    public TextMeshProUGUI label;

    [Header("Format")]
    [Tooltip("printf-style format for the speed value. F0 = no decimals.")]
    public string format = "{0:F0} MPH";

    [Header("Auto-Built Style (only used if label is null)")]
    public int fontSize = 64;
    public Color textColor = Color.white;
    [Tooltip("Pixel padding from the bottom-right corner of the screen.")]
    public Vector2 screenPadding = new Vector2(40f, 30f);

    void Start()
    {
        if (car == null) car = FindObjectOfType<CarController>();
        if (label == null) label = BuildLabel();
    }

    void Update()
    {
        if (car == null || label == null) return;
        label.text = string.Format(format, car.SpeedMph);
    }

    TextMeshProUGUI BuildLabel()
    {
        // Reuse any Canvas in the scene to avoid stacking multiple. Otherwise build one.
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGO = new GameObject("SpeedometerCanvas");
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
        }

        GameObject textGO = new GameObject("SpeedometerText");
        textGO.transform.SetParent(canvas.transform, false);

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = fontSize;
        tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.BottomRight;
        tmp.text = "0 MPH";

        // Anchor bottom-right, pivot bottom-right, sit `screenPadding` pixels in.
        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(400f, 100f);
        rt.anchoredPosition = new Vector2(-screenPadding.x, screenPadding.y);

        return tmp;
    }
}
