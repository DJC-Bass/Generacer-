using UnityEngine;

/// <summary>
/// Tuning for <see cref="TrackFog"/>, the TrackScene's height fog. Like the AudioLibrary, the fog is run
/// by an object created at runtime (no Inspector instance), so its settings live in an asset instead:
/// TrackFog loads <c>Resources/TrackFogSettings</c>. Without that asset there's no fog.
/// </summary>
[CreateAssetMenu(fileName = "TrackFogSettings", menuName = "Generacer/Track Fog Settings")]
public class TrackFogSettings : ScriptableObject
{
    [Tooltip("Master switch for the TrackScene's fog. Off = never any fog; on = Fog Chance decides each round.")]
    public bool enableFog = true;

    [Range(0f, 1f)]
    [Tooltip("Chance each round that it's a fog round (0.33 = 33%) — rolled the way the Directional Light's " +
             "blackout round is, and the same for every player in multiplayer. Opening the TrackScene on its " +
             "own (no game loop) always shows the fog, so it can be tuned.")]
    public float fogChance = 0.33f;

    [Header("Thickness")]
    [Tooltip("At the Reference Altitude, looking level: the distance (units) at which the fog is all but " +
             "total (95%) — the far course has faded into the sky by here. Lower = thicker. At the default " +
             "7500 (with the default Start Distance), scenery 1 km off is ~25% faded, 2 km ~50%, 3 km ~67%. " +
             "At 600 mph the car covers ~270 a second.")]
    [Min(100f)] public float fullFogDistance = 7500f;

    [Tooltip("The altitude Full Fog Distance is measured at. The Track Generator's Base Altitude (3000) — " +
             "the height most of the course runs around.")]
    public float referenceAltitude = 3000f;

    [Tooltip("Every this many units higher, the fog is half as thick; every this many lower, twice as " +
             "thick. Low sections and valleys between the tracks sit in haze, high ones in clearer air. " +
             "Larger = more even with height.")]
    [Min(10f)] public float halfHeight = 1500f;

    [Tooltip("Clear air around the car: the fog only starts gathering this far (units) from the camera, so " +
             "the road just ahead is never hazed.")]
    [Min(0f)] public float startDistance = 300f;

    [Tooltip("The most the fog can fade anything (0-1). 1 = far enough off, scenery vanishes into the sky " +
             "entirely; lower keeps a ghost of it however far.")]
    [Range(0f, 1f)] public float maxOpacity = 1f;

    [Header("Horizon")]
    [Tooltip("How many degrees below the horizon the sky's ground fades up into its horizon colour, instead " +
             "of meeting it in a hard line — so far track just under the horizon fades into haze, not into " +
             "dark ground under a near-white edge. Drawn by the fog: only on the track's cameras, and it " +
             "doesn't light the scene. 0 = the sky's own hard edge.")]
    [Range(0f, 45f)] public float groundHaze = 10f;

    [Header("References")]
    [Tooltip("The TrackScene's skybox (SimpleSkybox). The fog fades each pixel into THIS ROUND's recolour " +
             "of it, in that pixel's direction, so distant track melts into the sky behind it. The hub's " +
             "Spectator TVs and the Support Ship pilot, who look at the track from the hub, need it to " +
             "know which sky that is.")]
    public Material trackSkybox;

    [Tooltip("The fog's full-screen shader (Hidden/Generacer/TrackHeightFog). Referenced here so builds " +
             "include it.")]
    public Shader heightFogShader;
}
