using UnityEngine;

/// <summary>
/// Plays the one-shot 3D "portal exit" sound off the player car when it spawns into a scene that was
/// reached by travelling through a portal. Put this on the player-car prefab (the one chosen in Car
/// Selection).
///
/// A portal arms the sound via <see cref="AudioManager.ArmPortalExit"/> right before it loads its
/// destination scene; when the car is instantiated into that scene (by PlayerCarSwapper, or the
/// TrackGenerator's delayed spawn), this fires it off the car — so it rides the prefab like the
/// speed-barrier stingers, with no scene-side "find the player" timing to get wrong. If the scene was
/// not reached through a portal, the flag isn't set and nothing plays.
/// </summary>
public class PortalExitAudio : MonoBehaviour
{
    void Start() => AudioManager.TryPlayPortalExit(transform);
}
