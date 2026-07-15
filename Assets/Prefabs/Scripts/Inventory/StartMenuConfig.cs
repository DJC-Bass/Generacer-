using UnityEngine;

/// <summary>
/// Editable look/layout settings for the in-game <see cref="StartMenuController"/>. Because that
/// controller is created at runtime (on the PlayerSystems object) it has no Inspector instance to
/// tweak, so its tunables live here in an asset instead. The controller loads
/// <c>Resources/StartMenuConfig</c>; edit that asset to restyle the menu. If the asset is missing,
/// the controller falls back to these defaults.
/// </summary>
[CreateAssetMenu(fileName = "StartMenuConfig", menuName = "Generacer/Start Menu Config")]
public class StartMenuConfig : ScriptableObject
{
    [Header("Button Colors")]
    public Color buttonNormalColor = new Color(0f, 0f, 0f, 0.70f);
    public Color buttonHighlightedColor = new Color(0.12f, 0.68f, 0.90f, 0.95f);
    public Color buttonPressedColor = new Color(0.08f, 0.45f, 0.75f, 1f);
    public Color buttonSelectedColor = new Color(0.12f, 0.68f, 0.90f, 0.95f);
    public Color buttonTextColor = Color.white;

    [Header("Other Colors")]
    public Color titleColor = Color.white;
    [Tooltip("Full-screen dim behind the menu (the game stays visible through it).")]
    public Color dimColor = new Color(0f, 0f, 0f, 0.60f);
    public Color hintColor = new Color(1f, 1f, 1f, 0.60f);

    [Header("Layout — sizes")]
    public Vector2 buttonSize = new Vector2(420f, 76f);
    public float buttonSpacing = 16f;
    public float titleFontSize = 64f;
    public float buttonFontSize = 34f;
    public float hintFontSize = 26f;

    [Header("Layout — vertical positions (from screen centre)")]
    public float titleY = 270f;
    public float buttonColumnY = 0f;
    public float hintY = -330f;

    [Header("Layout — Controls screen rows")]
    [Tooltip("Height of each key-binding row (and the RESET button) on the CONTROLS screen (pixels). " +
             "Shrink so the full list clears the title.")]
    public float controlsRowHeight = 34f;
    [Tooltip("Font size of the CONTROLS rows as a fraction of Button Font Size.")]
    public float controlsRowFontScale = 0.6f;
    [Tooltip("Vertical gap between CONTROLS rows (pixels).")]
    public float controlsRowSpacing = 4f;
}
