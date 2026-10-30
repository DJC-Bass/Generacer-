using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TrackGenerator : MonoBehaviour
{
    // -------------------------------------------------------
    //  Inspector
    // -------------------------------------------------------

    [Header("Seed (0 = random each play)")]
    public int seed = 0;

    [Header("Track Length")]
    [Tooltip("Sets the track's scale: the finish portal sits about 1.1x this far ahead of the start. " +
             "Every start-to-finish route, whichever branches it takes, is then tuned to one shared " +
             "length, so no route is a shortcut.")]
    public float targetBranchLength = 22000f;

    [Header("Branching")]
    [Tooltip("Segments in every start-to-finish route. Each segment can swing, loop, or split off a " +
             "new path, so more segments make a busier track.")]
    [Range(8, 48)] public int segmentsPerRoute = 24;
    [Tooltip("Chance (0-1) that a segment splits off a new path. It's rolled along every path, new " +
             "ones included, so branches can branch again.")]
    [Range(0f, 1f)] public float branchChance = 0.2f;
    [Tooltip("The track always has at least this many paths. If the rolls come up short, extra " +
             "splits are forced along the way.")]
    [Range(1, 32)] public int minPaths = 4;
    [Tooltip("The track never has more than this many paths, so the finish portal doesn't get " +
             "overcrowded. 1 = a single path with no splits.")]
    [Range(1, 32)] public int maxPaths = 16;
    [Tooltip("How wide (units) the paths fan out through the middle of the track. Each path winds " +
             "freely on top of that, crossing over and under the others.")]
    public float branchSpread = 18000f;
    [Tooltip("Minimum angle (degrees) between the two roads leaving a split, so a new path visibly " +
             "peels away instead of running alongside its parent, where a player could hop across.")]
    public float minBranchAngle = 45f;

    [Header("Curve Smoothness")]
    [Tooltip("Distance between sampled waypoints along each Bezier (units). " +
             "Smaller = smoother mesh, larger = faster generation.")]
    public float waypointSpacing = 150f;
    [Tooltip("Mesh sampling density. 0.1 = one mesh ring every 10 units.")]
    public float meshSamplesPerUnit = 0.1f;

    [Header("Track Dimensions")]
    public float baseAltitude = 200f;
    public float minAltitude = 50f;
    public float roadWidth = 14f;
    public float roadThickness = 2f;
    public float uvTilingFactor = 0.04f;

    [Header("Shoulders")]
    [Tooltip("Width of the shoulder strip extruded from each side of the road.")]
    public float shoulderWidth = 4f;
    [Tooltip("Material applied to the shoulder strips. Use a URP/Lit material " +
             "with an emission color to make them glow.")]
    public Material shoulderMaterial;
    [Tooltip("How far the shoulders rise above the road surface, forming a curb rail that " +
             "outlines the turns (units). 0 = flush shoulders.")]
    [Min(0f)] public float shoulderRailHeight = 1f;
    [Tooltip("On = the raised rail is solid and stops the car like a real barrier. Off = the rail " +
             "is visual only; the car collides with the flat shoulder underneath and can run off " +
             "the track.")]
    public bool shoulderRailCollision = true;

    [Header("Altitude Layering")]
    [Tooltip("Vertical spacing between the paths' altitude layers (each path gets its own, at " +
             "random), so they run at different heights through the middle of the track.")]
    public float altitudeLayerSpacing = 120f;

    [Header("Assets")]
    public Material roadMaterial;
    // Per-generation instance of the road material with a randomised Base Map hue, so each track comes
    // out a different colour without ever modifying the shared RoadMaterial asset.
    private Material runtimeRoadMaterial;
    // Per-generation instance of the Shoulder Material whose Emission colour takes the SAME random hue
    // as the road, so the glow always matches — again without modifying the shared asset.
    private Material runtimeShoulderMaterial;
    public GameObject carPrefab;
    public GameObject endPortalPrefab;

    [Header("Path Style")]
    [Tooltip("How far each path swings from side to side as it winds to the finish (units).")]
    public float convergenceSwing = 2500f;
    [Tooltip("How much altitude each path can swing through on its way to the finish.")]
    public float convergenceAltitudeSwing = 800f;
    [Tooltip("Random ± deviation applied to Convergence Altitude Swing once per track " +
             "generation. 200 means each new track rolls a swing somewhere in " +
             "[base-200, base+200]. 0 = always exactly the base value.")]
    public float convergenceAltitudeSwingVariance = 0f;
    // Resolved per-generation value of convergenceAltitudeSwing (base ± variance).
    // Rolled once in GenerateTrack and read by RollPathStyle / LayOutSection.
    private float currentConvergenceAltitudeSwing;

    [Header("Road Roll")]
    [Tooltip("How far the roads roll (bank) over to either side as they wind (degrees), each path its " +
             "own way: 0 = always flat, 90 = right over on their side. A loop takes the roll of the road " +
             "leading into it, so a rolled overhead loop leans over with it — on its side at 90.")]
    [Range(0f, 90f)] public float rollSwing = 45f;
    [Tooltip("Random ± deviation applied to Roll Swing once per track generation (degrees). 20 means " +
             "each new track rolls somewhere in [base-20, base+20]. 0 = always exactly the base value.")]
    [Min(0f)] public float rollSwingVariance = 0f;
    [Tooltip("The fastest a road may roll over, in degrees per 100 units of road, so it twists gently " +
             "instead of corkscrewing.")]
    [Min(0.1f)] public float maxRollRate = 3f;
    // Resolved per-generation value of rollSwing (base ± variance), rolled once in GenerateTrack.
    private float currentRollSwing;

    [Header("Start Position Variance")]
    [Tooltip("Maximum lateral (X) offset of the start point from world origin (units).")]
    public float startLateralVariance = 2000f;
    [Tooltip("Maximum altitude offset added to baseAltitude (units, applied positive only " +
             "so the start never goes below baseAltitude).")]
    public float startAltitudeVariance = 800f;
    [Tooltip("Maximum forward (Z) offset of the start point from world origin (units).")]
    public float startForwardVariance = 1500f;

    [Header("Finish Position Variance")]
    [Tooltip("Maximum lateral (X) offset of the finish portal from straight-ahead (units).")]
    public float finishLateralVariance = 4000f;
    [Tooltip("Maximum altitude (Y) offset of the finish portal from base altitude (units).")]
    public float finishAltitudeVariance = 1500f;
    [Tooltip("How much further forward the finish can sit (units). Prevents finish from " +
             "ending up too close to leaves and forcing tight curves.")]
    public float finishForwardVariance = 3000f;

    [Header("Finish Spread")]
    [Tooltip("Distance between neighbouring paths' ends across the finish portal (units). The ends " +
         "are also staggered up and down (up to Altitude Layer Spacing apart, squeezed to fit the " +
         "portal's opening), so they don't overlap.")]
    public float finishLateralSpacing = 60f;

    [Header("Player Spawn")]
    [Tooltip("Initial forward speed in mph when the car spawns. 0 = stationary.")]
    public float spawnSpeedMph = 300f;
    [Tooltip("The Main Camera with CameraFollow attached. Will be auto-targeted to the spawned car.")]
    public CameraFollow followCamera;

    [Header("Car Spawn Adjustment")]
    [Tooltip("Forward offset from the track start point (units). Positive values " +
         "move the car further onto the track, negative values move it backward.")]
    public float carSpawnForwardOffset = 5f;
    [Tooltip("Height offset from the track surface (units). Positive values spawn " +
             "the car above the track, useful for letting it settle naturally onto its wheels.")]
    public float carSpawnHeightOffset = 1.5f;

    [Header("Spawn Boost")]
    [Tooltip("Initial forward velocity applied to the car at spawn (mph). " +
         "Set to 0 to disable. Drag is reduced to zero during the boost so " +
         "the velocity is preserved cleanly.")]
    public float spawnVelocityMph = 300f;

    [Header("Loops")]
    [Tooltip("Probability that any given segment of a path is replaced with a loop (0-1), rolled on " +
             "every path independently of Side Loop Chance; if both claim the same segment it's a " +
             "50/50 coin flip which one forms. Where one way on from a split comes out much shorter " +
             "than the other, it gets an extra loop or two to even them up (or, short of room, a loop on " +
             "either way is resized, within these ranges, or taken out).")]
    [Range(0f, 1f)] public float loopChance = 0.2f;
    [Tooltip("Minimum loop radius (units).")]
    public float minLoopRadius = 200f;
    [Tooltip("Maximum loop radius (units).")]
    public float maxLoopRadius = 350f;
    [Tooltip("Minimum sideways (left/right) gap between the loop entry and exit. " +
                 "Each loop picks a random value between min and max — like sliding the " +
                 "ribbon's far end left or right by a varying amount.")]
    public float minLoopExitOffset = 250f;
    [Tooltip("Maximum sideways (left/right) gap between the loop entry and exit.")]
    public float maxLoopExitOffset = 600f;
    [Tooltip("Forward (+) or backward (-) offset of the loop exit from the entry, " +
             "along the direction of travel. 0 = exit directly beside the entry; " +
             "positive pushes the following segment further ahead; negative pulls it back.")]
    public float loopExitForwardOffset = 0f;

    [Header("Side Loops")]
    [Tooltip("Probability that any given segment of a path is replaced with a side loop (0-1) — " +
             "a flat 360° left or right turn that passes over/under itself. Rolled on every path " +
             "independently of Loop Chance; if both claim the same segment it's a 50/50 coin flip " +
             "which one forms.")]
    [Range(0f, 1f)] public float sideLoopChance = 0f;
    [Tooltip("Minimum side loop radius (units). The car holds a full-steer turn of roughly " +
             "160 units at 300 mph, so keep this well above that for a comfortable turn at speed.")]
    public float minSideLoopRadius = 600f;
    [Tooltip("Maximum side loop radius (units).")]
    public float maxSideLoopRadius = 800f;
    [Tooltip("Minimum up/down gap between the side loop entry and exit — the clearance where the " +
             "road passes over itself. Each side loop picks a random value between min and max, " +
             "and randomly climbs (exit above the entry) or descends (exit below).")]
    public float minSideLoopExitOffset = 120f;
    [Tooltip("Maximum up/down gap between the side loop entry and exit.")]
    public float maxSideLoopExitOffset = 300f;
    [Tooltip("Forward (+) or backward (-) offset of the side loop exit from the entry, along the " +
             "direction of travel. 0 = exit stacked directly over/under the entry, so the road " +
             "rejoins itself tangentially. Around the loop radius = the road crosses over itself " +
             "at an angle. At 2x the radius or more the loop no longer crosses itself.")]
    public float sideLoopExitForwardOffset = 700f;

    // -------------------------------------------------------
    //  Edge structure — one Bezier curve per edge, one mesh per edge
    // -------------------------------------------------------

    class TrackEdge
    {
        public Vector3 startPos;
        public Vector3 startDir;
        public Vector3 endPos;
        public Vector3 endDir;
        public float handleStart;
        public float handleEnd;
        public TrackEdge parent;
        public List<TrackEdge> children = new List<TrackEdge>();
        public List<Vector3> sampledPoints;

        // Loop-specific fields. If isLoop is true, sampledPoints is generated as
        // a circle around loopCenter with loopRadius, in the plane defined by
        // loopForward and the world-up rotation axis.
        public bool isLoop;
        public bool isSideLoop;               // loop laid flat (a 360° left/right turn); left untagged so it gets no loop boost
        public float loopFlattenStart = 1f;   // 1 = no exit-flattening
        public Vector3 loopCenter;
        public float loopRadius;
        public Vector3 loopForward;     // direction of car travel when entering the loop
        public List<Vector3> loopPoints;     // precomputed centerline for loops
        public List<Vector3> loopNormals;    // per-sample surface normals (banking)

        // Shoulders kept flat, with no rail — the inner sides of the two roads leaving a split. Right
        // and left as TrackMeshBuilder builds them (its rightSide flag).
        public bool flatShoulderRight, flatShoulderLeft;

        // How far the road is rolled about its direction of travel at each end (degrees), easing from
        // one to the other along the edge's length. Loops carry theirs in their points and normals
        // instead.
        public float rollStart, rollEnd;
    }

    private readonly List<TrackEdge> allEdges = new List<TrackEdge>();
    // The last edge of each start-to-finish path — the one that reaches the finish portal — left to right.
    private readonly List<TrackEdge> leafEdges = new List<TrackEdge>();
    // How many AI racer groups each start-to-finish path (one per leaf, in leafEdges order) has been
    // given so far — see SampleLeastOccupiedPath.
    private int[] pathGroupCounts = new int[0];
    private Vector3 trackStart;
    private Vector3 trackFinish;

    // The layout frame, set by PlanTrack. The launch straight runs from trackStart to launchEnd; from
    // there each path's progress (0..1) is measured along the level line toward the finish.
    private Vector3 launchEnd;
    private Vector3 axisDir;      // level, launchEnd -> finish
    private Vector3 sideAxis;     // level, square to axisDir (+ = right)
    private float axisLength;     // level distance launchEnd -> finish
    private readonly List<PathPlan> plans = new List<PathPlan>();        // in the order they split off
    private readonly List<PathPlan> leftToRight = new List<PathPlan>();  // the same paths, in lane order left to right

    /// <summary>The live generator (the TrackScene holds exactly one). MultiplayerWorld uses it to
    /// teleport portal-entering players onto the track it generated.</summary>
    public static TrackGenerator Current { get; private set; }

    /// <summary>Raised the moment a track is fully laid out (every road, loop, shoulder and the finish
    /// portal built), before the car is placed on it. TrackTerrain shapes the round's mountains around it.</summary>
    public static event System.Action<TrackGenerator> Generated;

    /// <summary>Where a car entering this track should be placed (the same pose the single-player
    /// spawn uses).</summary>
    public Vector3 CarSpawnPosition =>
        trackStart + Vector3.forward * carSpawnForwardOffset + Vector3.up * carSpawnHeightOffset;
    public Quaternion CarSpawnRotation => Quaternion.LookRotation(Vector3.forward);

    /// <summary>Applies this track's spawn boost to a car (public for MultiplayerWorld's teleport-in).</summary>
    public void ApplySpawnBoostTo(Rigidbody rb) => ApplySpawnBoost(rb, Vector3.forward, spawnVelocityMph);

    // -------------------------------------------------------
    //  Entry point
    // -------------------------------------------------------

    void Start()
    {
        Current = this;
        int resolved;
        if (GameLoopManager.Instance != null)
            resolved = GameLoopManager.Instance.GetNextTrackSeed();
        else
            resolved = (seed == 0) ? Random.Range(1, 999999) : seed;
        Random.InitState(resolved);
        Debug.Log($"[TrackGenerator] Seed: {resolved}");
        GenerateTrack();
    }

    void GenerateTrack()
    {
        allEdges.Clear();
        leafEdges.Clear();
        plans.Clear();
        leftToRight.Clear();

        // Build this track's road + shoulder materials (instances sharing one randomised hue)
        // before any road edge is made.
        PrepareTrackMaterials();

        // Roll the path altitude swing once per generation so every path shares the same
        // vertical character, but successive runs (different seeds) vary.
        float vDev = Random.Range(-convergenceAltitudeSwingVariance,
                                   convergenceAltitudeSwingVariance);
        currentConvergenceAltitudeSwing = Mathf.Max(0f, convergenceAltitudeSwing + vDev);
        float rDev = rollSwingVariance > 0f ? Random.Range(-rollSwingVariance, rollSwingVariance) : 0f;
        currentRollSwing = Mathf.Clamp(rollSwing + rDev, 0f, 90f);

        // Start position: world origin plus random offsets so each play has a
        // uniquely-located starting point. Altitude is positive-only — start never
        // goes below baseAltitude. In multiplayer the whole track generates in the
        // TRACK AREA (the additively-loaded scene's world offset, matching the offset
        // MultiplayerWorld applied to the scene's authored objects).
        Vector3 areaOrigin = MultiplayerWorld.IsMultiplayerGame ? MultiplayerWorld.TrackAreaOffset : Vector3.zero;
        float startX = Random.Range(-startLateralVariance, startLateralVariance);
        float startY = baseAltitude + Random.Range(0f, startAltitudeVariance);
        float startZ = Random.Range(-startForwardVariance, startForwardVariance);

        trackStart = areaOrigin + new Vector3(startX, startY, startZ);

        // Finish position: well ahead of the start but with random lateral, vertical,
        // and forward offsets so each play-through has a uniquely-located portal.
        // Forward offset is one-sided (+ only) — never bring the finish closer than
        // the safe minimum, only push it further ahead.
        float forwardDist = targetBranchLength * 1.1f
                          + Random.Range(0f, finishForwardVariance);

        float lateralOffset = Random.Range(-finishLateralVariance, finishLateralVariance);
        float altitudeOffset = Random.Range(-finishAltitudeVariance, finishAltitudeVariance);

        trackFinish = trackStart
                    + Vector3.forward * forwardDist
                    + Vector3.right * lateralOffset
                    + Vector3.up * altitudeOffset;

        // High enough above the floor for the path ends' up/down stagger across the portal.
        trackFinish.y = Mathf.Max(trackFinish.y, minAltitude + 0.5f * FinishStackHeight);

        // Phases 1-2 — plan where the paths split and run, then lay every one out, tuned to one
        // shared route length.
        PlanTrack();
        pathGroupCounts = new int[leafEdges.Count];   // no racer groups on any path yet

        // Phase 3 — mesh every edge
        foreach (var edge in allEdges) BuildEdgeMesh(edge);

        if (endPortalPrefab != null)
        {
            // Parented under the generator so it lives in the TrackScene (an un-parented Instantiate
            // goes to the ACTIVE scene — wrong in the multiplayer additive world) and is torn down
            // with the scene. It faces the way every path arrives, so their ends spread across its width.
            var portal = Instantiate(endPortalPrefab, trackFinish + Vector3.up * 2f, Quaternion.LookRotation(axisDir));
            portal.transform.SetParent(transform, true);
        }

        Generated?.Invoke(this);
        StartCoroutine(SpawnCarDelayed());
    }
    /// <summary>
    /// Picks a start-to-finish path for a new group of AI racers and returns its world-space
    /// centerline points. Groups are spread out: the path given the fewest groups so far wins (at
    /// random among ties), so every path gets a group before any path gets a second, and so on.
    /// Every spawner shares the one tally, so DroneCar and ChallengerCar groups balance out together.
    /// </summary>
    public List<Vector3> SampleLeastOccupiedPath()
    {
        if (leafEdges.Count == 0) return new List<Vector3>();
        if (pathGroupCounts.Length != leafEdges.Count) pathGroupCounts = new int[leafEdges.Count];

        int fewest = int.MaxValue, ties = 0;
        foreach (int count in pathGroupCounts)
        {
            if (count < fewest) { fewest = count; ties = 1; }
            else if (count == fewest) ties++;
        }

        int pick = Random.Range(0, ties);
        for (int i = 0; i < pathGroupCounts.Length; i++)
        {
            if (pathGroupCounts[i] != fewest || pick-- > 0) continue;
            pathGroupCounts[i]++;
            return SamplePathThrough(leafEdges[i]);
        }
        return new List<Vector3>();   // unreachable
    }

    /// <summary>
    /// Centerline points of the start-to-finish path that ends with `leaf` (each leaf is one path):
    /// every edge from the start down to it, through whichever splits lead there.
    /// </summary>
    List<Vector3> SamplePathThrough(TrackEdge leaf)
    {
        var chain = new List<TrackEdge>();
        for (TrackEdge e = leaf; e != null; e = e.parent) chain.Add(e);
        chain.Reverse();

        var points = new List<Vector3>();
        foreach (TrackEdge edge in chain)
        {
            if (edge.sampledPoints == null) continue;
            // Skip each later edge's first point — it repeats the previous edge's last one.
            int startIdx = (points.Count == 0) ? 0 : 1;
            for (int i = startIdx; i < edge.sampledPoints.Count; i++)
                points.Add(edge.sampledPoints[i]);
        }
        return points;
    }

    // -------------------------------------------------------
    //  Phase 1 — plan the paths
    // -------------------------------------------------------
    //
    // One path (the trunk) runs from the start to the finish; along the way any path can split off a
    // new one, which runs to the finish too. Every path ends at its own slot across the finish portal.
    //
    // Control points along a route are numbered 0..segmentsPerRoute: 0 is the start, 1 the end of
    // the launch straight, the last one a slot at the portal. A path that splits off at control
    // point j shares that numbering from j on, so "segment s" covers the same stretch of the track on
    // every path — which is what lets a branch be matched against the stretch of its parent it's the
    // alternative to.

    enum SegmentKind { Road, Loop, SideLoop }

    /// <summary>A loop's random choices, rolled once up front so the loop comes out the same however
    /// many times its path is laid out while its length is being tuned.</summary>
    struct LoopRoll
    {
        public float radius;
        public float exitOffset;   // sideways (loop) or up/down (side loop) gap between entry and exit
        public float sideRoll;     // which way it turns or slides (a coin flip)
        public float vertRoll;     // side loop: climb or descend
    }

    /// <summary>One start-to-finish path: the trunk, or a branch that splits off another path.</summary>
    class PathPlan
    {
        public PathPlan parent;          // the path it splits off; null for the trunk
        public int forkIndex = 1;        // control point it splits off its parent at (1 for the trunk)
        public int side;                 // +1 = splits off to its parent's right, -1 = left
        public int lastSplit;            // last control point it split at, either way
        public readonly List<int> splits = new List<int>();   // control points where branches leave it
        public float fadeIn;             // progress over which it eases out from its parent's lane onto its own
        public int rank;                 // its lane's place, left to right
        public float slot, slotHeight;   // sideways and up/down offset of its end at the portal
        public float layer;              // its altitude layer offset (each path its own, shuffled)
        public float[] along;            // progress 0..1 of each control point

        // Per control point, worked out once the branching is known: lane centre and altitude layer
        // offset — what the path winds around.
        public float[] lane, lift;

        // Style, rolled once.
        public float[] swingSide, swingVert, tension;
        public float[] roll;             // how far the road is rolled at each control point (degrees)
        public SegmentKind[] kinds;
        public LoopRoll[] loops;

        // Sections (see Phase 2): their boundary control points — its start, each split, the finish —
        // and the length and stretch each section has come out at.
        public readonly List<int> bounds = new List<int>();
        public readonly List<PathPlan> branches = new List<PathPlan>();   // the paths that split off it
        public float[] sectionLength, sectionStretch;

        // Layout, redone while the route length is being tuned.
        public Vector3[] points, dirs;
        public Vector3[] loopExits;      // where the loop starting at each control point comes out (if any)
        public float[] lateral;          // sideways offset of each control point from the axis
        public float[] routeLength;      // length of the route from the start to each control point
        public TrackEdge[] edgeInto;     // the edge that ends at each control point
        public TrackEdge finalEdge;

        public int LayoutStart => parent == null ? 1 : forkIndex;
    }

    const int DivergeLegs = 2;               // control points over which a new path is forced apart from its parent
    const float MaxSplitProgress = 0.7f;     // no splits past here — paths need room to separate before the finish funnel
    const float SwingScaleCap = 2f;         // a path's sideways swings stretch to at most twice their rolled size...
    const float MaxStretch = 4f;            // ...its hills to four times (see LayOutSection)
    const float MaxLaneSlope = 0.56f;        // a lane change averages no steeper than this (~30°, ~40° at its steepest)
    const float FinishStackHeight = 700f;    // the path ends' spread up and down the portal (its opening is 800 tall)
    const int LoopReturnLegs = 3;            // control points over which a path eases back onto its line after a loop
    const int MaxExtraLoops = 10;            // loops one split may add or take away to even up its two ways on

    float LaneShiftSpan => 3f / (segmentsPerRoute - 1);   // lane changes take about three segments
    float SegmentSpan => axisLength / (segmentsPerRoute - 1);   // average level length of a segment
    float LoopRunUp => 0.3f * SegmentSpan;                // straight run from a segment's start into its loop
    float MinBendRadius => 1.2f * (0.5f * roadWidth + shoulderWidth);   // tighter folds the road's inner edge
    const int MaxEasePasses = 10;            // rounds of opening up bends too tight for the road (see EaseTightBends)
    const int FitScanSteps = 8;              // stretches tried across the range before homing in (see FitSection)

    /// <summary>
    /// Plans and lays out every path: the launch straight, where paths split and which lanes they
    /// take, each path's swings and loops, and finally the road edges, every route tuned to one length.
    /// </summary>
    void PlanTrack()
    {
        int n = segmentsPerRoute;

        // The launch straight runs dead ahead along +Z, long enough that the car spawns square on it
        // with room to settle before the first bend.
        float launchLength = Mathf.Max(carSpawnForwardOffset + 1000f, targetBranchLength / n);
        launchEnd = trackStart + Vector3.forward * launchLength;

        Vector3 level = trackFinish - launchEnd;
        level.y = 0f;
        if (level.sqrMagnitude < 1f) level = Vector3.forward;
        axisLength = level.magnitude;
        axisDir = level / axisLength;
        sideAxis = Vector3.Cross(Vector3.up, axisDir).normalized;

        PlanBranches();
        RankPaths();
        foreach (var p in plans)
        {
            PlanGuides(p);
            RollPathStyle(p);
            PlanRoll(p);
            PlanLoops(p);
            p.bounds.Add(p.LayoutStart);
            p.bounds.AddRange(p.splits);
            p.bounds.Add(n);
        }
        LayOutAllPaths();
    }

    /// <summary>
    /// Decides where paths split. Walks the route from start to finish; at each control point every
    /// path there rolls Branch Chance to split off a new one, until Max Paths is reached. If the
    /// rolls fall short of Min Paths, extra splits are forced, spread over what's left of the route.
    ///
    /// A new path's lane opens up right beside its parent's, so the lanes fan out from the splits;
    /// the paths themselves wind freely across them, over and under one another.
    /// </summary>
    void PlanBranches()
    {
        int n = segmentsPerRoute;
        var trunk = new PathPlan { lastSplit = 1 };
        trunk.along = RollAlong(null, 1);
        plans.Add(trunk);
        leftToRight.Add(trunk);

        int cap = Mathf.Max(1, maxPaths);
        int floor = Mathf.Clamp(minPaths, 1, cap);
        const int first = 3;   // the trunk runs clear of the launch straight before anything splits off
        int last = Mathf.Min(n - 4, 1 + Mathf.FloorToInt(MaxSplitProgress * (n - 1)));

        var ready = new List<PathPlan>();
        for (int j = first; j <= last && plans.Count < cap; j++)
        {
            // At least three segments between splits on any one path, so a new branch has peeled clear
            // (see PullApart) before the next split along either of them.
            ready.Clear();
            foreach (var p in plans)
                if (p.forkIndex < j && j - p.lastSplit >= 3) ready.Add(p);
            Shuffle(ready);

            foreach (var p in ready)
            {
                if (plans.Count >= cap) break;
                if (Random.value < branchChance) SplitPath(p, j);
            }

            // Behind on Min Paths: force a split here, more likely the further behind with less track left.
            int deficit = floor - plans.Count;
            if (deficit <= 0 || Random.value >= deficit / (float)(last - j + 1)) continue;
            ready.RemoveAll(p => p.lastSplit == j);
            if (ready.Count > 0) SplitPath(ready[Random.Range(0, ready.Count)], j);
        }
    }

    /// <summary>
    /// Splits a new path off `p` at control point j, to its left or right at random. It eases out
    /// onto a lane of its own while the paths around make room (see LaneCentre) — across the next few
    /// segments, or further for a big move, so no lane change is steeper than MaxLaneSlope.
    /// </summary>
    void SplitPath(PathPlan p, int j)
    {
        int side = Random.value < 0.5f ? -1 : 1;
        var branch = new PathPlan { parent = p, forkIndex = j, side = side, lastSplit = j };
        leftToRight.Insert(leftToRight.IndexOf(p) + (side > 0 ? 1 : 0), branch);

        // The two end up a lane apart (Branch Spread shared by every path), each moving about half that.
        float u0 = p.along[j];
        float move = 0.5f * branchSpread / (plans.Count + 1);
        branch.fadeIn = Mathf.Min(1f - u0, Mathf.Max(LaneShiftSpan, move / (MaxLaneSlope * axisLength)));
        branch.along = RollAlong(p, j);

        p.lastSplit = j;
        p.splits.Add(j);
        p.branches.Add(branch);
        plans.Add(branch);
    }

    /// <summary>
    /// Progress (0..1) of each control point, spaced unevenly — each gap 70-130% of the average — so
    /// the rhythm of the swings changes from stretch to stretch. A branch keeps its parent's spacing
    /// through the split and its first legs (so the two line up while they separate), then rolls its own.
    /// </summary>
    float[] RollAlong(PathPlan parent, int forkIndex)
    {
        int n = segmentsPerRoute;
        var along = new float[n + 1];
        int kept = 1;   // control points 0 and 1 (start, end of the launch straight) are both at 0
        if (parent != null)
        {
            kept = Mathf.Min(forkIndex + DivergeLegs, n);
            for (int i = 0; i <= kept; i++) along[i] = parent.along[i];
        }
        if (kept >= n) return along;

        var gaps = new float[n + 1];
        float total = 0f;
        for (int i = kept + 1; i <= n; i++) total += gaps[i] = Random.Range(0.7f, 1.3f);
        float span = 1f - along[kept];
        for (int i = kept + 1; i <= n; i++) along[i] = along[i - 1] + span * gaps[i] / total;
        along[n] = 1f;
        return along;
    }

    /// <summary>How fully a path is out on a lane of its own at progress u: 0 until it splits off,
    /// easing to 1 across its fade-in.</summary>
    static float Presence(PathPlan p, float u) =>
        p.parent == null ? 1f : Smooth01((u - p.along[p.forkIndex]) / p.fadeIn);

    /// <summary>
    /// A path's lane centre at progress u, as a fraction of Branch Spread (-0.5..0.5) — the line it
    /// winds around. Every path out at that point gets an equal share of the width, side by side, so
    /// the paths fan out across all of it; when a new path splits off its lane eases out from its
    /// parent's while the others spread out to make room.
    /// </summary>
    float LaneCentre(PathPlan p, float u)
    {
        float total = 0f, before = 0f, mine = Presence(p, u);
        foreach (var q in plans)
        {
            float w = Presence(q, u);
            total += w;
            if (q.rank < p.rank) before += w;
        }
        float lane = (before + 0.5f * mine) / total - 0.5f;
        return mine >= 1f ? lane : Mathf.Lerp(LaneCentre(p.parent, u), lane, mine);
    }

    /// <summary>How far the lanes fan out at progress u: from nothing at the start, to full width
    /// through the middle, and back to nothing at the finish so every path funnels into the portal.</summary>
    static float LaneEnvelope(float u) => Smooth01(u / 0.3f) * Smooth01((1f - u) / 0.35f);

    /// <summary>How far each path has eased onto its own slot at the portal by progress u — the other
    /// side of the funnel: nothing until 65% of the way, then all the way by the finish.</summary>
    static float FinishBlend(float u) => 1f - Smooth01((1f - u) / 0.35f);

    /// <summary>A path's lane at progress u, sideways from the axis (units): fanned out across Branch
    /// Spread through the middle, then easing onto its slot at the portal.</summary>
    float LaneAt(PathPlan p, float u)
    {
        if (p.parent != null && u < p.along[p.forkIndex]) return LaneAt(p.parent, u);
        return LaneCentre(p, u) * branchSpread * LaneEnvelope(u) + p.slot * FinishBlend(u);
    }

    /// <summary>A path's height above the axis at progress u from its altitude layer, easing onto its
    /// slot's height at the portal.</summary>
    float LiftAt(PathPlan p, float u)
    {
        if (p.parent != null && u < p.along[p.forkIndex]) return LiftAt(p.parent, u);
        return LayerAt(p, u) * LaneEnvelope(u) + p.slotHeight * FinishBlend(u);
    }

    static float Smooth01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    /// <summary>A path's altitude layer at progress u (before the envelope): a branch eases from its
    /// parent's layer onto its own as it splits off.</summary>
    float LayerAt(PathPlan p, float u)
    {
        if (p.parent == null) return p.layer;
        float u0 = p.along[p.forkIndex];
        if (u <= u0) return LayerAt(p.parent, u);
        return Mathf.Lerp(LayerAt(p.parent, u0), p.layer, Presence(p, u));
    }

    /// <summary>
    /// Numbers the lanes left to right, then gives every path an altitude layer and a place across the
    /// portal — each shuffled, so how high a path runs and where it ends up at the finish have nothing
    /// to do with the lane it fanned out on. Its end also sits at its own height in the portal, in the
    /// same order as the layers but squeezed to fit the opening, so paths crossing on the way in always
    /// pass at different heights.
    /// </summary>
    void RankPaths()
    {
        int count = leftToRight.Count;
        for (int r = 0; r < count; r++) leftToRight[r].rank = r;

        var byLayer = new List<PathPlan>(leftToRight);
        var bySlot = new List<PathPlan>(leftToRight);
        Shuffle(byLayer);
        Shuffle(bySlot);

        float endStep = Mathf.Min(altitudeLayerSpacing, FinishStackHeight / Mathf.Max(1, count - 1));
        for (int r = 0; r < count; r++)
        {
            float t = count == 1 ? 0f : r / (float)(count - 1) * 2f - 1f;   // -1 to +1
            byLayer[r].layer = t * altitudeLayerSpacing * (count - 1) * 0.5f;
            byLayer[r].slotHeight = t * endStep * (count - 1) * 0.5f;
            bySlot[r].slot = t * finishLateralSpacing * (count - 1) * 0.5f;
        }
    }

    /// <summary>Works out a path's lane and height offset at each control point.</summary>
    void PlanGuides(PathPlan p)
    {
        int n = segmentsPerRoute;
        p.lane = new float[n + 1];
        p.lift = new float[n + 1];
        for (int i = p.LayoutStart; i <= n; i++)
        {
            p.lane[i] = LaneAt(p, p.along[i]);
            p.lift[i] = LiftAt(p, p.along[i]);
        }
    }

    /// <summary>
    /// Rolls a path's own winding: strung together from short swing sections (see FillSwingProfile)
    /// plus a whole-path bow, sideways and up/down independently, at its own overall scale — some
    /// paths wide and wild, others calmer. Stored unscaled; LayOutSection scales and limits them.
    /// </summary>
    void RollPathStyle(PathPlan p)
    {
        int n = segmentsPerRoute;
        int start = p.LayoutStart;
        int m = n - start;   // segments laid out from here

        var along = new float[m + 1];
        float u0 = p.along[start];
        for (int l = 0; l <= m; l++) along[l] = (p.along[start + l] - u0) / Mathf.Max(1f - u0, 1e-4f);

        float sideScale = convergenceSwing * Random.Range(0.6f, 1.3f);
        float vertScale = currentConvergenceAltitudeSwing * Random.Range(0.15f, 0.45f);
        var side = new float[m + 1];
        var vert = new float[m + 1];
        var tension = new float[m + 1];
        for (int l = 0; l <= m; l++) tension[l] = 0.45f;   // the ends keep the old default
        FillSwingProfile(side, sideScale, tension);
        FillSwingProfile(vert, vertScale, null);
        AddPathBow(side, along, sideScale);
        AddPathBow(vert, along, vertScale);

        // A gentle up-and-down underneath the hills, so no stretch of the path is ever dead level —
        // scaling the hills up (to stretch a route, see LayOutSection) then always adds road.
        float roll = Random.value < 0.5f ? -1f : 1f;
        for (int l = 1; l < m; l++, roll = -roll) vert[l] += roll * 0.15f * vertScale;

        p.swingSide = new float[n + 1];
        p.swingVert = new float[n + 1];
        p.tension = new float[n + 1];
        for (int i = 0; i <= n; i++) p.tension[i] = 0.45f;
        for (int l = 0; l <= m; l++)
        {
            p.swingSide[start + l] = side[l];
            p.swingVert[start + l] = vert[l];
            p.tension[start + l] = tension[l];
        }
    }

    /// <summary>
    /// Rolls how far a path's road rolls over at each control point: a run of stretches, each held
    /// for a few segments — some level, some banked part of the way, some right over to Roll Swing —
    /// either way at random, flat off the launch and levelling out into the portal. A branch starts
    /// out at its parent's roll where it splits off. Each point is kept within reach of its
    /// neighbours at Max Roll Rate, so the road eases from one stretch's roll to the next gently.
    /// </summary>
    void PlanRoll(PathPlan p)
    {
        int n = segmentsPerRoute, start = p.LayoutStart;
        p.roll = new float[n + 1];
        if (p.parent != null) p.roll[start] = p.parent.roll[start];
        if (currentRollSwing <= 0f && p.roll[start] == 0f) return;

        float wanted = 0f;
        for (int i = start + 1, held = 0; i < n; i++, held--)
        {
            if (held <= 0)
            {
                // The next stretch: a quarter level, a quarter right over, the rest in between.
                float pick = Random.value;
                float lean = pick < 0.25f ? 0f : pick < 0.5f ? 1f : Random.Range(0.3f, 1f);
                wanted = lean * currentRollSwing * (Random.value < 0.5f ? -1f : 1f);
                held = Random.Range(3, 7);
            }
            float u = p.along[i];
            float taper = Smooth01(u / 0.08f) * Smooth01((1f - u) / 0.15f);
            float step = MaxRollStep(p, i);
            p.roll[i] = Mathf.Clamp(wanted * taper, p.roll[i - 1] - step, p.roll[i - 1] + step);
        }
        // Flat at the portal (roll[n] = 0): back from there, so every point can still level out in time.
        for (int i = n - 1; i > start; i--)
        {
            float step = MaxRollStep(p, i + 1);
            p.roll[i] = Mathf.Clamp(p.roll[i], p.roll[i + 1] - step, p.roll[i + 1] + step);
        }
    }

    /// <summary>The most the roll may change from control point i-1 to i: Max Roll Rate over the least
    /// road that segment can roll along — its level length less a loop's run-up (a loop keeps the roll
    /// it went in with), and at most 60% of it, as easing a tight bend open (see EaseTightBends) can
    /// draw a segment's ends together — and eased in and out (see RollAt), whose quickest moment is
    /// 1.5x its average.</summary>
    float MaxRollStep(PathPlan p, int i)
    {
        float level = (p.along[i] - p.along[i - 1]) * axisLength;
        float least = Mathf.Max(Mathf.Min(level - LoopRunUp, 0.6f * level), 0.3f * level);
        return maxRollRate / 100f * least / 1.5f;
    }

    /// <summary>The roll t (0..1) of the way along an edge rolled `from` at its start and `to` at its
    /// end: eased in and out, so it rolls smoothly on from the edges either side. (The mesh measures t
    /// along the road's length; the bend checks, near enough, along the curve.)</summary>
    static float RollAt(float from, float to, float t) => Mathf.Lerp(from, to, Smooth01(t));

    /// <summary>
    /// Decides which of a path's own segments become loops or side loops: every path rolls Loop Chance
    /// and Side Loop Chance on each of its segments, regardless of the others. (Where that leaves the
    /// two ways on from a split too far apart in length, the layout adds, resizes or removes loops —
    /// see EvenUpWithLoops.)
    /// </summary>
    void PlanLoops(PathPlan p)
    {
        int n = segmentsPerRoute;
        p.kinds = new SegmentKind[n];
        p.loops = new LoopRoll[n];

        for (int s = 0; s < n; s++)
        {
            if (!CanLoop(p, s)) continue;
            bool wantsLoop = Random.value < loopChance;
            // Side loops roll independently; skipped entirely while disabled.
            bool wantsSideLoop = sideLoopChance > 0f && Random.value < sideLoopChance;
            // Both claim this segment: 50/50 which one forms.
            if (wantsLoop && wantsSideLoop)
            {
                if (Random.value < 0.5f) wantsSideLoop = false;
                else wantsLoop = false;
            }
            if (!wantsLoop && !wantsSideLoop) continue;
            p.kinds[s] = wantsSideLoop ? SegmentKind.SideLoop : SegmentKind.Loop;
            p.loops[s] = RollLoop(wantsSideLoop);
            if (wantsLoop)
            {
                // Which way the path's lane and swings head through here, as rolled.
                float sideways = p.lane[s + 1] + p.swingSide[s + 1] - p.lane[s - 1] - p.swingSide[s - 1];
                float forward = (p.along[s + 1] - p.along[s - 1]) * axisLength;
                SlideAwayFromFinish(ref p.loops[s], sideways / Mathf.Max(forward, 1f));
            }
        }
    }

    /// <summary>
    /// Whether segment s of `p` may hold a loop: not the launch straight or the stretch right after
    /// it, not a new path's first legs (it's still peeling away from its parent) or the segments
    /// either side of a split, and not the last two segments, where the paths funnel together into
    /// the portal. A loop shifts the road after it (see LayOutSection), so these keep that shift off
    /// the anchors and the legs that set the split angles.
    /// </summary>
    bool CanLoop(PathPlan p, int s)
    {
        int first = p.parent == null ? 2 : p.forkIndex + DivergeLegs;
        if (s < first || s > segmentsPerRoute - 3) return false;
        foreach (int j in p.splits)
            if (s >= j - 1 && s <= j + 1) return false;
        return true;
    }

    LoopRoll RollLoop(bool sideLoop) => new LoopRoll
    {
        radius = sideLoop ? Random.Range(minSideLoopRadius, maxSideLoopRadius)
                          : Random.Range(minLoopRadius, maxLoopRadius),
        exitOffset = sideLoop ? Random.Range(minSideLoopExitOffset, maxSideLoopExitOffset)
                              : Random.Range(minLoopExitOffset, maxLoopExitOffset),
        sideRoll = Random.value,
        vertRoll = Random.value,
    };

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int k = Random.Range(0, i + 1);
            (list[i], list[k]) = (list[k], list[i]);
        }
    }

    // -------------------------------------------------------
    //  Phase 2 — lay the paths out
    // -------------------------------------------------------
    //
    // A path's route breaks into sections at its start, at each control point where a branch leaves
    // it, and at the finish. Those boundary points (anchors) sit exactly on the path's lane and
    // altitude layer, whatever its swings do, so every section can be laid out — and stretched or
    // calmed — on its own. That's what lets each split be balanced exactly: the two ways on from it
    // are tuned to the same length.

    /// <summary>
    /// Lays out every path so that, whichever branches a player takes, it's the same distance to the
    /// finish. Works back from the latest splits (see SettleBranchesOf); the trunk's first section,
    /// the one stretch no split depends on, keeps its swings as rolled and sets the route length.
    /// Paths are laid out with no regard for each other — they cross over, under and through one
    /// another freely. Finally every edge joins the track.
    /// </summary>
    void LayOutAllPaths()
    {
        int n = segmentsPerRoute;
        foreach (var p in plans) PlaceAnchors(p);

        PathPlan trunk = plans[0];
        SettleBranchesOf(trunk);
        SetSection(trunk, 0, 1f);

        foreach (var p in plans) CommitPath(p);

        float routeLength = trunk.routeLength[n], worst = 0f;
        int loops = 0, sideLoops = 0;
        foreach (var p in plans)
        {
            worst = Mathf.Max(worst, Mathf.Abs(p.routeLength[n] - routeLength) / routeLength);
            for (int s = p.LayoutStart; s < n; s++)
            {
                if (p.kinds[s] == SegmentKind.Loop) loops++;
                else if (p.kinds[s] == SegmentKind.SideLoop) sideLoops++;
            }
        }

        foreach (var p in leftToRight) leafEdges.Add(p.finalEdge);

        Debug.Log($"[TrackGenerator] {plans.Count} path(s), {loops} loop(s), {sideLoops} side loop(s); " +
                  $"every route {routeLength:F0} units long (within {worst * 100f:F1}%).");
    }

    /// <summary>
    /// Fixes every section of `p` from section `from` on, and every branch leaving it there (with all
    /// of theirs), so that at each split the road on along `p` and the road down the branch are the
    /// same length. Goes from the last split back: at each, the branch's later sections are settled
    /// first, then the two ways on meet in the middle — each starts from its swings as rolled and
    /// gives half the difference (see FitFrom) — falling back on whichever side can still give when the
    /// other can't. If the two are too far apart for that (one rolled several more loops, or fans out
    /// much wider), the shorter way gets an extra loop, or a loop on either way is resized or taken out
    /// (see EvenUpWithLoops), and they're fitted again.
    /// </summary>
    void SettleBranchesOf(PathPlan p, int from = 1)
    {
        for (int m = p.bounds.Count - 2; m >= from; m--)
        {
            PathPlan branch = BranchAt(p, m);
            SettleBranchesOf(branch);

            for (int extra = 0; ; extra++)
            {
                // Lay this section out first: the branch's first legs peel away from it.
                float mine = SetSection(p, m, 1f) + RoadAhead(p, m + 1);
                float theirs = SetSection(branch, 0, 1f) + RoadAhead(branch, 1);
                float target = 0.5f * (mine + theirs);
                float gotMine = FitFrom(p, m, target);
                float gotTheirs = FitFrom(branch, 0, target);
                // The branch's first legs follow this road's layout, so it can take a few rounds to settle.
                for (int pass = 0; pass < 6 && Mathf.Abs(gotTheirs - gotMine) > 0.0005f * target; pass++)
                {
                    gotMine = FitFrom(p, m, gotTheirs);
                    gotTheirs = FitFrom(branch, 0, gotMine);
                }

                if (Mathf.Abs(gotTheirs - gotMine) <= 0.005f * target || extra == MaxExtraLoops) break;
                if (!EvenUpWithLoops(p, m, branch)) break;
            }
        }
    }

    /// <summary>
    /// For when the two ways on from `p`'s split at section m can't be fitted to one length: changes
    /// the loops so they can — a loop more down the shorter way, or failing that a smaller one (or none)
    /// down the longer, or a bigger one down the shorter — sized to bring their lengths within reach of
    /// each other without overshooting. Returns false if no such change can be made.
    /// </summary>
    bool EvenUpWithLoops(PathPlan p, int m, PathPlan branch)
    {
        var (mineLo, mineHi) = Reach(p, m);
        var (theirLo, theirHi) = Reach(branch, 0);
        bool mineShort = mineHi < theirLo;
        if (!mineShort && theirHi >= mineLo) return false;   // already within reach: loops won't help

        float need = mineShort ? theirLo - mineHi : mineLo - theirHi;   // the least that closes the gap
        float most = mineShort ? theirHi - mineLo : mineHi - theirLo;   // the most that doesn't overshoot
        PathPlan shorter = mineShort ? p : branch, longer = mineShort ? branch : p;
        int shorterK = mineShort ? m : 0, longerK = mineShort ? 0 : m;
        return TopUpLoops(shorter, shorterK, need, most)
            || ReshapeLoops(longer, longerK, need, most, grow: false)
            || ReshapeLoops(shorter, shorterK, need, most, grow: true);
    }

    /// <summary>The shortest and longest `q`'s road from the start of its section k can come out by
    /// stretching that section alone. Leaves the section as it was.</summary>
    (float lo, float hi) Reach(PathPlan q, int k)
    {
        float was = q.sectionStretch[k], ahead = RoadAhead(q, k + 1);
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i <= FitScanSteps; i++)
        {
            float length = SetSection(q, k, MaxStretch * i / FitScanSteps) + ahead;
            lo = Mathf.Min(lo, length);
            hi = Mathf.Max(hi, length);
        }
        SetSection(q, k, was);
        return (lo, hi);
    }

    /// <summary>
    /// Adds a loop to every route through `q`'s road from the start of its section k to the finish,
    /// adding at least `need` and at most `most` to each, as near the middle as its size allows: one
    /// loop on that section if it has a free segment, otherwise one down each way on from its next
    /// split (and so on). The kind is rolled by Loop Chance and Side Loop Chance (the other kind if
    /// that one can't fit); every copy shares the size, but turns or slides its own way. Afterwards
    /// the splits it reached past are balanced again. Returns false if no loop fits, there's no room,
    /// or loops are switched off.
    /// </summary>
    bool TopUpLoops(PathPlan q, int k, float need, float most)
    {
        float odds = loopChance + sideLoopChance;
        if (odds <= 0f) return false;

        bool sideLoop = Random.value * odds < sideLoopChance;
        LoopRoll roll = RollLoop(sideLoop);
        roll.radius = RadiusForExtra(sideLoop, roll, 0.5f * (need + most));
        // A section shrinks its loops as it calms (see LoopRadiusAt), so what counts is how small it can go.
        if (LoopExtraAt(sideLoop, roll, 0f) > most)
        {
            bool other = !sideLoop;
            if ((other ? sideLoopChance : loopChance) <= 0f) return false;
            LoopRoll second = RollLoop(other);
            second.radius = RadiusForExtra(other, second, 0.5f * (need + most));
            if (LoopExtraAt(other, second, 0f) > most) return false;   // even the smallest overshoots
            sideLoop = other;
            roll = second;
        }

        var room = new List<(PathPlan path, int segment)>();
        if (!FindLoopRoom(q, k, room)) return false;
        foreach (var (path, segment) in room)
        {
            path.kinds[segment] = sideLoop ? SegmentKind.SideLoop : SegmentKind.Loop;
            path.loops[segment] = roll;
            path.loops[segment].sideRoll = Random.value;
            path.loops[segment].vertRoll = Random.value;
            Vector3 heading = path.dirs[segment];
            if (!sideLoop)
                SlideAwayFromFinish(ref path.loops[segment],
                                    Vector3.Dot(heading, sideAxis) / Mathf.Max(Vector3.Dot(heading, axisDir), 0.05f));
        }
        if (room.Count > 1 || room[0].path != q) SettleBranchesOf(q, k + 1);
        return true;
    }

    /// <summary>Picks free segments for a new loop so that every route through `q`'s road from its
    /// section k on passes exactly one (see TopUpLoops).</summary>
    bool FindLoopRoom(PathPlan q, int k, List<(PathPlan path, int segment)> room)
    {
        var free = new List<int>();
        for (int s = q.bounds[k]; s < q.bounds[k + 1]; s++)
            if (q.kinds[s] == SegmentKind.Road && CanLoop(q, s)) free.Add(s);
        if (free.Count > 0)
        {
            room.Add((q, free[Random.Range(0, free.Count)]));
            return true;
        }
        if (k + 2 >= q.bounds.Count) return false;   // this was the last section
        return FindLoopRoom(q, k + 1, room) && FindLoopRoom(BranchAt(q, k + 1), 0, room);
    }

    /// <summary>
    /// Reshapes one loop on every route through `q`'s road from the start of its section k to the
    /// finish — on that section if it has one, otherwise one down each way on from its next split (and
    /// so on) — so each route comes out longer (`grow`) or shorter by between `need` and `most`, as
    /// near the middle as it can: a new radius or exit offset within their ranges, the other kind of
    /// loop, or (shortening) no loop at all. Then balances again the splits it reached past. Returns
    /// false if some route there has no loop that can change by that much.
    /// </summary>
    bool ReshapeLoops(PathPlan q, int k, float need, float most, bool grow)
    {
        var found = new List<(PathPlan path, int segment, SegmentKind kind, LoopRoll roll)>();
        if (!FindLoopToReshape(q, k, need, most, grow, found)) return false;
        foreach (var (path, segment, kind, roll) in found)
        {
            bool turnedUpright = kind == SegmentKind.Loop && path.kinds[segment] != SegmentKind.Loop;
            path.kinds[segment] = kind;
            path.loops[segment] = roll;
            if (turnedUpright)
            {
                Vector3 heading = path.dirs[segment];
                SlideAwayFromFinish(ref path.loops[segment],
                                    Vector3.Dot(heading, sideAxis) / Mathf.Max(Vector3.Dot(heading, axisDir), 0.05f));
            }
        }
        if (found.Count > 1 || found[0].path != q) SettleBranchesOf(q, k + 1);
        return true;
    }

    /// <remarks>Sizes are judged at the stretch the section is laid out at (see LoopRadiusAt), and a
    /// change that only closes part of the gap is taken if nothing closes all of it.</remarks>
    bool FindLoopToReshape(PathPlan q, int k, float need, float most, bool grow,
                           List<(PathPlan path, int segment, SegmentKind kind, LoopRoll roll)> found)
    {
        float aim = 0.5f * (need + most), stretch = q.sectionStretch[k];
        float bestScore = float.MaxValue;
        var best = (path: q, segment: -1, kind: SegmentKind.Road, roll: default(LoopRoll));
        for (int s = q.bounds[k]; s < q.bounds[k + 1]; s++)
        {
            if (q.kinds[s] == SegmentKind.Road) continue;
            bool wasSide = q.kinds[s] == SegmentKind.SideLoop;
            float now = LoopExtraAt(wasSide, q.loops[s], stretch);
            if (!grow) Consider(s, SegmentKind.Road, q.loops[s], now);   // taking it out
            foreach (bool sideLoop in new[] { false, true })
            {
                if ((sideLoop ? sideLoopChance : loopChance) <= 0f) continue;
                float lo = sideLoop ? minSideLoopExitOffset : minLoopExitOffset;
                float hi = sideLoop ? maxSideLoopExitOffset : maxLoopExitOffset;
                foreach (float exit in new[] { lo, 0.5f * (lo + hi), hi })
                {
                    LoopRoll roll = q.loops[s];
                    roll.exitOffset = exit;
                    roll.radius = Mathf.Clamp(roll.radius, sideLoop ? minSideLoopRadius : minLoopRadius,
                                              sideLoop ? maxSideLoopRadius : maxLoopRadius);
                    float extra = LoopExtraAt(sideLoop, roll, stretch);
                    Consider(s, sideLoop ? SegmentKind.SideLoop : SegmentKind.Loop, roll, grow ? extra - now : now - extra);
                }
            }
        }
        if (best.segment >= 0)
        {
            found.Add(best);
            return true;
        }
        if (k + 2 >= q.bounds.Count) return false;   // this was the last section
        return FindLoopToReshape(q, k + 1, need, most, grow, found)
            && FindLoopToReshape(BranchAt(q, k + 1), 0, need, most, grow, found);

        // Best: the change nearest the middle of [need, most]; short of need, the biggest one going.
        void Consider(int segment, SegmentKind kind, LoopRoll roll, float change)
        {
            if (change > most || change < 0.25f * need) return;
            float score = change >= need ? Mathf.Abs(change - aim) : most + need - change;
            if (score >= bestScore) return;
            bestScore = score;
            best = (q, segment, kind, roll);
        }
    }

    /// <summary>About how much a loop adds to its route in a section laid out at `stretch`.</summary>
    float LoopExtraAt(bool sideLoop, LoopRoll roll, float stretch)
    {
        roll.radius = LoopRadiusAt(sideLoop, roll.radius, stretch);
        return LoopExtra(sideLoop, roll);
    }

    /// <summary>
    /// About how much a loop adds to its route: its road is close to a circle stretched out by its
    /// exit offset, and the path eases back from a sideways offset over the next few segments (see
    /// LayOutSection), which takes a little more.
    /// </summary>
    float LoopExtra(bool sideLoop, LoopRoll roll)
    {
        float circle = 2f * Mathf.PI * roll.radius;
        return Mathf.Sqrt(circle * circle + roll.exitOffset * roll.exitOffset) + EaseBack(sideLoop, roll);
    }

    float EaseBack(bool sideLoop, LoopRoll roll)
    {
        if (sideLoop) return 0f;
        float back = LoopReturnLegs * SegmentSpan;
        return Mathf.Sqrt(back * back + roll.exitOffset * roll.exitOffset) - back;
    }

    /// <summary>The radius that makes a loop add about `want` to its route (see LoopExtra), clamped to
    /// the loop's radius range.</summary>
    float RadiusForExtra(bool sideLoop, LoopRoll roll, float want)
    {
        float lo = sideLoop ? minSideLoopRadius : minLoopRadius;
        float hi = sideLoop ? maxSideLoopRadius : maxLoopRadius;
        float stretched = want - EaseBack(sideLoop, roll), drift = roll.exitOffset;
        float circle = stretched > drift ? Mathf.Sqrt(stretched * stretched - drift * drift) : 0f;
        return Mathf.Clamp(circle / (2f * Mathf.PI), lo, hi);
    }

    /// <summary>
    /// Makes `q`'s road from the start of its section k to the finish come out `length` long, keeping
    /// every split further along balanced. Section k gives first; whatever it can't take, the two ways
    /// on from the next split take together — each in turn the same way, so the change can reach as
    /// deep into the branches beyond as it needs to (unless the two can't end up level, when they're
    /// left as they were). Returns the length it got.
    /// </summary>
    float FitFrom(PathPlan q, int k, float length)
    {
        float ahead = RoadAhead(q, k + 1);
        FitSection(q, k, length - ahead);
        float miss = length - RoadAhead(q, k);
        if (k + 1 < q.sectionLength.Length && Mathf.Abs(miss) > 0.0005f * length)
        {
            PathPlan branch = BranchAt(q, k + 1);
            float branchRoad = RoadAhead(branch, 0);
            var before = new List<(PathPlan path, int section, float stretch)>();
            SaveStretches(q, k + 1, before);

            float mine = FitFrom(q, k + 1, ahead + miss) - ahead;
            float theirs = FitFrom(branch, 0, branchRoad + mine) - branchRoad;
            if (Mathf.Abs(theirs - mine) > 1f)
            {
                // The branch couldn't follow all the way: settle for what it managed, on both.
                mine = FitFrom(q, k + 1, ahead + theirs) - ahead;
                theirs = FitFrom(branch, 0, branchRoad + mine) - branchRoad;   // its first legs follow q's final layout
            }
            if (Mathf.Abs(theirs - mine) > 0.0005f * length)
                foreach (var (path, section, stretch) in before) SetSection(path, section, stretch);
        }
        return RoadAhead(q, k);
    }

    /// <summary>Notes the stretch of every section of `q`'s road from its section k on, and of every
    /// path branching off there (and theirs), parents before branches — the order to lay them back out in.</summary>
    static void SaveStretches(PathPlan q, int k, List<(PathPlan path, int section, float stretch)> into)
    {
        for (int m = k; m < q.sectionStretch.Length; m++) into.Add((q, m, q.sectionStretch[m]));
        for (int m = Mathf.Max(k, 1); m < q.sectionStretch.Length; m++) SaveStretches(BranchAt(q, m), 0, into);
    }

    static PathPlan BranchAt(PathPlan p, int m) => p.branches.Find(b => b.forkIndex == p.bounds[m]);

    /// <summary>Length of `p`'s road from the start of section m to the finish (sections already set).</summary>
    static float RoadAhead(PathPlan p, int m)
    {
        float length = 0f;
        for (int k = m; k < p.sectionLength.Length; k++) length += p.sectionLength[k];
        return length;
    }

    /// <summary>
    /// The stretch that makes `p`'s section m come out `length` long, laid out at it. More stretch
    /// usually means more road, but not always — a loop's offset and a bend eased open can work
    /// against the swings — so it scans the whole range for where the length crosses `length`, takes
    /// the crossing nearest the swings as rolled (stretch 1) and homes in on it by bisection. If none
    /// gets there, it settles for the nearest. Returns the length it got.
    /// </summary>
    float FitSection(PathPlan p, int m, float length)
    {
        float lo = -1f, hi = -1f, closest = 1f, closestMiss = float.MaxValue;
        float previous = 0f, previousMiss = 0f;
        for (int i = 0; i <= FitScanSteps; i++)
        {
            float stretch = MaxStretch * i / FitScanSteps;
            float miss = SetSection(p, m, stretch) - length;
            if (Mathf.Abs(miss) < closestMiss) { closestMiss = Mathf.Abs(miss); closest = stretch; }
            bool crosses = i > 0 && (previousMiss < 0f) != (miss < 0f);
            if (crosses && (lo < 0f || Mathf.Abs(previous + stretch - 2f) < Mathf.Abs(lo + hi - 2f))) { lo = previous; hi = stretch; }
            previous = stretch;
            previousMiss = miss;
        }
        if (lo < 0f) return SetSection(p, m, closest);

        bool loShort = SetSection(p, m, lo) < length;
        for (int i = 0; i < 14; i++)
        {
            float mid = 0.5f * (lo + hi);
            if ((SetSection(p, m, mid) < length) == loShort) lo = mid;
            else hi = mid;
        }
        return SetSection(p, m, 0.5f * (lo + hi));
    }

    /// <summary>Lays out `p`'s section m at `stretch` and measures it.</summary>
    float SetSection(PathPlan p, int m, float stretch)
    {
        LayOutSection(p, m, stretch);
        p.sectionStretch[m] = stretch;
        return p.sectionLength[m] = BuildSectionEdges(p, m, null, commit: false);
    }

    /// <summary>
    /// Sets the anchors' positions and directions — the start, each split, and the end on its slot
    /// across the portal — straight from the path's lane and layer. A branch's start is its parent's
    /// anchor at the split.
    /// </summary>
    void PlaceAnchors(PathPlan p)
    {
        int n = segmentsPerRoute;
        p.points = new Vector3[n + 1];
        p.dirs = new Vector3[n + 1];
        p.loopExits = new Vector3[n + 1];
        p.lateral = new float[n + 1];
        p.routeLength = new float[n + 1];
        p.edgeInto = new TrackEdge[n + 1];
        p.sectionLength = new float[p.bounds.Count - 1];
        p.sectionStretch = new float[p.bounds.Count - 1];

        foreach (int i in p.bounds)
        {
            if (i == n)
            {
                p.points[n] = trackFinish + sideAxis * p.slot + Vector3.up * p.slotHeight;
                p.points[n].y = Mathf.Max(p.points[n].y, minAltitude);
                // In along the track's overall climb or descent, so the last stretch runs straight in
                // instead of every path levelling off — each its own way — just short of the portal.
                p.dirs[n] = (trackFinish - launchEnd).normalized;
                p.lateral[n] = p.slot;
            }
            else if (p.parent == null && i == 1)
            {
                p.points[0] = trackStart;
                p.points[1] = launchEnd;
                p.dirs[0] = p.dirs[1] = Vector3.forward;
            }
            else if (i == p.forkIndex)
            {
                p.points[i] = p.parent.points[i];
                p.dirs[i] = p.parent.dirs[i];
                p.lateral[i] = p.parent.lateral[i];
            }
            else
            {
                float u = p.along[i], du = 0.5f / (n - 1);
                p.points[i] = OnLane(p, u);
                p.dirs[i] = (OnLane(p, u + du) - OnLane(p, u - du)).normalized;
                p.lateral[i] = p.lane[i];
            }
        }
    }

    /// <summary>The point at progress u on a path's lane and altitude layer — where it runs with no
    /// swings at all.</summary>
    Vector3 OnLane(PathPlan p, float u)
    {
        Vector3 point = Vector3.Lerp(launchEnd, trackFinish, u)
                      + sideAxis * LaneAt(p, u) + Vector3.up * LiftAt(p, u);
        point.y = Mathf.Max(point.y, minAltitude);
        return point;
    }

    /// <summary>
    /// Places the control points inside `p`'s section m (between two anchors). Each sits on the axis at
    /// its progress, out on the path's lane, plus its own swing — free to cross over or under other
    /// paths — raised by its altitude layer and its up/down swing. A branch is pushed apart from its
    /// parent at Min Branch Angle or wider over its first legs.
    ///
    /// A loop comes out beside, above or below where it went in, so the road after one carries on from
    /// there: the points after it shift along with it, easing back onto the path's own line over the
    /// next few (never moving the section's end). Otherwise the road out of the loop would have to
    /// double back to reach the next point, folding over itself.
    ///
    /// `stretch` sets how much road the section takes: 0 = no swings at all, 1 = its swings as rolled,
    /// up to SwingScaleCap = swings that much bigger; past that the sideways swings stay put and only
    /// the hills keep growing, as they need no room to the side.
    /// Its loops grow and shrink with it too, within their radius ranges (see LoopRadiusAt).
    /// </summary>
    void LayOutSection(PathPlan p, int m, float stretch)
    {
        int a = p.bounds[m], b = p.bounds[m + 1];
        float k = Mathf.Min(stretch, SwingScaleCap);   // sideways
        float sideCap = convergenceSwing * 2f;
        float vertCap = currentConvergenceAltitudeSwing * 0.5f * Mathf.Max(1f, stretch);

        for (int i = a + 1; i < b; i++)
        {
            float u = p.along[i];
            // Calm off the launch and into the portal. The hills settle earlier, over the funnel, so
            // they can't undo the height steps between the paths' ends at the portal.
            float taper = Smooth01(u / 0.06f) * Smooth01((1f - u) / 0.12f);
            float hillTaper = Smooth01(u / 0.06f) * Smooth01((0.95f - u) / 0.25f);

            float swing = SoftLimit(k * p.swingSide[i] * taper, sideCap);
            // Just after a split, no swinging toward the new branch: it peels away from this road at
            // Min Branch Angle, so a lurch its way would drag it along (and stretching one, the other).
            foreach (PathPlan branch in p.branches)
                if (i - branch.forkIndex >= 1 && i - branch.forkIndex <= DivergeLegs && swing * branch.side > 0f)
                    swing = 0f;
            float x = p.lane[i] + swing;
            if (p.parent != null && i <= p.forkIndex + DivergeLegs) x = PullApart(p, i, x);

            float y = p.lift[i] + SoftLimit(stretch * p.swingVert[i] * hillTaper, vertCap);
            Vector3 point = Vector3.Lerp(launchEnd, trackFinish, u) + sideAxis * x + Vector3.up * y;
            point.y = Mathf.Max(point.y, minAltitude);
            p.points[i] = point;
            p.lateral[i] = x;
        }

        // In order along the road, as each loop moves the points after it.
        for (int i = a + 1; i < b; i++)
        {
            p.dirs[i] = Heading(p, i);
            if (p.kinds[i] == SegmentKind.Road) continue;

            LoopFrame loop = FrameLoop(p, i);
            p.loopExits[i] = loop.exit;
            Vector3 shift = loop.exit - loop.entry;

            // Out further along the track (a side loop's forward offset): the points after it move up
            // as far, the rest of the section squeezing in to fit before its end.
            float ahead = Vector3.Dot(shift, axisDir);
            shift -= axisDir * ahead;
            if (ahead > 0f)
            {
                float from = Vector3.Dot(p.points[i + 1] - launchEnd, axisDir);
                float room = Vector3.Dot(p.points[b] - launchEnd, axisDir) - from;
                ahead = Mathf.Min(ahead, 0.6f * Mathf.Max(room, 0f));
                for (int j = i + 1; j < b; j++)
                {
                    float t = (Vector3.Dot(p.points[j] - launchEnd, axisDir) - from) / Mathf.Max(room, 1f);
                    p.points[j] += axisDir * (ahead * (1f - t));
                }
            }

            // Out to one side, above or below: they ease back onto the path's line over the next few.
            int legs = Mathf.Clamp(b - i - 1, 1, LoopReturnLegs);
            for (int j = i + 1; j < b; j++)
            {
                p.points[j] += shift * (1f - Smooth01((j - i - 1) / (float)legs));
                p.points[j].y = Mathf.Max(p.points[j].y, minAltitude);
            }
        }

        EaseTightBends(p, a, b);
    }

    /// <summary>
    /// Opens up every bend in `p`'s section (between anchors a and b) too tight for the road. The road
    /// is built straight across (on the level, or rolled over), so a bend across it tighter than half
    /// the road and a shoulder folds its inner edge back over itself — a pinch — and a near-vertical
    /// stretch twists it (see BendRadius). Each pass eases the points either side of every such bend
    /// in toward their neighbours —
    /// halfway, for a bend that tight, less for one that's only getting close — until none is left.
    /// (Easing in by degrees keeps a section's length changing smoothly with its stretch, which
    /// FitSection relies on.) Anchors and loop starts (their loops are already placed) stay put, and a
    /// new path's first legs still peel away at Min Branch Angle (see PullApart).
    /// </summary>
    void EaseTightBends(PathPlan p, int a, int b)
    {
        var ease = new float[b + 1];
        for (int pass = 0; pass < MaxEasePasses; pass++)
        {
            bool any = false;
            for (int i = a; i <= b; i++) ease[i] = 0f;
            for (int s = a; s < b; s++)
            {
                float closeness = (2f * MinBendRadius - TightestBend(p, s)) / MinBendRadius;
                if (closeness <= 0f) continue;
                float w = 0.5f * Smooth01(closeness);
                ease[s] = Mathf.Max(ease[s], w);
                ease[s + 1] = Mathf.Max(ease[s + 1], w);
                any = true;
            }
            if (!any) return;

            for (int i = a + 1; i < b; i++)
            {
                if (ease[i] <= 0f || Pinned(p, i, a, b)) continue;
                Vector3 point = Vector3.Lerp(p.points[i], EasedTowards(p, i, a, b), ease[i]);
                float x = Vector3.Dot(point - Vector3.Lerp(launchEnd, trackFinish, p.along[i]), sideAxis);
                if (p.parent != null && i <= p.forkIndex + DivergeLegs)
                {
                    float apart = PullApart(p, i, x);
                    point += sideAxis * (apart - x);
                    x = apart;
                }
                point.y = Mathf.Max(point.y, minAltitude);
                p.points[i] = point;
                p.lateral[i] = x;
            }
            for (int i = a + 1; i < b; i++)
                if (p.kinds[i] == SegmentKind.Road) p.dirs[i] = Heading(p, i);
        }
    }

    static bool Pinned(PathPlan p, int i, int a, int b) => i <= a || i >= b || p.kinds[i] != SegmentKind.Road;

    /// <summary>
    /// Where easing moves control point i toward: midway between its neighbours — and where one of
    /// them is fixed along with its heading (an anchor, or the loop the road comes out of), halfway
    /// from there to carrying straight on along that heading, as the bend beside it is that heading's.
    /// </summary>
    Vector3 EasedTowards(PathPlan p, int i, int a, int b)
    {
        bool fromLoop = p.kinds[i - 1] != SegmentKind.Road;
        Vector3 before = fromLoop ? p.loopExits[i - 1] : p.points[i - 1], after = p.points[i + 1];
        Vector3 target = 0.5f * (before + after);
        if (fromLoop || i - 1 == a)
        {
            Vector3 heading = fromLoop ? FrameLoop(p, i - 1).fwd : p.dirs[a];
            target = Vector3.Lerp(target, before + heading * Vector3.Distance(before, p.points[i]), 0.5f);
        }
        if (i + 1 == b)
            target = Vector3.Lerp(target, after - p.dirs[b] * Vector3.Distance(p.points[i], after), 0.5f);
        return target;
    }

    /// <summary>The road's tightest bend along segment s of `p` — the road out of its loop, if it has
    /// one — as BuildSectionEdges will build it (see BendRadius).</summary>
    float TightestBend(PathPlan p, int s)
    {
        Vector3 end = p.points[s + 1];
        if (p.kinds[s] == SegmentKind.Road)
        {
            float span = Vector3.Distance(p.points[s], end);
            return BendRadius(p.points[s], p.points[s] + p.dirs[s] * (span * p.tension[s]),
                              end - p.dirs[s + 1] * (span * p.tension[s + 1]), end, p.roll[s], p.roll[s + 1]);
        }
        Vector3 exit = p.loopExits[s], exitDir = p.dirs[s];
        exitDir.y = 0f;
        exitDir = exitDir.sqrMagnitude < 0.0001f ? Vector3.forward : exitDir.normalized;
        float handle = 0.45f * Vector3.Distance(exit, end);
        return BendRadius(exit, exit + exitDir * handle, end - p.dirs[s + 1] * handle, end, p.roll[s], p.roll[s + 1]);
    }

    /// <summary>
    /// The tightest bend of a cubic Bezier road, rolled `rollFrom` to `rollTo` along it (see RollAt),
    /// as the radius its edges have to clear: the curve's bend across the road's width (on a road
    /// rolled over, a crest or dip bends it across), or — never less tight — its bend seen from
    /// above, as the mesh squares each cross-section up from the level heading, which turns quickly
    /// wherever the road runs near vertical.
    /// </summary>
    static float BendRadius(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float rollFrom, float rollTo)
    {
        const int Samples = 32;
        bool rolled = rollFrom != 0f || rollTo != 0f;
        float least = float.MaxValue;
        for (int k = 0; k <= Samples; k++)
        {
            float t = k / (float)Samples, u = 1f - t;
            Vector3 v = 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
            Vector3 acc = 6f * u * (p2 - 2f * p1 + p0) + 6f * t * (p3 - 2f * p2 + p1);
            float speed = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            float turn = Mathf.Abs(v.x * acc.z - v.z * acc.x);
            if (turn > 1e-6f) least = Mathf.Min(least, speed * speed * speed / turn);
            else if (speed < 1e-3f) least = 0f;

            if (!rolled || speed < 1e-3f) continue;
            float along = v.magnitude;
            Vector3 heading = v / along;
            Vector3 bend = (acc - Vector3.Dot(acc, heading) * heading) / (along * along);
            // Square across the road on the level, rolled about the heading (as AngleAxis would).
            Vector3 flat = Vector3.Cross(new Vector3(v.x, 0f, v.z), Vector3.up) / speed;
            float angle = RollAt(rollFrom, rollTo, t) * Mathf.Deg2Rad;
            Vector3 across = flat * Mathf.Cos(angle) + Vector3.Cross(heading, flat) * Mathf.Sin(angle);
            float bendAcross = Mathf.Abs(Vector3.Dot(bend, across));
            if (bendAcross > 1e-9f) least = Mathf.Min(least, 1f / bendAcross);
        }
        return least;
    }

    /// <summary>
    /// The road's direction through control point i: halfway between its way in (from the point
    /// before, or from the loop it comes out of) and its way out to the next point. Splitting the
    /// difference keeps the curve either side from swinging wide of a short leg and doubling back.
    /// </summary>
    Vector3 Heading(PathPlan p, int i)
    {
        Vector3 from = p.kinds[i - 1] != SegmentKind.Road ? p.loopExits[i - 1] : p.points[i - 1];
        Vector3 dir = (p.points[i] - from).normalized + (p.points[i + 1] - p.points[i]).normalized;
        return dir.sqrMagnitude > 1e-6f ? dir.normalized : (p.points[i + 1] - from).normalized;
    }

    /// <summary>Where a loop goes in and comes out, and which way it heads (see FrameLoop).</summary>
    struct LoopFrame
    {
        public Vector3 fwd, side;   // level heading into it, and the car's right (before it's turned over)
        public float turn;          // +1 = it turns or slides to the car's right, -1 = left
        public Quaternion lean;     // the road's roll where it goes in, which the loop is turned over by
        public Vector3 entry;
        public Vector3 flatExit;    // where it would come out off an unrolled road...
        public Vector3 exit;        // ...and where it does
    }

    LoopFrame FrameLoop(PathPlan p, int s) =>
        FrameLoop(p.points[s], p.dirs[s], p.kinds[s] == SegmentKind.SideLoop, p.loops[s], p.roll[s]);

    /// <summary>
    /// A loop on the segment starting at `start`: its run-up carries straight on (on the level) into
    /// it, and it comes out beside where it went in — slid sideways by its exit offset — or, for a
    /// side loop, above or below. Which way it turns or slides was rolled relative to the track axis
    /// (see SlideAwayFromFinish). The whole loop is then turned over about its heading by the road's
    /// roll there (`roadRoll`), so it sits square to the road leading into it: at 90°, an overhead
    /// loop lies on its side. Turned over, it never swings down through the altitude floor — it turns
    /// the other way instead.
    /// </summary>
    LoopFrame FrameLoop(Vector3 start, Vector3 startDir, bool sideLoop, LoopRoll roll, float roadRoll)
    {
        Vector3 fwd = startDir;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        var frame = new LoopFrame { fwd = fwd, entry = start + fwd * LoopRunUp };
        frame.side = Vector3.Cross(Vector3.up, fwd).normalized;
        frame.lean = Quaternion.AngleAxis(roadRoll, fwd);
        float axisSide = roll.sideRoll < 0.5f ? -1f : 1f;
        frame.turn = Vector3.Dot(frame.side, sideAxis) < 0f ? -axisSide : axisSide;

        for (int attempt = 0; ; attempt++)
        {
            if (sideLoop)
            {
                // Climbs or descends (a 50/50 roll), but never down through the altitude floor.
                float vertSign = roll.vertRoll < 0.5f && frame.entry.y - roll.exitOffset >= minAltitude ? -1f : 1f;
                frame.flatExit = frame.entry + Vector3.up * (roll.exitOffset * vertSign) + fwd * sideLoopExitForwardOffset;
            }
            else
            {
                frame.flatExit = frame.entry + frame.side * (roll.exitOffset * frame.turn) + fwd * loopExitForwardOffset;
            }
            frame.exit = frame.entry + frame.lean * (frame.flatExit - frame.entry);

            // How far below its entry the turned-over loop can reach (at its largest — see
            // LoopRadiusAt): the circle drops below it when it leans down, and its exit may sit lower.
            Vector3 toCentre = frame.lean * (sideLoop ? frame.side * frame.turn : Vector3.up);
            float radius = sideLoop ? maxSideLoopRadius : maxLoopRadius;
            float drop = 2f * radius * Mathf.Max(0f, -toCentre.y) + Mathf.Max(0f, frame.entry.y - frame.exit.y);
            if (attempt == 1 || frame.entry.y - drop >= minAltitude) return frame;
            frame.turn = -frame.turn;
        }
    }

    /// <summary>
    /// For a loop on a road heading off at an angle to the track axis (`slope` = sideways per unit
    /// forward): makes it slide out to the side the road is heading for, which carries its exit back
    /// from the finish rather than out past the road ahead. A road heading near enough straight on
    /// keeps the side it rolled. Decided once, when the loop is placed, so the loop doesn't jump from
    /// side to side as its section is stretched.
    /// </summary>
    static void SlideAwayFromFinish(ref LoopRoll roll, float slope)
    {
        if (Mathf.Abs(slope) > 0.18f) roll.sideRoll = slope > 0f ? 0.75f : 0.25f;
    }

    /// <summary>
    /// Keeps a new path peeling away from its parent: on the first leg the two roads leave the split
    /// at least Min Branch Angle apart, and after that the gap keeps opening at no less than half that
    /// rate. The two share their control point spacing through these legs, so they line up side by side.
    /// </summary>
    float PullApart(PathPlan p, int i, float x)
    {
        PathPlan q = p.parent;
        float legForward = Mathf.Max((p.along[i] - p.along[i - 1]) * axisLength, 1f);
        float minGap;
        if (i == p.forkIndex + 1)
        {
            // Angle between the two first legs, on the level, from the split point.
            float parentAngle = Mathf.Atan2(q.lateral[i] - q.lateral[i - 1], legForward);
            float angle = Mathf.Clamp(parentAngle + p.side * minBranchAngle * Mathf.Deg2Rad, -1.3f, 1.3f);
            float least = q.lateral[i - 1] + legForward * Mathf.Tan(angle);
            minGap = p.side * (least - q.lateral[i]);
        }
        else
        {
            minGap = p.side * (p.lateral[i - 1] - q.lateral[i - 1])
                   + 0.5f * legForward * Mathf.Tan(minBranchAngle * Mathf.Deg2Rad);
        }
        float gap = p.side * (x - q.lateral[i]);
        return gap >= minGap ? x : q.lateral[i] + p.side * minGap;
    }

    /// <summary>
    /// Builds the road edges of `p`'s section m from its control points — ordinary Bezier segments, or
    /// a loop sequence where one is planned — chained on from `previous`, and returns the section's
    /// length. With commit off the edges are only measured; with it on they join the track.
    /// </summary>
    float BuildSectionEdges(PathPlan p, int m, TrackEdge previous, bool commit)
    {
        var loopEdges = new List<TrackEdge>(3);
        float sectionLength = 0f;

        for (int s = p.bounds[m]; s < p.bounds[m + 1]; s++)
        {
            float length = 0f;
            if (p.kinds[s] != SegmentKind.Road)
            {
                bool sideLoop = p.kinds[s] == SegmentKind.SideLoop;
                LoopRoll roll = p.loops[s];
                roll.radius = LoopRadiusAt(sideLoop, roll.radius, p.sectionStretch[m]);
                loopEdges.Clear();
                previous = BuildLoopSequence(previous, FrameLoop(p, s), p.points[s], p.dirs[s],
                                             p.points[s + 1], p.dirs[s + 1], sideLoop, roll,
                                             p.roll[s], p.roll[s + 1], loopEdges);
                foreach (TrackEdge edge in loopEdges)
                {
                    AddEdge(edge, commit);
                    length += EdgeLength(edge);
                }
            }
            else
            {
                var seg = new TrackEdge
                {
                    startPos = p.points[s],
                    startDir = p.dirs[s],
                    endPos = p.points[s + 1],
                    endDir = p.dirs[s + 1],
                    rollStart = p.roll[s],
                    rollEnd = p.roll[s + 1],
                    parent = previous
                };
                // Each end's handle takes its control point's tension, so tight and lazy corners mix
                // along the path instead of every corner sharing one shape.
                float span = Vector3.Distance(seg.startPos, seg.endPos);
                seg.handleStart = span * p.tension[s];
                seg.handleEnd = span * p.tension[s + 1];
                AddEdge(seg, commit);
                length = EdgeLength(seg);
                previous = seg;
            }
            sectionLength += length;
            if (commit)
            {
                p.routeLength[s + 1] = p.routeLength[s] + length;
                p.edgeInto[s + 1] = previous;
            }
        }
        return sectionLength;
    }

    /// <summary>A loop's radius in a section laid out at `stretch`: as rolled at 1, easing down toward
    /// the smallest allowed with less and up toward the largest with more — one more way a section
    /// gives or takes road.</summary>
    float LoopRadiusAt(bool sideLoop, float rolled, float stretch)
    {
        float lo = sideLoop ? minSideLoopRadius : minLoopRadius;
        float hi = sideLoop ? maxSideLoopRadius : maxLoopRadius;
        return stretch <= 1f ? Mathf.Lerp(lo, rolled, stretch)
                             : Mathf.Lerp(rolled, hi, (stretch - 1f) / (MaxStretch - 1f));
    }

    /// <summary>Builds `p`'s edges for good, section by section, chained on from its parent's edge
    /// into the split (or the launch straight, for the trunk).</summary>
    void CommitPath(PathPlan p)
    {
        int n = segmentsPerRoute;
        int start = p.LayoutStart;
        if (p.parent == null)
        {
            float launch = Vector3.Distance(trackStart, launchEnd);
            var launchEdge = new TrackEdge
            {
                startPos = trackStart,
                startDir = Vector3.forward,
                endPos = launchEnd,
                endDir = Vector3.forward,
                handleStart = launch * 0.4f,
                handleEnd = launch * 0.4f
            };
            AddEdge(launchEdge, commit: true);
            p.routeLength[1] = EdgeLength(launchEdge);
            p.edgeInto[1] = launchEdge;
        }
        else
        {
            p.routeLength[start] = p.parent.routeLength[start];
            p.edgeInto[start] = p.parent.edgeInto[start];
        }

        for (int m = 0; m < p.sectionLength.Length; m++)
            BuildSectionEdges(p, m, p.edgeInto[p.bounds[m]], commit: true);
        p.finalEdge = p.edgeInto[n];

        // Right at a split the two roads overlap until they've pulled a road's width apart, so their
        // inner rails would cross each other's road. Those two shoulders stay flat for the first leg.
        if (p.parent != null)
        {
            Vector3 apart = p.points[start + 1] - p.parent.points[start + 1];   // parent -> branch
            apart.y = 0f;
            FlattenShoulderFacing(p.edgeInto[start + 1], -apart);
            FlattenShoulderFacing(p.parent.edgeInto[start + 1], apart);
        }
    }

    /// <summary>Keeps the shoulder on `edge` that faces `towards` (a level direction) flat — no rail.</summary>
    static void FlattenShoulderFacing(TrackEdge edge, Vector3 towards)
    {
        Vector3 level = edge.startDir;
        level.y = 0f;
        // TrackMeshBuilder's "right" side, for this edge's heading.
        Vector3 meshRight = Vector3.Cross(level.normalized, Vector3.up);
        if (Vector3.Dot(meshRight, towards) >= 0f) edge.flatShoulderRight = true;
        else edge.flatShoulderLeft = true;
    }

    void AddEdge(TrackEdge edge, bool commit)
    {
        if (!commit) return;
        if (edge.parent != null) edge.parent.children.Add(edge);
        allEdges.Add(edge);
    }

    float EdgeLength(TrackEdge edge)
    {
        if (!edge.isLoop) return EstimateBezierLength(edge);
        float length = 0f;
        for (int i = 1; i < edge.loopPoints.Count; i++)
            length += Vector3.Distance(edge.loopPoints[i - 1], edge.loopPoints[i]);
        return length;
    }

    // The swing styles a path is strung together from, and their relative odds — the flowing shapes
    // a little more common than the busy ones.
    enum SwingStyle { Straight, Sweeper, Slalom, Hairpin, LaneShift }
    static readonly float[] SwingStyleOdds = { 2f, 3f, 1.5f, 1.5f, 2f };

    /// <summary>
    /// Adds one whole-path bow to `offsets`: 30% of paths none, the rest a single long C or a long
    /// S, peaking at a random point along the way. Zero at both ends, so the endpoints never move.
    /// </summary>
    static void AddPathBow(float[] offsets, float[] along, float scale)
    {
        if (Random.value < 0.3f) return;
        float waves = Random.value < 0.5f ? 1f : 2f;                     // 1 = C, 2 = S
        float amp = Random.Range(0.4f, 1f) * scale * (Random.value < 0.5f ? -1f : 1f);
        float peak = Random.Range(0.35f, 0.65f);                          // where the bow leans

        for (int i = 1; i < offsets.Length - 1; i++)
        {
            // Warp `along` so its midpoint lands on `peak`, skewing the bow forward or back.
            float t = along[i];
            float w = t < peak ? 0.5f * t / peak : 0.5f + 0.5f * (t - peak) / (1f - peak);
            offsets[i] += amp * Mathf.Sin(Mathf.PI * waves * w);
        }
    }

    /// <summary>Smoothly squashes `x` into (-limit, limit): near-linear for small values, rounding
    /// off big ones instead of clipping them flat.</summary>
    static float SoftLimit(float x, float limit) =>
        limit <= 0f ? 0f : limit * (float)System.Math.Tanh(x / limit);

    /// <summary>
    /// Fills offsets[1..n-2] (the interior points — both ends stay on the axis at 0) with a run of
    /// random swing sections and — when `tension` is given — the handle tension that suits each
    /// section. Used for both the sideways and the vertical profile of a path.
    /// </summary>
    void FillSwingProfile(float[] offsets, float scale, float[] tension)
    {
        int last = offsets.Length - 2;   // final interior point
        float lane = 0f;                 // the line the path is currently centred on
        var previous = (SwingStyle)(-1);

        for (int i = 1; i <= last; )
        {
            SwingStyle style = PickSwingStyle(previous);
            previous = style;
            float sign = Random.value < 0.5f ? -1f : 1f;
            int len;

            switch (style)
            {
                case SwingStyle.Straight:      // hold the current line — a breather between swings
                    len = Random.Range(1, 3);
                    for (int k = 0; k < len; k++)
                        Put(i + k, lane + Random.Range(-0.1f, 0.1f) * scale, 0.4f, 0.5f);
                    break;

                case SwingStyle.Sweeper:       // one long, smooth arc out to a side and back
                {
                    len = Random.Range(2, 5);
                    float amp = Random.Range(0.6f, 1.1f) * scale;
                    for (int k = 0; k < len; k++)
                        Put(i + k, lane + sign * amp * Mathf.Sin(Mathf.PI * (k + 1) / (len + 1)), 0.4f, 0.5f);
                    break;
                }

                case SwingStyle.Slalom:        // a short burst of quick left-right flicks
                    len = Random.Range(2, 4);
                    for (int k = 0; k < len; k++, sign = -sign)
                        Put(i + k, lane + sign * Random.Range(0.3f, 0.7f) * scale, 0.4f, 0.5f);
                    break;

                case SwingStyle.Hairpin:       // a single sharp jab out to one side
                    len = 1;
                    Put(i, lane + sign * Random.Range(0.8f, 1.1f) * scale, 0.4f, 0.5f);
                    break;

                default:                       // LaneShift — drift across to a new line and stay on it
                {
                    len = Random.Range(1, 3);
                    float newLane = Mathf.Clamp(lane + sign * Random.Range(0.4f, 0.8f) * scale,
                                                -0.6f * scale, 0.6f * scale);
                    for (int k = 0; k < len; k++)
                        Put(i + k, Mathf.Lerp(lane, newLane, (k + 1f) / len), 0.4f, 0.5f);
                    lane = newLane;
                    break;
                }
            }
            i += len;
        }

        void Put(int index, float value, float minTension, float maxTension)
        {
            if (index > last) return;   // a section running past the end is simply cut short
            offsets[index] = value;
            if (tension != null) tension[index] = Random.Range(minTension, maxTension);
        }
    }

    /// <summary>
    /// Weighted random swing style. Never `previous`, so no style runs twice in a row — and a busy
    /// section (slalom or hairpin) is always followed by a calm one, so busy flicks can't chain
    /// into one long, coil-like zigzag.
    /// </summary>
    static SwingStyle PickSwingStyle(SwingStyle previous)
    {
        float total = 0f;
        for (int s = 0; s < SwingStyleOdds.Length; s++)
            if (CanFollow((SwingStyle)s, previous)) total += SwingStyleOdds[s];

        float roll = Random.value * total;
        for (int s = 0; s < SwingStyleOdds.Length; s++)
        {
            if (!CanFollow((SwingStyle)s, previous)) continue;
            roll -= SwingStyleOdds[s];
            if (roll <= 0f) return (SwingStyle)s;
        }
        return previous == SwingStyle.Sweeper ? SwingStyle.Straight : SwingStyle.Sweeper;   // float round-off
    }

    static bool IsBusySwing(SwingStyle s) => s == SwingStyle.Slalom || s == SwingStyle.Hairpin;

    static bool CanFollow(SwingStyle next, SwingStyle previous) =>
        next != previous && !(IsBusySwing(next) && IsBusySwing(previous));

    /// <summary>
    /// Builds the loop as a single flexible "wire": a circular arc in a tilted
    /// plane, pinned at the entry and exit by a smootherstep drift. One continuous
    /// edge — no apex/split/ramp — so it adapts smoothly to any offset (closed
    /// teardrop when the offset is small, open arch when stretched), exactly like
    /// a real pliable wire.
    ///
    /// Side loops (sideLoop = true) use the same approach -> loop -> post-loop
    /// scaffolding, but the circle lies flat and turns left or right, and the exit
    /// slides up or down instead of sideways so the road clears itself where it
    /// crosses over.
    ///
    /// Where it goes in and comes out is set by `frame` (see FrameLoop); the loop's size comes
    /// pre-rolled in `roll`. It's built as if off an unrolled road, then turned over about its
    /// heading by the road's roll where it starts (`rollIn`), which the run-up keeps to; the road
    /// out of it rolls on from there to `rollOut` at the segment's end. The three new edges —
    /// approach, loop, post-loop — are chained and appended to `built` in order but not yet joined
    /// to the track; the caller does that. Returns the post-loop edge.
    /// </summary>
    TrackEdge BuildLoopSequence(TrackEdge parent, LoopFrame frame,
                                 Vector3 approachStart, Vector3 approachStartDir,
                                 Vector3 originalEnd, Vector3 originalEndDir,
                                 bool sideLoop, LoopRoll roll, float rollIn, float rollOut,
                                 List<TrackEdge> built)
    {
        float loopRadius = roll.radius;
        Vector3 fwd = frame.fwd, loopEntry = frame.entry, loopExit = frame.exit;

        // ----- Approach edge: flat road blending up to the loop entry. -----
        var approach = new TrackEdge
        {
            startPos = approachStart,
            startDir = approachStartDir,
            endPos = loopEntry,
            endDir = fwd,
            handleStart = LoopRunUp * 0.4f,
            handleEnd = LoopRunUp * 0.4f,
            rollStart = rollIn,
            rollEnd = rollIn,
            parent = parent
        };
        built.Add(approach);

        int loopSamples = Mathf.Max(48, Mathf.RoundToInt(loopRadius * 0.5f));
        Vector3 loopCenter;   // only used as a fallback by the mesh; a sensible value
        List<Vector3> loopPts, loopNrm;

        if (sideLoop)
        {
            // The exit sits above or below the entry: that gap is the clearance where the road
            // passes over itself.
            Vector3 turnDir = frame.side * frame.turn;
            loopPts = BuildPliableSideLoopPoints(loopEntry, fwd, turnDir, frame.flatExit, loopRadius, loopSamples, out loopNrm);
            loopCenter = loopEntry + turnDir * loopRadius;
        }
        else
        {
            // The exit's sideways slide vs. the loop's radius is what makes it loop (tight) or
            // arch (stretched) — the pliable-wire behaviour.
            loopPts = BuildPliableLoopPoints(loopEntry, fwd, frame.flatExit, loopRadius, loopSamples, out loopNrm);
            loopCenter = loopEntry + Vector3.up * loopRadius;
        }

        // Turned over with the road leading in.
        if (rollIn != 0f)
        {
            for (int i = 0; i < loopPts.Count; i++)
            {
                loopPts[i] = loopEntry + frame.lean * (loopPts[i] - loopEntry);
                loopNrm[i] = frame.lean * loopNrm[i];
            }
            loopCenter = loopEntry + frame.lean * (loopCenter - loopEntry);
        }

        var loop = new TrackEdge
        {
            startPos = loopEntry,
            endPos = loopExit,
            // startDir/endDir kept for any code that reads them; the mesh uses loopPoints.
            startDir = fwd,
            endDir = (loopPts[loopPts.Count - 1] - loopPts[loopPts.Count - 2]).normalized,
            parent = approach,
            isLoop = true,
            isSideLoop = sideLoop,
            loopCenter = loopCenter,
            loopRadius = loopRadius,
            loopForward = fwd,
            loopPoints = loopPts,
            loopNormals = loopNrm,
        };
        built.Add(loop);

        // ----- Post-loop edge: exit -> original segment end (normal road). -----
        // The loop ends with a horizontal-ish tangent (the RMF + drift flattens the
        // ends), so this picks up smoothly just like the approach feeds the entry.
        Vector3 loopExitDir = loop.endDir;
        loopExitDir.y = 0f;
        if (loopExitDir.sqrMagnitude < 0.0001f) loopExitDir = fwd;
        loopExitDir.Normalize();

        float span = Vector3.Distance(loopExit, originalEnd);
        var postLoop = new TrackEdge
        {
            startPos = loopExit,
            startDir = loopExitDir,
            endPos = originalEnd,
            endDir = originalEndDir,
            handleStart = span * 0.45f,
            handleEnd = span * 0.45f,
            rollStart = rollIn,
            rollEnd = rollOut,
            parent = loop
        };
        built.Add(postLoop);
        return postLoop;
    }

    /// <summary>
    /// Generates the loop centerline as a circle in a tilted plane, drifted so it
    /// starts exactly at `entry` and ends exactly at `exit`. The plane contains
    /// world-up and tilts toward the entry->exit chord, so sideways/forward offset
    /// is absorbed by tilting the loop rather than distorting it.
    /// </summary>
    List<Vector3> BuildPliableLoopPoints(Vector3 entry, Vector3 entryDir,
                                          Vector3 exit, float radius, int samples,
                                          out List<Vector3> normals)
    {
        Vector3 up = Vector3.up;

        const float lowerFactor = 1f;
        const float widthScale = 1f;
        const float sweepFrac = 1f;
        const float endBlend = 0.12f;   // fraction at each end that flattens to road
        float rv = radius * lowerFactor;
        float rh = radius * widthScale;

        Vector3 fwdh = entryDir; fwdh.y = 0f;
        if (fwdh.sqrMagnitude < 1e-6f) fwdh = Vector3.forward;
        fwdh.Normalize();

        Vector3 e_t = fwdh;   // along travel
        Vector3 e_c = up;     // straight up — no lean, so the heading never turns

        Vector3 center = entry + e_c * rv;
        float sweep = Mathf.PI * 2f * sweepFrac;

        Vector3 startCircle = center + (-e_c * rv);
        Vector3 endCircle = center + (-Mathf.Cos(sweep) * e_c * rv + Mathf.Sin(sweep) * e_t * rh);

        var pts = new List<Vector3>(samples + 1);
        var centers = new List<Vector3>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float theta = t * sweep;

            Vector3 circlePt = center + (-Mathf.Cos(theta) * e_c * rv + Mathf.Sin(theta) * e_t * rh);
            float ss = t * t * t * (t * (t * 6f - 15f) + 10f);
            Vector3 drift = (exit - endCircle) * ss + (entry - startCircle) * (1f - ss);

            pts.Add(circlePt + drift);
            centers.Add(center + drift);   // the loop center, drifted the same way
        }

        // Surface normals: face the loop interior through the body, blend to world
        // up at both ends so the loop meets the flat road flat (no twist / no "+").
        normals = new List<Vector3>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;

            Vector3 tangent;
            if (i == 0) tangent = (pts[1] - pts[0]);
            else if (i == samples) tangent = (pts[samples] - pts[samples - 1]);
            else tangent = (pts[i + 1] - pts[i - 1]);
            tangent.Normalize();

            Vector3 interior = (centers[i] - pts[i]).normalized;

            Vector3 n;
            if (t < endBlend)
            {
                float b = t / endBlend; b = b * b * b * (b * (b * 6f - 15f) + 10f);
                n = Vector3.Lerp(up, interior, b);
            }
            else if (t > 1f - endBlend)
            {
                float b = (1f - t) / endBlend; b = b * b * b * (b * (b * 6f - 15f) + 10f);
                n = Vector3.Lerp(up, interior, b);
            }
            else
            {
                n = interior;
            }

            // Orthogonalize against the tangent so width = tangent x normal is clean.
            n = (n - Vector3.Dot(n, tangent) * tangent);
            if (n.sqrMagnitude < 1e-6f) n = up;
            n.Normalize();
            normals.Add(n);
        }

        return pts;
    }

    /// <summary>
    /// Side-loop counterpart of BuildPliableLoopPoints: the same drifted circle,
    /// laid flat. It curves toward `turnDir` (the car's left or right) so the car
    /// makes one continuous 360° turn, while the smootherstep drift carries it up
    /// or down (and forward) to `exit`. That drift is what lifts the road clear
    /// where it passes over itself. The road stays flat across its width, so it
    /// meets the ordinary road flat at both ends.
    /// </summary>
    List<Vector3> BuildPliableSideLoopPoints(Vector3 entry, Vector3 entryDir, Vector3 turnDir,
                                              Vector3 exit, float radius, int samples,
                                              out List<Vector3> normals)
    {
        Vector3 up = Vector3.up;

        Vector3 e_t = entryDir; e_t.y = 0f;   // along travel
        if (e_t.sqrMagnitude < 1e-6f) e_t = Vector3.forward;
        e_t.Normalize();

        // Toward the loop centre: horizontal and square to the direction of travel.
        Vector3 e_c = turnDir - Vector3.Dot(turnDir, e_t) * e_t;
        e_c.y = 0f;
        if (e_c.sqrMagnitude < 1e-6f) e_c = Vector3.Cross(up, e_t);
        e_c.Normalize();

        Vector3 center = entry + e_c * radius;
        float sweep = Mathf.PI * 2f;

        Vector3 startCircle = center + (-e_c * radius);
        Vector3 endCircle = center + (-Mathf.Cos(sweep) * e_c + Mathf.Sin(sweep) * e_t) * radius;

        var pts = new List<Vector3>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float theta = t * sweep;

            Vector3 circlePt = center + (-Mathf.Cos(theta) * e_c + Mathf.Sin(theta) * e_t) * radius;
            float ss = t * t * t * (t * (t * 6f - 15f) + 10f);
            Vector3 drift = (exit - endCircle) * ss + (entry - startCircle) * (1f - ss);

            pts.Add(circlePt + drift);
        }

        // Surface normals: world up, tilted only along the direction of travel as
        // the road climbs/descends — no banking, so the car steers the whole turn.
        normals = new List<Vector3>(samples + 1);
        for (int i = 0; i <= samples; i++)
        {
            Vector3 tangent;
            if (i == 0) tangent = (pts[1] - pts[0]);
            else if (i == samples) tangent = (pts[samples] - pts[samples - 1]);
            else tangent = (pts[i + 1] - pts[i - 1]);
            tangent.Normalize();

            Vector3 n = up - Vector3.Dot(up, tangent) * tangent;
            if (n.sqrMagnitude < 1e-6f) n = up;
            n.Normalize();
            normals.Add(n);
        }

        return pts;
    }

    // -------------------------------------------------------
    //  Bezier helpers
    // -------------------------------------------------------

    Vector3 BezierPoint(TrackEdge e, float t)
    {
        Vector3 p0 = e.startPos;
        Vector3 p1 = e.startPos + e.startDir * e.handleStart;
        Vector3 p2 = e.endPos - e.endDir * e.handleEnd;
        Vector3 p3 = e.endPos;

        float u = 1f - t;
        return u * u * u * p0
             + 3f * u * u * t * p1
             + 3f * u * t * t * p2
             + t * t * t * p3;
    }

    float EstimateBezierLength(TrackEdge e, int samples = 30)
    {
        float len = 0f;
        Vector3 prev = e.startPos;
        for (int i = 1; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 curr = BezierPoint(e, t);
            len += Vector3.Distance(prev, curr);
            prev = curr;
        }
        return len;
    }

    static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0
             + 3f * u * u * t * p1
             + 3f * u * t * t * p2
             + t * t * t * p3;
    }

    static Vector3 CubicBezierTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        Vector3 d = 3f * u * u * (p1 - p0)
                  + 6f * u * t * (p2 - p1)
                  + 3f * t * t * (p3 - p2);
        return d.normalized;
    }
    // -------------------------------------------------------
    //  Phase 3 — meshing
    // -------------------------------------------------------

    void BuildEdgeMesh(TrackEdge edge)
    {

        if (edge.isLoop)
        {
            // Loops carry their own continuous centerline; no Bezier sampling
            edge.sampledPoints = edge.loopPoints;
            BuildLoopMeshObject(edge, Vector3.zero);   // rotationAxis unused now (RMF)
            return;
        }

        float arcLen = EstimateBezierLength(edge);
        int numPts = Mathf.Max(8, Mathf.RoundToInt(arcLen / waypointSpacing));

        {
            // Normal Bezier sampling (existing code)
            arcLen = EstimateBezierLength(edge);
            numPts = Mathf.Max(8, Mathf.RoundToInt(arcLen / waypointSpacing));

            edge.sampledPoints = new List<Vector3>(numPts + 1);
            for (int i = 0; i <= numPts; i++)
            {
                float t = i / (float)numPts;
                Vector3 pt = BezierPoint(edge, t);
                if (pt.y < minAltitude) pt.y = minAltitude;
                edge.sampledPoints.Add(pt);
            }
        }

        // Rest of BuildEdgeMesh — building the TrackSpline and spawning the mesh — stays the same
        var spline = new TrackSpline();

        // ... existing phantom + spline code continues unchanged

        if (edge.parent != null && edge.parent.sampledPoints != null
            && edge.parent.sampledPoints.Count >= 2)
        {
            var pp = edge.parent.sampledPoints;
            spline.AddPoint(pp[pp.Count - 2]);
        }
        else
        {
            spline.AddPoint(edge.startPos - edge.startDir * waypointSpacing);
        }

        foreach (var pt in edge.sampledPoints) spline.AddPoint(pt);

        if (edge.children.Count > 0 && edge.children[0].sampledPoints != null
            && edge.children[0].sampledPoints.Count >= 2)
        {
            spline.AddPoint(edge.children[0].sampledPoints[1]);
        }
        else
        {
            spline.AddPoint(edge.endPos + edge.endDir * waypointSpacing);
        }

        int resolution = Mathf.Max(20, Mathf.RoundToInt(arcLen * meshSamplesPerUnit));

        GameObject obj = new GameObject("RoadEdge");
        obj.transform.SetParent(transform);

        // A rolled road turns each cross-section about its direction of travel (see RollAt) — eased
        // evenly along the road's length, not the curve's parameter, which bunches up in the bends.
        System.Func<float, float> rollAt = null;
        if (edge.rollStart != 0f || edge.rollEnd != 0f)
        {
            List<Vector3> pts = edge.sampledPoints;
            var along = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++) along[i] = along[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
            float total = Mathf.Max(along[pts.Count - 1], 1e-3f), from = edge.rollStart, to = edge.rollEnd;
            rollAt = t =>
            {
                float x = Mathf.Clamp01(t) * (pts.Count - 1);
                int i = Mathf.Min((int)x, pts.Count - 2);
                return RollAt(from, to, Mathf.Lerp(along[i], along[i + 1], x - i) / total);
            };
        }

        Mesh roadMesh = TrackMeshBuilder.BuildRoadMesh(
            spline, roadWidth, resolution, roadThickness, uvTilingFactor, rollAt
        );
        obj.AddComponent<MeshFilter>().sharedMesh = roadMesh;
        obj.AddComponent<MeshCollider>().sharedMesh = roadMesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = GetRoadMaterial();
        SmoothSurface.Register(roadMesh);   // the car rides the curve these panels stand in for

        // Shoulder strips on both sides
        if (shoulderWidth > 0f)
        {
            SpawnShoulder(obj.transform, spline, resolution, rightSide: true, rail: !edge.flatShoulderRight, rollAt);
            SpawnShoulder(obj.transform, spline, resolution, rightSide: false, rail: !edge.flatShoulderLeft, rollAt);
        }

        // Put the road edge and its shoulder children on the Track layer.
        ApplyTrackLayer(obj);
    }

    /// <summary>
    /// Spawns a loop's road mesh using the dedicated loop builder, with the
    /// road surface correctly oriented to face the loop's interior.
    /// </summary>
    void BuildLoopMeshObject(TrackEdge edge, Vector3 rotationAxis)
    {
        // Side loops keep the "RoadEdge" name prefix (the obstacle spawners sample every
        // RoadEdge*) but are left untagged: the "Loop" tag is what triggers the car's
        // loop speed boost, and side loops get no automatic turbo.
        GameObject obj = new GameObject(edge.isSideLoop ? "RoadEdge_SideLoop" : "RoadEdge_Loop");
        obj.transform.SetParent(transform);

        Mesh loopMesh = TrackMeshBuilder.BuildLoopMeshExplicit(
                    edge.sampledPoints, edge.loopNormals, roadWidth, roadThickness, uvTilingFactor);

        obj.AddComponent<MeshFilter>().sharedMesh = loopMesh;
        obj.AddComponent<MeshCollider>().sharedMesh = loopMesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = GetRoadMaterial();
        SmoothSurface.Register(loopMesh);

        if (!edge.isSideLoop) obj.tag = "Loop";

        if (shoulderWidth > 0f)
        {
            SpawnLoopShoulder(obj.transform, edge, Vector3.zero, true);
            SpawnLoopShoulder(obj.transform, edge, Vector3.zero, false);
        }

        // Put the loop mesh and its shoulder children on the Track layer.
        ApplyTrackLayer(obj);
    }

    void SpawnLoopShoulder(Transform parent, TrackEdge edge, Vector3 rotationAxis, bool rightSide)
    {
        var shoulderObj = new GameObject(rightSide ? "LoopShoulderRight" : "LoopShoulderLeft");
        shoulderObj.transform.SetParent(parent);
        if (!edge.isSideLoop) shoulderObj.tag = "Loop";

        Mesh shoulderMesh = TrackMeshBuilder.BuildLoopShoulderMeshExplicit(
                    edge.sampledPoints, edge.loopNormals, roadWidth, shoulderWidth, roadThickness, rightSide, uvTilingFactor);

        AttachShoulderMeshes(shoulderObj, shoulderMesh);
        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetShoulderMaterial();
    }

    /// <summary>
    /// Spawns a shoulder strip as a child of the road edge GameObject. The
    /// strip uses its own MeshFilter/MeshRenderer with the shoulder material
    /// so it can have emission separate from the main road. Also gets a
    /// MeshCollider so the car can drive on it just like the road.
    /// </summary>
    void SpawnShoulder(Transform parent, TrackSpline spline, int resolution, bool rightSide, bool rail,
                       System.Func<float, float> rollAt)
    {
        var shoulderObj = new GameObject(rightSide ? "ShoulderRight" : "ShoulderLeft");
        shoulderObj.transform.SetParent(parent);

        Mesh shoulderMesh = TrackMeshBuilder.BuildShoulderMesh(
            spline, roadWidth, shoulderWidth, resolution, roadThickness, rightSide, uvTilingFactor, rollAt
        );

        AttachShoulderMeshes(shoulderObj, shoulderMesh, rail);

        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetShoulderMaterial();
    }

    /// <summary>
    /// Gives a shoulder its meshes. It renders its flat slab lifted into a curb rail by Shoulder
    /// Rail Height (or the flat slab itself when that's 0). With Shoulder Rail Collision on, the
    /// raised rail is the collider too, so it stops the car like a real barrier — the car bodies
    /// ride ~0.4-0.55 above the road, under the rail's top, so they meet its wall rather than
    /// riding over it. Off, the car collides with the flat slab and the rail is visual only.
    /// With `rail` off the shoulder stays a flat slab regardless (the inner sides of a split).
    /// </summary>
    void AttachShoulderMeshes(GameObject shoulderObj, Mesh flatShoulder, bool rail = true)
    {
        Mesh visual = rail && shoulderRailHeight > 0f
            ? TrackMeshBuilder.BuildRaisedShoulderMesh(flatShoulder.vertices, flatShoulder.uv, shoulderRailHeight)
            : flatShoulder;
        Mesh collision = shoulderRailCollision ? visual : flatShoulder;

        shoulderObj.AddComponent<MeshFilter>().sharedMesh = visual;
        shoulderObj.AddComponent<MeshCollider>().sharedMesh = collision;
        SmoothSurface.Register(collision);

        // Runtime meshes aren't garbage-collected; free the flat slab when nothing ended up using it.
        if (visual != flatShoulder && collision != flatShoulder) Destroy(flatShoulder);
    }

    Material GetRoadMaterial()
    {
        // GenerateTrack prepares the randomised instances up front; this is just a guard.
        if (runtimeRoadMaterial == null) PrepareTrackMaterials();
        return runtimeRoadMaterial;
    }

    /// <summary>The shoulders' (and their rail teeth's) material: this generation's hue-matched
    /// Shoulder Material instance, or the road material when no Shoulder Material is assigned.</summary>
    Material GetShoulderMaterial()
    {
        if (runtimeRoadMaterial == null) PrepareTrackMaterials();
        return runtimeShoulderMaterial != null ? runtimeShoulderMaterial : runtimeRoadMaterial;
    }

    /// <summary>Builds this generation's road and shoulder materials as INSTANCES of the assigned
    /// RoadMaterial / Shoulder Material (so the shared assets on disk are never touched), with ONE
    /// randomised HUE shared by both: the road's Base Map colour and the shoulder's Emission colour.
    /// Saturation and value (the emission's HDR intensity included), and every other material value,
    /// are left as they are — so each track comes out a different colour with the road and its glow
    /// always matching. Uses an independent RNG so the colour doesn't perturb the seeded
    /// track-geometry generation.</summary>
    void PrepareTrackMaterials()
    {
        // Drop a prior generation's instances.
        if (runtimeRoadMaterial != null) Destroy(runtimeRoadMaterial);
        if (runtimeShoulderMaterial != null) Destroy(runtimeShoulderMaterial);

        // Multiplayer: derive the hue from the round seed so every client's track matches.
        System.Random hueRng = MultiplayerWorld.IsMultiplayerGame
            ? MultiplayerWorld.DeriveRandom("roadhue")
            : new System.Random();
        float hue = (float)hueRng.NextDouble();   // 0..1 == the full 0..360 hue wheel

        runtimeRoadMaterial = roadMaterial != null ? new Material(roadMaterial) : BuildFallbackRoadMaterial();
        // URP/Lit's "Base Map" tint (Surface Inputs) is the "_BaseColor" property.
        SetColorHue(runtimeRoadMaterial, "_BaseColor", hue);

        runtimeShoulderMaterial = shoulderMaterial != null ? new Material(shoulderMaterial) : null;
        // URP/Lit's "Emission Map" colour (the HDR swatch beside the Emission Map slot) is "_EmissionColor".
        if (runtimeShoulderMaterial != null) SetColorHue(runtimeShoulderMaterial, "_EmissionColor", hue);
    }

    /// <summary>Swaps a colour property's hue, keeping its saturation, value and alpha. HDR stays
    /// HDR: an emission intensity above 1 comes back out unchanged.</summary>
    static void SetColorHue(Material mat, string prop, float hue)
    {
        if (!mat.HasProperty(prop)) return;
        Color c = mat.GetColor(prop);
        Color.RGBToHSV(c, out _, out float s, out float v);
        Color shifted = Color.HSVToRGB(hue, s, v, true);
        shifted.a = c.a;   // leave alpha untouched
        mat.SetColor(prop, shifted);
    }

    Material BuildFallbackRoadMaterial()
    {
        Material m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.mainTexture = ArrowTextureGenerator.Generate();
        return m;
    }

    void OnDestroy()
    {
        if (Current == this) Current = null;
        // The runtime instances aren't owned by any GameObject, so free them when the generator is torn down.
        if (runtimeRoadMaterial != null) Destroy(runtimeRoadMaterial);
        if (runtimeShoulderMaterial != null) Destroy(runtimeShoulderMaterial);
    }

    // -------------------------------------------------------
    //  Layer assignment — all generated track geometry (road,
    //  shoulders, loops, loop shoulders) goes on the Track layer
    //  so the car's suspension ground mask can target the track.
    // -------------------------------------------------------

    [Header("Layers")]
    [Tooltip("Name of the layer every generated track object (road, shoulders, loops) is " +
             "placed on. Layer 8 is 'Track' by default; set the car's Ground Mask to it.")]
    public string trackLayerName = "Track";
    private bool warnedMissingTrackLayer;

    /// <summary>
    /// Puts a generated track object — and all of its children (e.g. the shoulder strips) —
    /// on the configured track layer. No-op, with a one-time warning, if that layer isn't
    /// defined in Tags and Layers.
    /// </summary>
    void ApplyTrackLayer(GameObject root)
    {
        int layer = LayerMask.NameToLayer(trackLayerName);
        if (layer < 0)
        {
            if (!warnedMissingTrackLayer)
            {
                warnedMissingTrackLayer = true;
                Debug.LogWarning($"[TrackGenerator] Layer '{trackLayerName}' not found in Tags " +
                                 "and Layers — generated track left on its default layer.");
            }
            return;
        }
        SetLayerRecursively(root, layer);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // -------------------------------------------------------
    //  Mesh edges in correct order so parents are sampled first.
    // (Already guaranteed by allEdges.Add order — parents added before children.)
    // -------------------------------------------------------

    IEnumerator SpawnCarDelayed()
    {
        // Multiplayer: the car stays in the hub until the player drives through the portal —
        // MultiplayerWorld teleports it to CarSpawnPosition then. Auto-placing here would yank a
        // hub-dwelling player onto the track the moment the round generates.
        if (MultiplayerWorld.IsMultiplayerGame) yield break;

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Vector3 spawnForward = Vector3.forward;
        Vector3 pos = trackStart
                    + spawnForward * carSpawnForwardOffset
                    + Vector3.up * carSpawnHeightOffset;
        Quaternion rot = Quaternion.LookRotation(spawnForward);

        GameObject existingCar = PlayerRegistry.LocalCar;
        if (existingCar != null)
        {
            var rb = existingCar.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            existingCar.transform.SetPositionAndRotation(pos, rot);
            // Single-player's version of the area teleport: the car is PLACED on the start line, so the
            // interpolator must not render it sliding there from wherever it was parked in the hub.
            MultiplayerWorld.ClearInterpolationHistory(rb);

            // Apply the spawn boost AFTER the teleport so velocity is preserved.
            // We need one more physics step for the new transform to register before
            // the velocity is applied — otherwise it can be cancelled by the
            // resolution of any tiny initial penetration with the track surface.
            if (rb != null && spawnVelocityMph > 0f)
            {
                yield return new WaitForFixedUpdate();
                ApplySpawnBoost(rb, spawnForward, spawnVelocityMph);
            }

            yield break;
        }

        if (carPrefab != null)
        {
            var spawned = Instantiate(carPrefab, pos, rot);
            Debug.LogWarning("[TrackGenerator] No tagged Player car in scene — " +
                             "instantiated from prefab. Camera and other references " +
                             "may need manual wiring.");

            var rb = spawned.GetComponent<Rigidbody>();
            if (rb != null && spawnVelocityMph > 0f)
            {
                yield return new WaitForFixedUpdate();
                ApplySpawnBoost(rb, spawnForward, spawnVelocityMph);
            }
        }
    }

    /// <summary>
    /// Applies a one-time velocity to the rigidbody along the given direction.
    /// Converts mph to m/s and uses Rigidbody.linearVelocity directly so the
    /// boost is instantaneous, not subject to acceleration over time.
    /// </summary>
    void ApplySpawnBoost(Rigidbody rb, Vector3 direction, float mphSpeed)
    {
        const float MPH_TO_MS = 0.44704f;
        float speedMs = mphSpeed * MPH_TO_MS;

        rb.linearVelocity = direction.normalized * speedMs;
    }

    void AttachCamera(Transform carTransform)
    {
        // Use the assigned camera if available, otherwise find one in the scene
        CameraFollow cam = followCamera;
        if (cam == null) cam = FindAnyObjectByType<CameraFollow>();

        if (cam == null)
        {
            Debug.LogWarning("[TrackGenerator] No CameraFollow found in scene.");
            return;
        }

        cam.target = carTransform;

        // Snap the camera to its starting offset immediately so the player
        // doesn't see a one-frame jump from origin to the car
        Vector3 snapPos = carTransform.TransformPoint(cam.offset);
        cam.transform.position = snapPos;
        cam.transform.LookAt(carTransform);
    }
}