using System;
using UnityEngine.SceneManagement;

/// <summary>
/// Central rule for WHICH in-game HUDs are shown and WHEN. Two questions, and they are separate:
///
///  1. WHERE — the HUDs appear only in actual gameplay scenes, never in the main menu, car selection
///     or settings. They are persistent (DontDestroyOnLoad), so each one re-checks on every scene load.
///  2. WHOSE — a player flying a Support Ship is looking through a different vehicle's camera, so the
///     CAR's instruments (speed, turbo/jet stock, equipped SD) are reporting on a machine they are not
///     driving and are parked somewhere they cannot see. Those hide; the ship gets its own readout.
///
/// Credits are the one thing SHARED between the two views: currency belongs to the player, not to
/// whichever vehicle they happen to be looking out of.
/// </summary>
public static class GameplayHud
{
    // Scenes the gameplay HUDs are visible in. Anything NOT listed (MainMenu, CarSelection, ...)
    // hides them.
    public static readonly string[] GameplayScenes = { "HubWorld", "TrackScene", "GeneracersEnding", "ClipperEnding" };

    /// <summary>True if the gameplay HUDs should be visible in the named scene.</summary>
    public static bool VisibleInScene(string sceneName)
    {
        foreach (var s in GameplayScenes)
            if (s == sceneName) return true;
        return false;
    }

    /// <summary>Raised whenever an answer below changes, so a persistent HUD can re-apply without
    /// polling. Scene loads already prompt their own re-check; this covers the piloting switch, which
    /// happens with no scene change at all.</summary>
    public static event Action OnVisibilityChanged;

    /// <summary>True while the local player is flying a Support Ship rather than driving.</summary>
    public static bool Piloting { get; private set; }

    public static void SetPiloting(bool piloting)
    {
        if (Piloting == piloting) return;
        Piloting = piloting;
        OnVisibilityChanged?.Invoke();
    }

    static bool InGameplayScene => VisibleInScene(SceneManager.GetActiveScene().name);

    /// <summary>Shown in BOTH views — the player's own state rather than a vehicle's.</summary>
    public static bool ShowShared => InGameplayScene;

    /// <summary>Shown only while actually driving: instruments that read the CAR.</summary>
    public static bool ShowCarHud => InGameplayScene && !Piloting;
}
