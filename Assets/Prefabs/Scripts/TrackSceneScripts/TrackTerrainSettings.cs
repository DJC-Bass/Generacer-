using UnityEngine;

/// <summary>
/// Tuning for <see cref="TrackTerrain"/>, the hills and mountains under the TrackScene. Like the fog, the
/// terrain is built by an object created at runtime (no Inspector instance), so its settings live in an
/// asset instead: TrackTerrain loads <c>Resources/TrackTerrainSettings</c>. Without that asset there's no
/// terrain.
/// </summary>
[CreateAssetMenu(fileName = "TrackTerrainSettings", menuName = "Generacer/Track Terrain Settings")]
public class TrackTerrainSettings : ScriptableObject
{
    public enum GroundContact
    {
        [Tooltip("Solid, and touching it fails the run exactly like the Kill Floor (drones knocked into it " +
                 "pay their bounty and vanish).")]
        EndsTheRun,
        [Tooltip("Solid ground and nothing more: a car that lands on it just sits there.")]
        Solid,
        [Tooltip("Scenery only: falling cars pass through it to the Kill Floor.")]
        None,
    }

    [Tooltip("Master switch for the hills and mountains under the TrackScene. Off = never any terrain; on = " +
             "Terrain Chance decides each round.")]
    public bool enableTerrain = true;

    [Range(0f, 1f)]
    [Tooltip("Chance each round that it's a terrain round (0.75 = 75%); the rest are sky rounds, with nothing " +
             "below the track but the sky and the Kill Floor. Rolled the way the fog and blackout rounds are, " +
             "and the same for every player in multiplayer. Opening the TrackScene on its own (no game loop) " +
             "always builds the terrain, so it can be tuned.")]
    public float terrainChance = 0.75f;

    [Header("Shape")]
    [Tooltip("Altitude of the plains between the hills and mountains. Keep it above the Kill Floor (-500).")]
    public float floorAltitude = -300f;

    [Tooltip("How high the rolling hills rise above the plains.")]
    [Min(0f)] public float hillHeight = 600f;

    [Tooltip("Rough distance between neighbouring hills.")]
    [Min(100f)] public float hillSize = 5000f;

    [Tooltip("How high the tallest mountain peaks rise above the plains. The track's base altitude is " +
             "4000, so taller than that and the course runs through the mountains rather than over them.")]
    [Min(0f)] public float mountainHeight = 7000f;

    [Tooltip("Rough distance between neighbouring mountain ridges.")]
    [Min(100f)] public float mountainSize = 12000f;

    [Tooltip("How much of the land is mountain range rather than hills and plains (0-1).")]
    [Range(0f, 1f)] public float mountainCoverage = 0.5f;

    [Header("Around the Track")]
    [Tooltip("However tall the ground would grow, under the track it stays at least this far below the " +
             "road — the pin-impression part: mountains are pressed down wherever the track runs over them.")]
    [Min(10f)] public float trackClearance = 150f;

    [Tooltip("How steeply the ground may climb away from the track, as height gained per unit of distance " +
             "(1 = 45°, 1.25 ≈ 51°, 2 ≈ 63°). Steeper = the track cuts through the mountains in a narrower " +
             "canyon.")]
    [Min(0.1f)] public float valleySlope = 1.25f;

    [Tooltip("How softly the valley walls round off into the mountains they're cut from (units). 0 = a " +
             "sharp crease where they meet.")]
    [Min(0f)] public float valleySoftness = 400f;

    [Header("Extent & Detail")]
    [Tooltip("How far the terrain reaches beyond the outermost track, in every direction. The far clip " +
             "is 25000, so a smaller margin shows its edge on clear rounds.")]
    [Min(0f)] public float margin = 15000f;

    [Tooltip("Height samples along each side: 513, 1025, 2049 or 4097 (anything else is rounded to one of " +
             "these). More = finer mountains, but each step up is 4× the build time and memory.")]
    public int resolution = 2049;

    [Tooltip("How closely the terrain's distant level of detail follows its true shape, in pixels on " +
             "screen. Lower = more accurate (more triangles); higher = cheaper, but distant mountains can " +
             "creep up over distant track by up to this many pixels.")]
    [Range(1f, 20f)] public float pixelError = 2f;

    [Header("Look")]
    [Tooltip("The ground's colour. Its HUE is replaced each round by the sky's ground hue, so the terrain " +
             "always matches the sky; its saturation and brightness are kept.")]
    public Color groundColor = new Color(0.30f, 0.22f, 0.40f);

    [Tooltip("Colour of the steep slopes and cliffs (the valley walls beside the track included). Hue " +
             "replaced the same way.")]
    public Color slopeColor = new Color(0.22f, 0.17f, 0.29f);

    [Tooltip("Slopes steeper than this (degrees) start turning Slope Color...")]
    [Range(0f, 90f)] public float slopeStart = 30f;

    [Tooltip("...and are all Slope Color by this angle.")]
    [Range(0f, 90f)] public float slopeEnd = 45f;

    [Header("Collision")]
    [Tooltip("What happens when something falls onto the terrain.")]
    public GroundContact groundContact = GroundContact.EndsTheRun;

    [Header("References")]
    [Tooltip("The terrain's material (Universal Render Pipeline/Terrain/Lit, GPU instancing on). " +
             "Referenced here so builds include it.")]
    public Material terrainMaterial;

    [Tooltip("The TrackScene's skybox (SimpleSkybox). In multiplayer the terrain can be built before anyone " +
             "on this machine is under this round's sky, so it needs the base sky to work out the round's " +
             "ground hue from.")]
    public Material trackSkybox;
}
