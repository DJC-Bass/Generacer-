using UnityEngine;

/// <summary>
/// Gives the TrackScene a chance to run a "blackout" round with its Directional Light switched off.
/// Every TrackScene load re-rolls (the scene reloads fresh each track run, so this is per round): it
/// rolls <see cref="deactivateChance"/> and, on a hit, disables the light for that round.
///
/// The light is resolved as: the assigned <see cref="directionalLight"/>, else a Light on this same
/// GameObject, else the first Directional light found in the scene. Like <see cref="RoundObstacleSelector"/>
/// it only acts during an actual game-loop run — with no GameLoopManager (e.g. opening TrackScene on
/// its own) the light is left on. Runs in Start, which Unity calls before the first frame renders, so
/// a blackout round never flashes the light on for a frame.
/// </summary>
public class RoundDirectionalLightToggle : MonoBehaviour
{
    [Tooltip("The TrackScene's Directional Light. Leave empty to auto-use a Light on this GameObject, " +
             "or the first Directional light found in the scene.")]
    public Light directionalLight;

    [Range(0f, 1f)]
    [Tooltip("Chance each round that the Directional Light is deactivated for that round (0.33 = 33%).")]
    public float deactivateChance = 0.33f;

    void Start()
    {
        // Only during an actual game-loop run (mirrors RoundObstacleSelector: no manager -> normal scene).
        if (GameLoopManager.Instance == null) return;

        Light target = ResolveLight();
        if (target == null)
        {
            Debug.LogWarning("[RoundDirectionalLightToggle] No Directional Light found to toggle.");
            return;
        }

        // Multiplayer: the roll derives from the server's round seed so every player gets the same
        // blackout (or lack of one). Single-player keeps the plain per-load roll.
        bool blackout = MultiplayerWorld.IsMultiplayerGame
            ? MultiplayerWorld.DeriveRandom("blackout").NextDouble() < deactivateChance
            : Random.value < deactivateChance;
        if (blackout) target.enabled = false;

        Debug.Log($"[RoundDirectionalLightToggle] Round {GameLoopManager.Instance.RoundNumber}: " +
                  $"Directional Light {(blackout ? "OFF (blackout round)" : "on")}.");
    }

    /// <summary>Assigned light wins; otherwise a Directional Light on this object, then the first one
    /// in the scene.</summary>
    Light ResolveLight()
    {
        if (directionalLight != null) return directionalLight;

        var self = GetComponent<Light>();
        if (self != null && self.type == LightType.Directional) return self;

        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) return l;

        return null;
    }
}
