using UnityEngine;

/// <summary>
/// Central bank of the game's audio clips + default mix levels. Because <see cref="AudioManager"/>
/// is created at runtime (no Inspector instance), the clips it plays live here in an asset instead.
/// The manager loads <c>Resources/AudioLibrary</c>; drop your clips onto that asset to wire up sound.
/// Add new slots here as more audio is needed (engine loops, gameplay stingers, ending themes, ...).
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Generacer/Audio Library")]
public class AudioLibrary : ScriptableObject
{
    [Header("Music")]
    [Tooltip("Looping song for the Main Menu scene.")]
    public AudioClip mainMenuMusic;
    [Tooltip("Looping song for the Car Selection scene (its own track, separate from the Main Menu).")]
    public AudioClip carSelectionMusic;
    [Tooltip("Looping song for the HUB world.")]
    public AudioClip hubMusic;
    [Tooltip("Looping song for the Generacers (flawless-win) ending scene.")]
    public AudioClip generacersEndingMusic;
    [Tooltip("Looping song for the Clipper (Drone-ending escape) ending scene.")]
    public AudioClip clipperEndingMusic;
    [Tooltip("TrackScene song pool — one is picked at random each time you enter the track (never the " +
             "same one twice in a row), so races don't get a stale single theme. Add as many as you like.")]
    public AudioClip[] trackMusic;

    [Header("Menu SFX")]
    [Tooltip("Played when the highlighted menu item changes (navigating up/down the list).")]
    public AudioClip menuMove;
    [Tooltip("Played when a menu item is chosen/confirmed (A / click / Submit).")]
    public AudioClip menuSelect;
    [Tooltip("Played when backing out of a selection or sub-screen (B / Cancel).")]
    public AudioClip menuBack;

    [Header("Vehicle SFX (universal — same for every car)")]
    [Tooltip("One-shot played when Turbo Boost activates.")]
    public AudioClip turboBoost;
    [Tooltip("One-shot played when the car jumps (Jet).")]
    public AudioClip jump;

    [Header("Default Mix (0..1)")]
    [Range(0f, 1f)] public float musicVolume = 0.6f;
    [Range(0f, 1f)] public float sfxVolume = 0.9f;
}
