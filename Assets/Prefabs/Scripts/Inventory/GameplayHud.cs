/// <summary>
/// Central rule for where the in-game HUDs (credits, turbo, jet) are shown. They appear ONLY in
/// actual gameplay scenes and are hidden everywhere else — the main menu, car selection, settings,
/// etc. The HUDs are persistent (DontDestroyOnLoad), so each one checks this on every scene load.
/// Add any new gameplay scene names here.
/// </summary>
public static class GameplayHud
{
    // Scenes the gameplay HUDs are visible in. Anything NOT listed (MainMenu, CarSelection, ...)
    // hides them.
    public static readonly string[] GameplayScenes = { "HubWorld", "TrackScene" };

    /// <summary>True if the gameplay HUDs should be visible in the named scene.</summary>
    public static bool VisibleInScene(string sceneName)
    {
        foreach (var s in GameplayScenes)
            if (s == sceneName) return true;
        return false;
    }
}
