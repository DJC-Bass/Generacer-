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

    [Header("Altitude Layering")]
    [Tooltip("Vertical spacing between adjacent paths so they don't intersect on the way back.")]
    public float altitudeLayerSpacing = 120f;

    [Header("Assets")]
    public Material roadMaterial;
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
    [Tooltip("Probability that any given convergence segment is replaced with a loop (0-1).")]
    [Range(0f, 1f)] public float loopChance = 0.2f;
    [Tooltip("Minimum loop radius (units).")]
    public float minLoopRadius = 200f;
    [Tooltip("Maximum loop radius (units).")]
    public float maxLoopRadius = 350f;
    [Tooltip("Lateral offset range applied to the post-loop segment (units). " +
             "Positive values shift the exit to the right of the entry, negative to the left.")]
    public float loopExitOffset = 300f;

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
        public float loopFlattenStart = 1f;   // 1 = no exit-flattening
        public Vector3 loopCenter;
        public float loopRadius;
        public Vector3 loopForward;     // direction of car travel when entering the loop
    }

    // Add as a field near allEdges
    private TrackEdge rootEdge;
    private readonly List<TrackEdge> allEdges = new List<TrackEdge>();
    private readonly List<TrackEdge> leafEdges = new List<TrackEdge>();
    private Vector3 trackStart;
    private Vector3 trackFinish;

    // -------------------------------------------------------
    //  Entry point
    // -------------------------------------------------------

    void Start()
    {
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

        // Start position: world origin plus random offsets so each play has a
        // uniquely-located starting point. Altitude is positive-only — start never
        // goes below baseAltitude.
        float startX = Random.Range(-startLateralVariance, startLateralVariance);
        float startY = baseAltitude + Random.Range(0f, startAltitudeVariance);
        float startZ = Random.Range(-startForwardVariance, startForwardVariance);

        trackStart = new Vector3(startX, startY, startZ);

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
            Instantiate(endPortalPrefab, trackFinish + Vector3.up * 2f, Quaternion.identity);

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
            handleStart = extraLength * 0.4f,
            handleEnd = extraLength * 0.4f,
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
        int numSegments = Random.Range(2, 5);

        var controlPositions = new List<Vector3> { leaf.endPos };

        Vector3 axis = (leafFinish - leaf.endPos);
        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.forward;
        Vector3 axisDir = axis.normalized;
        Vector3 sideAxis = Vector3.Cross(Vector3.up, axisDir).normalized;
        if (sideAxis.sqrMagnitude < 0.0001f) sideAxis = Vector3.right;

        float styleSwing = Random.Range(0.6f, 1.6f);
        float styleVertical = Random.Range(0.3f, 1.4f);
        int windingPattern = Random.Range(0, 3);

        float prevSide = 0f, prevVert = 0f;

        for (int i = 1; i < numSegments; i++)
        {
            float t = i / (float)numSegments;
            Vector3 straightPt = Vector3.Lerp(leaf.endPos, leafFinish, t);

            float sideOffset = ChooseOffset(windingPattern, t, prevSide,
                                             convergenceSwing * styleSwing);
            int vertWinding = (windingPattern + 1) % 3;
            float vertOffset = ChooseOffset(vertWinding, t, prevVert,
                                              convergenceAltitudeSwing * styleVertical);

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
            bool wantsLoop = (i > 0 && i < numSegments - 1) && Random.value < loopChance;

            if (wantsLoop)
            {
                previousEdge = BuildLoopSequence(previousEdge,
                                                  segStart, segStartDir,
                                                  segEnd, segEndDir,
                                                  segmentLength);
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
    /// Builds the loop as a tilted corkscrew spiral made of TWO cubic Bezier
    /// curves that meet at a flat top (apex), exactly as in the red/blue concept:
    ///
    ///   Curve 1 (entry -> apex):  bottom handle aligned with the incoming road
    ///                             (peel-up), top handle FLAT (horizontal).
    ///   Curve 2 (apex  -> exit):  top handle FLAT (horizontal, mirroring curve 1
    ///                             so the join is C1-continuous), bottom handle
    ///                             aligned with the outgoing road.
    ///
    /// The apex is lifted to 2*radius and drifted forward + sideways so the loop
    /// has genuine diameter and spirals past itself instead of pinching into a
    /// thin "8". Handles share the same flat direction and equal length at the
    /// apex, so they never cross each other or the track.
    /// </summary>
    TrackEdge BuildLoopSequence(TrackEdge parent,
                                 Vector3 approachStart, Vector3 approachStartDir,
                                 Vector3 originalEnd, Vector3 originalEndDir,
                                 float segmentLength)
    {
        float loopRadius = Random.Range(minLoopRadius, maxLoopRadius);

        // Horizontal forward direction the car is travelling on entry.
        Vector3 fwd = approachStartDir;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();

        // Horizontal sideways axis — the spiral drifts along this so it tilts
        // into a corkscrew rather than a flat planar loop.
        Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
        if (side.sqrMagnitude < 0.0001f) side = Vector3.right;

        // Pick a consistent drift direction (left or right) for this loop so the
        // whole spiral leans one way.
        float sideSign = (Random.value < 0.5f) ? -1f : 1f;

        // ----- Approach edge: blends the flat road up to the loop entry -----
        float approachLength = segmentLength * 0.25f;
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

        // ----- Key loop points -----
        // Spiral drift amounts: forward push keeps the top ahead of the bottom,
        // sideways push tilts the whole loop into a corkscrew.
        float forwardDrift = loopRadius * 0.6f;
        float sideDrift = loopRadius * 1f * sideSign;

        // Apex sits one full diameter up, drifted forward + sideways so the loop
        // reads as round and the two halves don't stack on the same vertical line.
        Vector3 apex = loopEntry
                     + Vector3.up * (loopRadius * 2f)
                     + fwd * forwardDrift
                     + side * (sideDrift * 0.5f);

        // Exit returns near the bottom but pushed forward + further sideways so it
        // clears the entry (this is the spiral's "step over").
        Vector3 loopExit = loopEntry
                         + fwd * (forwardDrift * 1.6f)
                         + side * sideDrift;

        // loopCenter for the mesh's inward-facing normal: mid-height, centred
        // between entry and exit in the horizontal plane.
        Vector3 loopCenter = loopEntry
                           + Vector3.up * loopRadius
                           + fwd * (forwardDrift * 0.6f)
                           + side * (sideDrift * 0.4f);

        // Flat (horizontal) tangent shared at the apex by both curves. Points
        // back against the entry direction, tilted toward the drift side so it
        // flows around the spiral. Kept horizontal (y stays ~0) = "flat on top".
        Vector3 apexFlatDir = (-fwd + side * (0.75f * sideSign));
        apexFlatDir.y = 0f;
        apexFlatDir.Normalize();

        // Handle length: half-loop spans ~180 deg, so each cubic needs generous
        // handles. ~1.3*r gives a clean round arc without overshoot/crossing.
        float handleLen = loopRadius * 1.6f;

        // ----- Curve 1: entry -> apex (the "blue" curve) -----
        // Bottom handle aligned with the road (fwd) so the road peels up smoothly.
        // Top handle flat (apexFlatDir).
        var firstHalf = new TrackEdge
        {
            startPos = loopEntry,
            startDir = fwd,                 // road-aligned peel-up at the bottom
            endPos = apex,
            endDir = apexFlatDir,           // flat on top
            handleStart = handleLen,
            handleEnd = handleLen,
            parent = approach,
            isLoop = true,
            loopCenter = loopCenter,
            loopRadius = loopRadius,
            loopForward = fwd
        };
        approach.children.Add(firstHalf);
        allEdges.Add(firstHalf);

        // ----- Curve 2: apex -> exit (the "red" curve) -----
        // Top handle flat — SAME direction as curve 1's end handle => the join at
        // the apex is C1-continuous (no kink, handles colinear, never cross).
        // Bottom handle aligned with the outgoing road direction.
        Vector3 exitDir = (originalEnd - loopExit);
        exitDir.y = 0f;
        if (exitDir.sqrMagnitude < 0.0001f) exitDir = fwd;
        exitDir.Normalize();

        var secondHalf = new TrackEdge
        {
            startPos = apex,
            startDir = apexFlatDir,         // flat on top, colinear with curve 1
            endPos = loopExit,
            endDir = exitDir,               // road-aligned at the bottom exit
            handleStart = handleLen,
            handleEnd = handleLen,
            parent = firstHalf,
            isLoop = true,
            loopCenter = loopCenter,
            loopRadius = loopRadius,
            loopForward = fwd
        };
        firstHalf.children.Add(secondHalf);
        allEdges.Add(secondHalf);

        // ----- Exit ramp: built as a NORMAL ROAD edge (isLoop = false) so it -----
        // shares BuildRoadMesh's orientation convention with postLoop, exactly the
        // way `approach` mirrors the entry. We carve the lower ~22% of the exit
        // half off the loop mesh and rebuild it as road, because by that point the
        // curve has rolled mostly horizontal and a road mesh is stable there.
        //
        // splitPos/splitDir are sampled from the SAME cubic the loop half uses, so
        // the ramp begins exactly where the loop geometry is, just meshed flat.
        float splitT = .90f;
        Vector3 splitPos = CubicBezier(apex, apex + apexFlatDir * handleLen,
                                       loopExit - exitDir * handleLen, loopExit, splitT);
        Vector3 splitDir = CubicBezierTangent(apex, apex + apexFlatDir * handleLen,
                                              loopExit - exitDir * handleLen, loopExit, splitT);

        // Shorten the loop half so it ends at the split instead of loopExit.
        secondHalf.endPos = splitPos;
        secondHalf.endDir = splitDir;
        // Re-tune its end handle so the curve up to the split keeps its shape.
        secondHalf.handleEnd = handleLen * splitT;

        float rampSpan = Vector3.Distance(splitPos, loopExit);
        var exitRamp = new TrackEdge
        {
            startPos = splitPos,
            startDir = splitDir,
            endPos = loopExit,
            endDir = exitDir,
            handleStart = rampSpan * 0.4f,
            handleEnd = rampSpan * 0.4f,
            parent = secondHalf,
            isLoop = false                  // <-- meshed as flat road, matches postLoop
        };
        secondHalf.children.Add(exitRamp);
        allEdges.Add(exitRamp);

        // ----- Post-loop edge: loopExit -> original segment end (road mesh) -----
        Vector3 postLoopStartDir = exitDir;

        var postLoop = new TrackEdge
        {
            startPos = loopExit,
            startDir = postLoopStartDir,
            endPos = originalEnd,
            endDir = originalEndDir,
            parent = exitRamp
        };
        TuneHandlesForLength(postLoop, segmentLength * 0.5f);

        float span = Vector3.Distance(postLoop.startPos, postLoop.endPos);
        float minHandle = span * 0.45f;
        postLoop.handleStart = Mathf.Max(postLoop.handleStart, minHandle);
        postLoop.handleEnd = Mathf.Max(postLoop.handleEnd, minHandle);

        exitRamp.children.Add(postLoop);
        allEdges.Add(postLoop);

        return postLoop;
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
        float arcLen = EstimateBezierLength(edge);
        int numPts = Mathf.Max(8, Mathf.RoundToInt(arcLen / waypointSpacing));

        edge.sampledPoints = new List<Vector3>(numPts + 1);
        for (int i = 0; i <= numPts; i++)
        {
            float t = i / (float)numPts;
            Vector3 pt = BezierPoint(edge, t);
            if (!edge.isLoop && pt.y < minAltitude) pt.y = minAltitude;  // don't clamp loop arcs
            edge.sampledPoints.Add(pt);
        }

        if (edge.isLoop)
        {
            // Loop arcs use the inward-facing loop mesh builder
            Vector3 rotationAxis = Vector3.Cross(edge.loopForward, Vector3.up).normalized;
            if (rotationAxis.sqrMagnitude < 0.0001f) rotationAxis = Vector3.right;
            BuildLoopMeshObject(edge, rotationAxis);
            return;
        }
        else
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
        if (shoulderWidth > 0f)
        {
            SpawnShoulder(obj.transform, spline, resolution, rightSide: true);
            SpawnShoulder(obj.transform, spline, resolution, rightSide: false);
        }
    }

    /// <summary>
    /// Spawns a loop's road mesh using the dedicated loop builder, with the
    /// road surface correctly oriented to face the loop's interior.
    /// </summary>
    void BuildLoopMeshObject(TrackEdge edge, Vector3 rotationAxis)
    {
        GameObject obj = new GameObject("RoadEdge_Loop");
        obj.transform.SetParent(transform);

        Mesh loopMesh = TrackMeshBuilder.BuildLoopMesh(
                    edge.sampledPoints, edge.loopCenter, rotationAxis,
                    roadWidth, roadThickness, uvTilingFactor,
                    edge.loopFlattenStart
                );

        obj.AddComponent<MeshFilter>().sharedMesh = loopMesh;
        obj.AddComponent<MeshCollider>().sharedMesh = loopMesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = GetRoadMaterial();

        int trackLayer = LayerMask.NameToLayer("Track");
        if (trackLayer >= 0) obj.layer = trackLayer;

        // Shoulders on both edges of the loop road
        if (shoulderWidth > 0f)
        {
            SpawnLoopShoulder(obj.transform, edge, rotationAxis, true);
            SpawnLoopShoulder(obj.transform, edge, rotationAxis, false);
        }
    }

    /// <summary>
    /// Spawns an emissive shoulder strip along one edge of the loop road.
    /// </summary>
    void SpawnLoopShoulder(Transform parent, TrackEdge edge, Vector3 rotationAxis, bool rightSide)
    {
        var shoulderObj = new GameObject(rightSide ? "LoopShoulderRight" : "LoopShoulderLeft");
        shoulderObj.transform.SetParent(parent);

        Mesh shoulderMesh = TrackMeshBuilder.BuildLoopShoulderMesh(
            edge.sampledPoints, edge.loopCenter, rotationAxis,
            roadWidth, shoulderWidth, roadThickness, rightSide, uvTilingFactor
        );

        shoulderObj.AddComponent<MeshFilter>().sharedMesh = shoulderMesh;
        shoulderObj.AddComponent<MeshCollider>().sharedMesh = shoulderMesh;

        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = shoulderMaterial != null ? shoulderMaterial : GetRoadMaterial();
    }

    /// <summary>
    /// Spawns a shoulder strip as a child of the road edge GameObject. The
    /// strip uses its own MeshFilter/MeshRenderer with the shoulder material
    /// so it can have emission separate from the main road. Also gets a
    /// MeshCollider so the car can drive on it just like the road.
    /// </summary>
    void SpawnShoulder(Transform parent, TrackSpline spline, int resolution, bool rightSide)
    {
        var shoulderObj = new GameObject(rightSide ? "ShoulderRight" : "ShoulderLeft");
        shoulderObj.transform.SetParent(parent);

        Mesh shoulderMesh = TrackMeshBuilder.BuildShoulderMesh(
            spline, roadWidth, shoulderWidth, resolution, roadThickness, rightSide, uvTilingFactor
        );

        shoulderObj.AddComponent<MeshFilter>().sharedMesh = shoulderMesh;
        shoulderObj.AddComponent<MeshCollider>().sharedMesh = shoulderMesh;

        var renderer = shoulderObj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = shoulderMaterial != null ? shoulderMaterial : GetRoadMaterial();
    }

    Material GetRoadMaterial()
    {
        if (roadMaterial != null) return roadMaterial;
        Material m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.mainTexture = ArrowTextureGenerator.Generate();
        return m;
    }

    // -------------------------------------------------------
    //  Mesh edges in correct order so parents are sampled first.
    // (Already guaranteed by allEdges.Add order — parents added before children.)
    // -------------------------------------------------------

    IEnumerator SpawnCarDelayed()
    {
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Vector3 spawnForward = Vector3.forward;
        Vector3 pos = trackStart
                    + spawnForward * carSpawnForwardOffset
                    + Vector3.up * carSpawnHeightOffset;
        Quaternion rot = Quaternion.LookRotation(spawnForward);

        GameObject existingCar = GameObject.FindWithTag("Player");
        if (existingCar != null)
        {
            var rb = existingCar.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            existingCar.transform.SetPositionAndRotation(pos, rot);

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