using UnityEngine;

/// <summary>
/// Editable content + look for the Tutorial scene's on-screen guide (<see cref="TutorialGuide"/>).
/// The guide is created at runtime (on the persistent PlayerSystems object), so it has no Inspector
/// instance — its data lives here, mirroring how <see cref="StartMenuConfig"/> backs the Start menu.
/// The guide loads <c>Resources/TutorialGuideConfig</c>; if the asset is missing it falls back to
/// these defaults. Create/duplicate the asset via Assets &gt; Create &gt; Generacer &gt; Tutorial
/// Guide Config to change the messages without touching code.
/// </summary>
[CreateAssetMenu(fileName = "TutorialGuideConfig", menuName = "Generacer/Tutorial Guide Config")]
public class TutorialGuideConfig : ScriptableObject
{
    [Tooltip("Scene the guide runs in. It's a no-op in every other scene.")]
    public string sceneName = "Tutorial";

    [Tooltip("Seconds each message stays up before auto-advancing to the next.")]
    public float messageDuration = 3f;

    [Tooltip("The guide messages, shown in order. Edit these to teach your exact controls.")]
    [TextArea]
    public string[] messages = new[]
    {
        "Welcome to the Tutorial! This is a safe playground — drive around and get a feel for the controls.",
        "Use the left stick to steer, and the triggers to accelerate and brake.",
        "Try jumping, drifting, and using your Turbo boost to explore the city.",
        "Out on the track, collect and activate SD abilities to gain an edge.",
        "Press Start any time for the menu. You can turn these tips off under Settings.",
        "Tip: press D-Pad Left / Right to step back and forth through these messages.",
    };

    [Header("Look")]
    public float messageFontSize = 40f;
    public Color messageColor = Color.white;
    [Tooltip("Background panel behind the message (kept semi-transparent so the game shows through).")]
    public Color panelColor = new Color(0f, 0f, 0f, 0.6f);
    public float hintFontSize = 24f;
    public Color hintColor = new Color(1f, 1f, 1f, 0.6f);
    [Tooltip("Distance (reference px) from the top screen edge down to the panel's top.")]
    public float topOffset = 120f;
    public Vector2 panelSize = new Vector2(1100f, 120f);
}
