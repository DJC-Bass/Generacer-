using TMPro;
using UnityEngine;

/// <summary>
/// HUB-world digital countdown clock. Shows the current round's TIME REMAINING as MM:SS:SSS
/// (minutes : seconds : milliseconds), counting down, so players standing in the hub can see
/// how much time is left in the TrackScene round.
///
/// It reads <see cref="GameLoopManager.RoundTimeRemaining"/>, which counts down while the round is live
/// (portal up / players racing) and holds at the full <c>roundDuration</c> during the pre-round load —
/// so the screen naturally reads the full time before GO, then ticks down to 00:00:00. Works in
/// single-player and multiplayer alike (the server-driven puppet timer feeds the very same property).
///
/// Attach this to the Digital Clock prefab. For the screen itself you have two options:
///   • RECOMMENDED — add a TextMeshPro (3D Object ▸ Text - TextMeshPro) as a child, drag it onto the
///     screen face in the Scene view so you can see it, and assign it to <see cref="display"/>.
///   • Or leave <see cref="display"/> empty: this auto-creates one at Play, which you position with the
///     screen* fields below (enter Play mode to see it, then nudge the offset/rotation/scale).
/// The digits are a flat-coloured text on the (dark) screen face — no billboarding; it stays flush.
/// </summary>
public class HubRoundClock : MonoBehaviour
{
    [Header("Screen text")]
    [Tooltip("The TextMeshPro (3D) that shows the countdown. Leave empty to auto-create one you can " +
             "position with the fields below.")]
    public TMP_Text display;

    [Tooltip("Colour of the digits (a bright colour reads well on the dark screen).")]
    public Color digitColor = new Color(1f, 0.25f, 0.2f);   // red LED look

    [Tooltip("Separator between the fields. \":\" is the classic clock look; set to \"/\" for MM/SS/SSS.")]
    public string separator = ":";

    [Tooltip("Shown when there's no round timer yet (main menu / before the loop starts).")]
    public string idleText = "00:00:000";

    [Header("Auto-created screen (ignored once Display is assigned)")]
    [Tooltip("Local position of the auto-created text relative to this object — push it onto the face.")]
    public Vector3 screenLocalPosition = Vector3.zero;
    [Tooltip("Local rotation (euler) of the auto-created text. If the digits read mirrored, set Y to 180.")]
    public Vector3 screenLocalEuler = Vector3.zero;
    [Tooltip("Uniform local scale of the auto-created text — the quickest knob to make it fit the face.")]
    public float screenScale = 0.2f;
    [Tooltip("Font size of the auto-created text.")]
    public float fontSize = 36f;
    [Tooltip("Width/height (local units) of the auto-created text box. Enlarge if the digits clip.")]
    public Vector2 screenSize = new Vector2(30f, 8f);

    void Awake()
    {
        if (display == null) display = CreateScreen();
        if (display != null) display.color = digitColor;   // the colour field is the single source of truth
    }

    void Update()
    {
        if (display == null) return;
        var glm = GameLoopManager.Instance;
        display.text = glm != null ? Format(glm.RoundTimeRemaining, separator) : idleText;
    }

    TMP_Text CreateScreen()
    {
        var go = new GameObject("RoundClockScreen");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = screenLocalPosition;
        go.transform.localEulerAngles = screenLocalEuler;
        go.transform.localScale = Vector3.one * screenScale;

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = fontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = screenSize;
        tmp.text = idleText;
        return tmp;
    }

    /// <summary>Formats seconds as MM:SS:SSS — minutes and seconds padded to 2 digits, milliseconds to
    /// 3 (000–999). Minutes grow past 2 digits only for rounds longer than 100 minutes.</summary>
    static string Format(float seconds, string sep)
    {
        if (seconds < 0f) seconds = 0f;
        int millis = Mathf.FloorToInt(seconds * 1000f);
        int m = millis / 60000;
        int s = (millis / 1000) % 60;
        int ms = millis % 1000;
        return $"{m:00}{sep}{s:00}{sep}{ms:000}";
    }
}
