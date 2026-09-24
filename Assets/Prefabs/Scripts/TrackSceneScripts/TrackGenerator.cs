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
    [Tooltip("Total length of every path from start to finish (units). " +
             "~22000 ≈ 3-4 minutes at 300mph average.")]
    public float targetBranchLength = 22000f;

    [Header("Vein Shape")]
    [Tooltip("Number of times the tree splits outward from the start. " +
             "2 levels = 4 paths, 3 levels = 8 paths.")]
    [Range(1, 4)] public int branchLevels = 3;
    [Tooltip("Total angular fan-out of the outermost branches (degrees). " +
             "60 = wide vein, 30 = narrow vein.")]
    public float fanAngleDeg = 60f;
    [Tooltip("How much the outermost branches arc outward before converging.")]
    public float lateralSweep = 4000f;

    [Tooltip("Minimum angle (degrees) each branch must diverge from its parent. " +
         "Prevents deep branches from running nearly parallel — keeps the tree " +
         "visually fanned even at higher branch levels.")]
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

    [Header("Rail Teeth")]
    [Tooltip("Raise square 'teeth' along both shoulders so turns read clearly, with open " +
             "gaps between them the car can still drive through to leave the track.")]
    public bool railTeeth = true;
    [Tooltip("Every Nth shoulder segment is raised into a tooth. 4 = one tooth then three " +
             "open segments; higher spreads them out; 1 = a continuous wall.")]
    [Min(1)] public int railToothEvery = 4;
    [Tooltip("How far each tooth rises straight up off the shoulder (units).")]
    public float railToothHeight = 4f;
    [Tooltip("On = the teeth are solid and the car bumps off them. Off = visual only; " +
             "the car passes straight through.")]
    public bool railTeethCollide = true;

    [Header("Altitude Layering")]
    [Tooltip("Vertical spacing between adjacent paths so they don't intersect on the way back.")]
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

    [Header("Altitude Variance")]
    [Tooltip("Maximum altitude swing per outward edge (units). " +
         "Each edge endpoint gets a random Y offset within ±this range.")]
    public float altitudeVariance = 600f;
    [Tooltip("How strongly the Bezier handles tilt up/down. " +
             "Higher = more dramatic dips/crests between waypoints.")]
    [Range(0f, 1f)] public float verticalHandleFactor = 0.4f;

    [Header("Organic Variance")]
    [Tooltip("How much each branch's angle, length, and curvature deviates from " +
         "its symmetric default (0 = perfect mirror, 1 = chaotic).")]
    [Range(0f, 1f)] public float branchVariance = 0.4f;

    [Header("Convergence Style")]
    [Tooltip("How much each convergence path swings sideways before converging. " +
         "Higher = more dramatic curves on the way back to the finish.")]
    public float convergenceSwing = 2500f;
    [Tooltip("How much altitude each convergence path can swing through during the return.")]
    public float convergenceAltitudeSwing = 800f;
    [Tooltip("Random ± deviation applied to Convergence Altitude Swing once per track " +
             "generation. 200 means each new track rolls a swing somewhere in " +
             "[base-200, base+200]. 0 = always exactly the base value.")]
    public float convergenceAltitudeSwingVariance = 0f;
    // Resolved per-generation value of convergenceAltitudeSwing (base ± variance).
    // Rolled once in GenerateTrack and read by BuildLeafConvergence.
    private float currentConvergenceAltitudeSwing;

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
    [Tooltip("Distance between adjacent path endpoints at the finish line (units). " +
         "0 = all paths converge at the same point (old behaviour).")]
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
    [Tooltip("Probability that any given convergence segment is replaced with a loop (0-1). " +
             "Rolled independently of Side Loop Chance; if both claim the same segment it's a " +
             "50/50 coin flip which one forms.")]
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
    [Tooltip("Probability that any given convergence segment is replaced with a side loop (0-1) — " +
             "a flat 360° left or right turn that passes over/under itself. Rolled independently " +
             "of Loop Chance; if both claim the same segment it's a 50/50 coin flip which one forms.")]
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
        public int railToothPhaseEnd;         // rail-tooth pattern position where this edge ends; its children carry it on

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
    }

    // Add as a field near allEdges
    private TrackEdge rootEdge;
    private readonly List<TrackEdge> allEdges = new List<TrackEdge>();
    private readonly List<TrackEdge> leafEdges = new List<TrackEdge>();
    private Vector3 trackStart;
    private Vector3 trackFinish;

    /// <summary>The live generator (the TrackScene holds exactly one). MultiplayerWorld uses it to
    /// teleport portal-entering players onto the track it generated.</summary>
    public static TrackGenerator Current { get; private set; }

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

        // Build this track's road + shoulder materials (instances sharing one randomised hue)
        // before any road edge is made.
        PrepareTrackMaterials();

        // Roll the convergence altitude swing once per generation so every leaf's
        // return path shares the same vertical character, but successive runs
        // (different seeds) vary.
        float vDev = Random.Range(-convergenceAltitudeSwingVariance,
                                   convergenceAltitudeSwingVariance);
        currentConvergenceAltitudeSwing = Mathf.Max(0f, convergenceAltitudeSwing + vDev);

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

        // Total path goes start → outward tip → finish
        // Half the length is outward, half is the convergence back
        float outwardLength = targetBranchLength * 0.5f;
        float inwardLength = targetBranchLength * 0.5f;

        // Finish position: well ahead of the leaves but with random lateral, vertical,
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

        trackFinish.y = Mathf.Max(trackFinish.y, minAltitude);

        // Phase 1 — build outward tree
        BuildOutwardTree(outwardLength);

        // Phase 2 — assign altitude layers to leaves so converging paths stay separated
        AssignAltitudeLayers();

        // Phase 3 — connect each leaf to the finish with a length-tuned Bezier
        BuildConvergence(inwardLength);

        // Phase 4 — mesh every edge
        foreach (var edge in allEdges) BuildEdgeMesh(edge);

        if (endPortalPrefab != null)
        {
            // Parented under the generator so it lives in the TrackScene (an un-parented Instantiate
            // goes to the ACTIVE scene — wrong in the multiplayer additive world) and is torn down
            // with the scene.
            var portal = Instantiate(endPortalPrefab, trackFinish + Vector3.up * 2f, Quaternion.identity);
            portal.transform.SetParent(transform, true);
        }

        StartCoroutine(SpawnCarDelayed());
    }

    // -------------------------------------------------------
    //  Phase 1 — outward tree
    // -------------------------------------------------------

    /// <summary>
    /// Recursively builds a binary tree of Bezier edges expanding outward.
    /// Each level halves the angular sector its parent occupied.
    /// All leaves end up at the same forward distance from start, evenly fanned.
    /// </summary>
    void BuildOutwardTree(float totalOutwardLength)
    {
        // Total outward length is split across levels — each child INHERITS the
        // remaining budget from its parent rather than using a fixed per-level
        // value. This lets some branches use up their length quickly (short
        // shallow paths) and others spread it across more levels (long deep paths).
        var root = new TrackEdge
        {
            startPos = trackStart,
            startDir = Vector3.forward,
            endPos = trackStart + Vector3.forward * (totalOutwardLength / branchLevels),
            endDir = Vector3.forward,
            handleStart = (totalOutwardLength / branchLevels) * 0.4f,
            handleEnd = (totalOutwardLength / branchLevels) * 0.4f,
            parent = null
        };
        allEdges.Add(root);
        rootEdge = root;

        float remainingLength = totalOutwardLength - (totalOutwardLength / branchLevels);
        BuildSubtree(root, level: 1, parentAngle: 0f, remainingLength: remainingLength);
    }

    /// <summary>
    /// Walks from root to a finish point, picking randomly at each fork.
    /// Returns a list of world-space centerline points along that single path.
    /// </summary>
    public List<Vector3> SampleRandomPath()
    {
        var points = new List<Vector3>();
        if (rootEdge == null) return points;

        TrackEdge current = rootEdge;
        while (current != null)
        {
            if (current.sampledPoints != null)
            {
                // Skip the first point of each edge after root to avoid duplicates
                // (each edge's first point equals the previous edge's last point)
                int startIdx = (points.Count == 0) ? 0 : 1;
                for (int i = startIdx; i < current.sampledPoints.Count; i++)
                    points.Add(current.sampledPoints[i]);
            }

            // Pick a random child if there are forks; null if we've reached a leaf
            if (current.children.Count == 0)
                current = null;
            else
                current = current.children[Random.Range(0, current.children.Count)];
        }

        return points;
    }

    /// <summary>
    /// Builds two children of `parent`, splitting the parent's heading angle.
    /// Recurses until branchLevels reached, then each leaf is registered.
    /// </summary>
    void BuildSubtree(TrackEdge parent, int level, float parentAngle, float remainingLength)
    {
        // Clean termination — no random early termination. Every branch goes the
        // full depth so the tree is symmetric and identical each generation.
        if (level > branchLevels || remainingLength < 100f)
        {
            leafEdges.Add(parent);
            return;
        }

        float levelsRemaining = branchLevels - level + 1;
        float edgeLength = remainingLength / levelsRemaining;

        // Deterministic divergence: each level diverges by a clean fraction of the
        // fan angle, clamped to the minimum. No angle jitter, no length jitter.
        float baseDiverge = (fanAngleDeg * 0.5f) / Mathf.Pow(2f, level - 1);
        baseDiverge = Mathf.Max(baseDiverge, minBranchAngle);

        float leftAngle = parentAngle - baseDiverge;
        float rightAngle = parentAngle + baseDiverge;

        var left = MakeChildEdge(parent, leftAngle, edgeLength);
        var right = MakeChildEdge(parent, rightAngle, edgeLength);

        parent.children.Add(left);
        parent.children.Add(right);
        allEdges.Add(left);
        allEdges.Add(right);

        BuildSubtree(left, level + 1, leftAngle, remainingLength - edgeLength);
        BuildSubtree(right, level + 1, rightAngle, remainingLength - edgeLength);
    }

    /// <summary>
    /// Creates an edge starting at parent's endpoint, heading in parent's
    /// exit direction, and ending at a target position offset by lateralAngle.
    /// Both endpoint tangents are aligned to the angle so the Bezier starts
    /// in the parent's direction and ends pointing along the new angle.
    /// </summary>
    TrackEdge MakeChildEdge(TrackEdge parent, float angleFromForward, float edgeLength)
    {
        Vector3 startPos = parent.endPos;
        Vector3 startDir = parent.endDir;

        // Clean lateral endpoint — deterministic sweep, no jitter (shape unchanged)
        float lateralFactor = Mathf.Sin(angleFromForward * Mathf.Deg2Rad);
        Vector3 endPos = startPos
                              + Vector3.forward * edgeLength
                              + Vector3.right * lateralFactor * lateralSweep
                                                * (edgeLength / (targetBranchLength * 0.5f));

        // Altitude variance restored — random Y delta from parent, accumulating
        // gentle hills along the outward tree. Only the altitude varies; the
        // horizontal X/Z position above is untouched, so the tree's shape from
        // a top-down view is identical every generation.
        float yDelta = Random.Range(-altitudeVariance, altitudeVariance);
        endPos.y = Mathf.Max(parent.endPos.y + yDelta, minAltitude);

        // End direction: horizontal heading from the angle, then pitched up/down
        // to match the altitude change so the Bezier flows smoothly over crests
        // and into valleys instead of kinking vertically at each fork.
        Vector3 endDirHorizontal = Quaternion.AngleAxis(angleFromForward, Vector3.up) * Vector3.forward;
        float endPitchDeg = Random.Range(-altitudeVariance, altitudeVariance)
                                 / edgeLength * Mathf.Rad2Deg * verticalHandleFactor;
        Quaternion pitchRot = Quaternion.AngleAxis(endPitchDeg,
                                                         Vector3.Cross(Vector3.up, endDirHorizontal));
        Vector3 endDir = (pitchRot * endDirHorizontal).normalized;

        return new TrackEdge
        {
            startPos = startPos,
            startDir = startDir,
            endPos = endPos,
            endDir = endDir,
            handleStart = edgeLength * 0.45f,
            handleEnd = edgeLength * 0.45f,
            parent = parent
        };
    }

    /// <summary>
    /// Extends an edge that's been chosen for early termination, so it still
    /// reaches the same total forward distance as fully-branched paths. The
    /// added segment uses the same Bezier style as a regular branch but heads
    /// straight along the parent's exit angle without any divergence.
    /// </summary>
    void ExtendLeafForward(TrackEdge parent, float parentAngle, float extraLength)
    {
        // Direction the parent was already heading
        Vector3 fwdDir = Quaternion.AngleAxis(parentAngle, Vector3.up) * Vector3.forward;

        // Endpoint forward by extraLength, with mild lateral and altitude jitter
        // so even early-terminated paths have visual interest at their endpoints.
        float lateralFactor = Mathf.Sin(parentAngle * Mathf.Deg2Rad);
        Vector3 endPos = parent.endPos
                       + fwdDir * extraLength
                       + Vector3.right * lateralFactor * lateralSweep
                                       * 0.3f * branchVariance
                                       * (extraLength / (targetBranchLength * 0.5f));

        float yDelta = Random.Range(-altitudeVariance, altitudeVariance) * 0.7f;
        endPos.y = Mathf.Max(parent.endPos.y + yDelta, minAltitude);

        var extension = new TrackEdge
        {
            startPos = parent.endPos,
            startDir = parent.endDir,
            endPos = endPos,
            endDir = fwdDir,
            handleStart = extraLength * 0.8f,
            handleEnd = extraLength * 0.8f,
            parent = parent
        };

        parent.children.Add(extension);
        allEdges.Add(extension);

        // The extension itself becomes the leaf — replace the parent reference
        // in the leaf list when this returns.
        // (Handled by the caller adding `parent` to leafEdges, but we want the
        // extension to be the leaf, not the parent.)
    }

    // -------------------------------------------------------
    //  Phase 2 — altitude layering
    // -------------------------------------------------------

    /// <summary>
    /// Sorts leaves by their X position (lateral fan order) and assigns each
    /// a unique altitude offset. Adjacent leaves are at different altitudes,
    /// so converging paths can pass over/under each other cleanly.
    /// </summary>
    void AssignAltitudeLayers()
    {
        if (leafEdges.Count <= 1) return;

        leafEdges.Sort((a, b) => a.endPos.x.CompareTo(b.endPos.x));

        int n = leafEdges.Count;
        for (int i = 0; i < n; i++)
        {
            // -1 to +1 mapped across leaves
            float t = (n == 1) ? 0f : (i / (float)(n - 1)) * 2f - 1f;
            float yOffset = t * altitudeLayerSpacing * (n - 1) * 0.5f;

            Vector3 endPos = leafEdges[i].endPos;
            endPos.y = Mathf.Max(endPos.y + yOffset, minAltitude);
            leafEdges[i].endPos = endPos;
        }
    }

    // -------------------------------------------------------
    //  Phase 3 — convergence
    // -------------------------------------------------------

    /// <summary>
    /// Each leaf gets a single Bezier edge to the finish. Handle lengths are
    /// tuned per-leaf so the arc length equals the same target for every path,
    /// regardless of how far that leaf sits from the finish.
    /// </summary>
    void BuildConvergence(float convergeLength)
    {
        // Sort leaves left-to-right so endpoints fan out in their natural order.
        // Use the lateral axis perpendicular to the start→finish direction.
        Vector3 finishAxis = (trackFinish - trackStart);
        finishAxis.y = 0f;
        if (finishAxis.sqrMagnitude < 0.0001f) finishAxis = Vector3.forward;
        Vector3 axisDir = finishAxis.normalized;
        Vector3 sideAxis = Vector3.Cross(Vector3.up, axisDir).normalized;

        // Sort leaves by their projection onto the side axis — leftmost leaf
        // gets the leftmost finish point, rightmost gets the rightmost
        leafEdges.Sort((a, b) =>
        {
            float aProj = Vector3.Dot(a.endPos - trackStart, sideAxis);
            float bProj = Vector3.Dot(b.endPos - trackStart, sideAxis);
            return aProj.CompareTo(bProj);
        });

        // Compute each leaf's individual finish point, fanned across a horizontal line
        int n = leafEdges.Count;
        var leafFinishPoints = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float t = (n == 1) ? 0f : (i / (float)(n - 1)) * 2f - 1f;  // -1 to +1
            float lateralOffset = t * finishLateralSpacing * (n - 1) * 0.5f;

            Vector3 leafFinish = trackFinish + sideAxis * lateralOffset;
            leafFinish.y = trackFinish.y;  // all endpoints at same altitude
            leafFinishPoints.Add(leafFinish);
        }

        // Compute average arrival direction per-leaf so each path arrives at
        // its own endpoint heading naturally toward it
        var arrivalDirs = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            Vector3 toFinish = (leafFinishPoints[i] - leafEdges[i].endPos);
            if (toFinish.sqrMagnitude < 0.0001f) toFinish = Vector3.forward;
            arrivalDirs.Add(toFinish.normalized);
        }

        // Build each leaf's convergence to its own finish point
        for (int i = 0; i < n; i++)
            BuildLeafConvergence(leafEdges[i], leafFinishPoints[i],
                                  arrivalDirs[i], convergeLength);
    }

    /// <summary>
    /// Builds a chain of 2–4 Bezier segments from leaf to finish.
    /// Each control point along the chain is offset in a random direction
    /// (lateral and vertical) producing genuinely different curve characters
    /// per leaf. Tangent continuity at each midpoint guarantees smooth joins.
    /// </summary>
    void BuildLeafConvergence(TrackEdge leaf, Vector3 leafFinish,
                               Vector3 arrivalDir, float convergeLength)
    {
        // Minimum of 3 winding segments so no branch is a near-straight short shot.
        // Every branch gets enough intermediate control points to wind to the same
        // length as its neighbours, so no path is a shortcut.
        int numSegments = Random.Range(11, 12);   // 3 or 4

        var controlPositions = new List<Vector3> { leaf.endPos };

        Vector3 axis = (leafFinish - leaf.endPos);
        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.forward;
        Vector3 axisDir = axis.normalized;
        Vector3 sideAxis = Vector3.Cross(Vector3.up, axisDir).normalized;
        if (sideAxis.sqrMagnitude < 0.0001f) sideAxis = Vector3.right;

        float styleSwing = Random.Range(.5f, 1.5f);
        float styleVertical = Random.Range(.2f, .4f);
        int windingPattern = Random.Range(0, 192);

        float prevSide = 0f, prevVert = 0f;

        for (int i = 1; i < numSegments; i++)
        {
            float t = i / (float)numSegments;
            Vector3 straightPt = Vector3.Lerp(leaf.endPos, leafFinish, t);

            float sideOffset = ChooseOffset(windingPattern, t, prevSide,
                                             convergenceSwing * styleSwing);
            int vertWinding = (windingPattern + 1) % 3;
            float vertOffset = ChooseOffset(vertWinding, t, prevVert,
                                              currentConvergenceAltitudeSwing * styleVertical);

            Vector3 controlPos = straightPt + sideAxis * sideOffset + Vector3.up * vertOffset;
            controlPos.y = Mathf.Max(controlPos.y, minAltitude);

            controlPositions.Add(controlPos);
            prevSide = sideOffset;
            prevVert = vertOffset;
        }
        controlPositions.Add(leafFinish);

        var controlDirs = new List<Vector3>();
        controlDirs.Add(leaf.endDir);

        for (int i = 1; i < controlPositions.Count - 1; i++)
        {
            Vector3 dir = (controlPositions[i + 1] - controlPositions[i - 1]).normalized;
            if (dir.sqrMagnitude < 0.0001f) dir = axisDir;
            controlDirs.Add(dir);
        }
        controlDirs.Add(arrivalDir);

        float segmentLength = convergeLength / numSegments;
        TrackEdge previousEdge = leaf;
        for (int i = 0; i < numSegments; i++)
        {
            Vector3 segStart = controlPositions[i];
            Vector3 segEnd = controlPositions[i + 1];
            Vector3 segStartDir = controlDirs[i];
            Vector3 segEndDir = controlDirs[i + 1];

            // Decide whether this segment gets a loop. Don't put a loop on the very
            // first or last segment — the first feels disorienting right after the
            // outward tree, and the last would dump the player at the finish portal
            // mid-loop. Middle segments are best for loops.
            bool middleSegment = i > 0 && i < numSegments - 1;
            bool wantsLoop = middleSegment && Random.value < loopChance;

            // Side loops roll independently. The roll is skipped entirely while they're disabled
            // so a given seed generates exactly the same track it did before side loops existed.
            bool wantsSideLoop = middleSegment && sideLoopChance > 0f && Random.value < sideLoopChance;

            // Both claim this segment: 50/50 which one forms.
            if (wantsLoop && wantsSideLoop)
            {
                if (Random.value < 0.5f) wantsSideLoop = false;
                else wantsLoop = false;
            }

            if (wantsLoop || wantsSideLoop)
            {
                previousEdge = BuildLoopSequence(previousEdge,
                                                  segStart, segStartDir,
                                                  segEnd, segEndDir,
                                                  segmentLength, sideLoop: wantsSideLoop);
            }
            else
            {
                // Normal segment — Bezier as before
                var seg = new TrackEdge
                {
                    startPos = segStart,
                    startDir = segStartDir,
                    endPos = segEnd,
                    endDir = segEndDir,
                    parent = previousEdge
                };
                TuneHandlesForLength(seg, segmentLength);

                float span = Vector3.Distance(seg.startPos, seg.endPos);
                float minHandle = span * 0.45f;
                seg.handleStart = Mathf.Max(seg.handleStart, minHandle);
                seg.handleEnd = Mathf.Max(seg.handleEnd, minHandle);

                previousEdge.children.Add(seg);
                allEdges.Add(seg);
                previousEdge = seg;
            }
        }
    }

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
    /// </summary>
    TrackEdge BuildLoopSequence(TrackEdge parent,
                                 Vector3 approachStart, Vector3 approachStartDir,
                                 Vector3 originalEnd, Vector3 originalEndDir,
                                 float segmentLength, bool sideLoop)
    {
        float loopRadius = sideLoop ? Random.Range(minSideLoopRadius, maxSideLoopRadius)
                                    : Random.Range(minLoopRadius, maxLoopRadius);

        // Horizontal entry direction.
        Vector3 fwd = approachStartDir;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        // ----- Approach edge: flat road blending up to the loop entry. -----
        float approachLength = segmentLength * .50f;
        Vector3 loopEntry = approachStart + fwd * approachLength;

        var approach = new TrackEdge
        {
            startPos = approachStart,
            startDir = approachStartDir,
            endPos = loopEntry,
            endDir = fwd,
            handleStart = approachLength * 0.4f,
            handleEnd = approachLength * 0.4f,
            parent = parent
        };
        parent.children.Add(approach);
        allEdges.Add(approach);

        Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
        if (side.sqrMagnitude < 0.0001f) side = Vector3.right;

        int loopSamples = Mathf.Max(48, Mathf.RoundToInt(loopRadius * 0.5f));
        Vector3 loopExit;
        Vector3 loopCenter;   // only used as a fallback by the mesh; a sensible value
        List<Vector3> loopPts, loopNrm;

        if (sideLoop)
        {
            // ----- Side loop exit point: offset forward + up/down from the entry. -----
            // The up/down gap is the clearance where the road passes over itself.
            // Left vs right turn and climb vs descend are each a 50/50 roll.
            float turnSign = (Random.value < 0.5f) ? -1f : 1f;   // -1 = left loop, +1 = right loop
            float vertSign = (Random.value < 0.5f) ? -1f : 1f;   // -1 = exit below entry, +1 = above
            float vertOffset = Random.Range(minSideLoopExitOffset, maxSideLoopExitOffset);

            // Never descend through the altitude floor — climb instead.
            if (loopEntry.y - vertOffset < minAltitude) vertSign = 1f;

            loopExit = loopEntry + Vector3.up * (vertOffset * vertSign)
                                 + fwd * sideLoopExitForwardOffset;

            Vector3 turnDir = side * turnSign;
            loopPts = BuildPliableSideLoopPoints(loopEntry, fwd, turnDir, loopExit, loopRadius, loopSamples, out loopNrm);
            loopCenter = loopEntry + turnDir * loopRadius;
        }
        else
        {
            // ----- Loop exit point: offset forward + sideways from the entry. -----
            // The offset magnitude vs. loopRadius is what makes it loop (tight) or
            // arch (stretched) — the pliable-wire behaviour.
            float sideSign = (Random.value < 0.5f) ? -1f : 1f;

            // Sideways gap comes from Loop Exit Offset (slides the exit left/right,
            // like the ribbon's far end). Forward/back comes from its own field so the
            // two axes are independent and can be tuned separately.
            // Random sideways gap within the configured band, like Loop Radius.
            float sideOffset = Random.Range(minLoopExitOffset, maxLoopExitOffset);
            float sideDrift = sideOffset * sideSign;
            float forwardDrift = loopExitForwardOffset;

            loopExit = loopEntry + side * sideDrift + fwd * forwardDrift;

            // ----- Generate the continuous loop centerline. -----
            loopPts = BuildPliableLoopPoints(loopEntry, fwd, loopExit, loopRadius, loopSamples, out loopNrm);
            loopCenter = loopEntry + Vector3.up * loopRadius;
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
        approach.children.Add(loop);
        allEdges.Add(loop);

        // ----- Post-loop edge: exit -> original segment end (normal road). -----
        // The loop ends with a horizontal-ish tangent (the RMF + drift flattens the
        // ends), so this picks up smoothly just like the approach feeds the entry.
        Vector3 loopExitDir = loop.endDir;
        loopExitDir.y = 0f;
        if (loopExitDir.sqrMagnitude < 0.0001f) loopExitDir = fwd;
        loopExitDir.Normalize();

        var postLoop = new TrackEdge
        {
            startPos = loopExit,
            startDir = loopExitDir,
            endPos = originalEnd,
            endDir = originalEndDir,
            parent = loop
        };
        TuneHandlesForLength(postLoop, segmentLength * 0.5f);

        float span = Vector3.Distance(postLoop.startPos, postLoop.endPos);
        float minHandle = span * 0.45f;
        postLoop.handleStart = Mathf.Max(postLoop.handleStart, minHandle);
        postLoop.handleEnd = Mathf.Max(postLoop.handleEnd, minHandle);

        loop.children.Add(postLoop);
        allEdges.Add(postLoop);

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

    /// <summary>
    /// Returns a signed offset based on a winding pattern so different paths
    /// have characteristically different shapes rather than all looking similar.
    /// </summary>
    float ChooseOffset(int pattern, float t, float prevOffset, float maxAmplitude)
    {
        float magnitude = Random.Range(0.5f, 1f) * maxAmplitude;

        switch (pattern)
        {
            case 0:  // Zigzag — alternate sign each control point
                return -Mathf.Sign(prevOffset == 0f ? Random.Range(-1f, 1f) : prevOffset)
                      * magnitude;

            case 1:  // Sweep — keep same sign, varying magnitude
                float sweepSign = (prevOffset == 0f) ? Mathf.Sign(Random.Range(-1f, 1f)) : Mathf.Sign(prevOffset);
                return sweepSign * magnitude * Mathf.Sin(t * Mathf.PI);

            default: // Random — every control point picks an independent direction
                return Random.Range(-1f, 1f) * magnitude;
        }
    }

    /// <summary>
    /// Iteratively scales handle lengths so the resulting Bezier's arc length
    /// approaches targetLength. Both handles are scaled together for symmetry.
    /// </summary>
    void TuneHandlesForLength(TrackEdge edge, float targetLength)
    {
        float D = Vector3.Distance(edge.startPos, edge.endPos);

        // Starting guess: handles scaled so Bezier is ~targetLength long.
        // For a roughly straight Bezier, length ≈ D + 0.5 * (h1 + h2) * curvature factor
        float handle = Mathf.Max((targetLength - D) * 0.5f, D * 0.2f);

        for (int iter = 0; iter < 8; iter++)
        {
            // Cap handles so the curve never loops on itself
            handle = Mathf.Clamp(handle, D * 0.05f, D * 1.4f);

            edge.handleStart = handle;
            edge.handleEnd = handle;

            float estLen = EstimateBezierLength(edge);
            if (estLen < 0.01f) break;

            // Bezier arc length scales sub-linearly with handle length —
            // damped correction prevents oscillation
            float ratio = targetLength / estLen;
            handle *= Mathf.Pow(ratio, 0.6f);
        }

        handle = Mathf.Clamp(handle, D * 0.05f, D * 1.4f);
        edge.handleStart = handle;
        edge.handleEnd = handle;
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
    //  Phase 4 — meshing
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

        // Main road mesh (unchanged)
        Mesh roadMesh = TrackMeshBuilder.BuildRoadMesh(
            spline, roadWidth, resolution, roadThickness, uvTilingFactor
        );
        obj.AddComponent<MeshFilter>().sharedMesh = roadMesh;
        obj.AddComponent<MeshCollider>().sharedMesh = roadMesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = GetRoadMaterial();

        // Shoulder strips on both sides
        int toothPhase = RailToothPhaseAtStart(edge);
        if (shoulderWidth > 0f)
        {
            SpawnShoulder(obj.transform, spline, resolution, rightSide: true, toothPhase);
            SpawnShoulder(obj.transform, spline, resolution, rightSide: false, toothPhase);
        }
        SetRailToothPhaseEnd(edge, toothPhase, resolution);

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

        if (!edge.isSideLoop) obj.tag = "Loop";

        int toothPhase = RailToothPhaseAtStart(edge);
        if (shoulderWidth > 0f)
        {
            SpawnLoopShoulder(obj.transform, edge, Vector3.zero, true, toothPhase);
            SpawnLoopShoulder(obj.transform, edge, Vector3.zero, false, toothPhase);
        }
        SetRailToothPhaseEnd(edge, toothPhase, edge.sampledPoints.Count - 1);

        // Put the loop mesh and its shoulder children on the Track layer.
        ApplyTrackLayer(obj);
    }

    void SpawnLoopShoulder(Transform parent, TrackEdge edge, Vector3 rotationAxis, bool rightSide, int toothPhase)
    {
        var shoulderObj = new GameObject(rightSide ? "LoopShoulderRight" : "LoopShoulderLeft");
        shoulderObj.transform.SetParent(parent);
        if (!edge.isSideLoop) shoulderObj.tag = "Loop";

        Mesh shoulderMesh = TrackMeshBuilder.BuildLoopShoulderMeshExplicit(
                    edge.sampledPoints, edge.loopNormals, roadWidth, shoulderWidth, roadThickness, rightSide, uvTilingFactor);

        shoulderObj.AddComponent<MeshFilter>().sharedMesh = shoulderMesh;
        shoulderObj.AddComponent<MeshCollider>().sharedMesh = shoulderMesh;
        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetShoulderMaterial();

        SpawnRailTeeth(shoulderObj, shoulderMesh, renderer.sharedMaterial, toothPhase);
    }

    /// <summary>
    /// Spawns a shoulder strip as a child of the road edge GameObject. The
    /// strip uses its own MeshFilter/MeshRenderer with the shoulder material
    /// so it can have emission separate from the main road. Also gets a
    /// MeshCollider so the car can drive on it just like the road.
    /// </summary>
    void SpawnShoulder(Transform parent, TrackSpline spline, int resolution, bool rightSide, int toothPhase)
    {
        var shoulderObj = new GameObject(rightSide ? "ShoulderRight" : "ShoulderLeft");
        shoulderObj.transform.SetParent(parent);

        Mesh shoulderMesh = TrackMeshBuilder.BuildShoulderMesh(
            spline, roadWidth, shoulderWidth, resolution, roadThickness, rightSide, uvTilingFactor
        );

        shoulderObj.AddComponent<MeshFilter>().sharedMesh = shoulderMesh;
        shoulderObj.AddComponent<MeshCollider>().sharedMesh = shoulderMesh;

        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetShoulderMaterial();

        SpawnRailTeeth(shoulderObj, shoulderMesh, renderer.sharedMaterial, toothPhase);
    }

    /// <summary>
    /// Adds the rail teeth for one shoulder as a child of it, in the shoulder's own
    /// material. Children of the shoulder, so they land on the Track layer with it and
    /// take its tag (a loop's teeth count as loop, like its shoulders).
    /// </summary>
    void SpawnRailTeeth(GameObject shoulderObj, Mesh shoulderMesh, Material material, int toothPhase)
    {
        if (!railTeeth || railToothHeight <= 0f) return;

        Mesh teethMesh = TrackMeshBuilder.BuildShoulderTeethMesh(
            shoulderMesh.vertices, railToothEvery, railToothHeight, toothPhase);
        if (teethMesh.vertexCount == 0) return;   // edge too short to hold a tooth

        var teethObj = new GameObject("RailTeeth");
        teethObj.transform.SetParent(shoulderObj.transform);
        teethObj.tag = shoulderObj.tag;

        teethObj.AddComponent<MeshFilter>().sharedMesh = teethMesh;
        if (railTeethCollide) teethObj.AddComponent<MeshCollider>().sharedMesh = teethMesh;
        teethObj.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    /// <summary>
    /// Where the tooth pattern stands at the start of `edge`: carried on from its parent
    /// so the teeth keep an even rhythm across edge joins instead of restarting.
    /// </summary>
    int RailToothPhaseAtStart(TrackEdge edge) => edge.parent != null ? edge.parent.railToothPhaseEnd : 0;

    void SetRailToothPhaseEnd(TrackEdge edge, int startPhase, int segments)
    {
        edge.railToothPhaseEnd = (startPhase + segments) % Mathf.Max(1, railToothEvery);
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