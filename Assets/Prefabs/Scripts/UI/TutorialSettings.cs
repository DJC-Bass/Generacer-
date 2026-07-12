using UnityEngine;

/// <summary>
/// Player-facing preference for the Tutorial scene's on-screen guide. Backed by PlayerPrefs, so the
/// choice persists across sessions. Toggled from the Start menu's SETTINGS panel
/// (<see cref="StartMenuController"/>) and read by <see cref="TutorialGuide"/>.
/// </summary>
public static class TutorialSettings
{
    const string GuideKey = "tutorial.guideEnabled";

    /// <summary>Whether the Tutorial scene shows its on-screen guide messages. Defaults to ON.</summary>
    public static bool GuideEnabled
    {
        get => PlayerPrefs.GetInt(GuideKey, 1) != 0;
        set { PlayerPrefs.SetInt(GuideKey, value ? 1 : 0); PlayerPrefs.Save(); }
    }
}
