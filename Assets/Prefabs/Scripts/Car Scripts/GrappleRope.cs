using UnityEngine;

/// <summary>
/// The visual rope for the grappling hook: a many-segment VERLET-simulated string driving a
/// LineRenderer, pinned at one end to the car's muzzle and at the other to the hook head.
///
/// Deliberately visual-only. The actual tether physics is ONE distance constraint applied to the car's
/// rigidbody (see <see cref="GrappleHook"/>) — a real chain of jointed rigidbodies would stretch and go
/// unstable at this game's speeds (600 mph), while verlet points are stable, cheap, and give the sag,
/// whip and settle that reads as rope. Nothing here ever touches gameplay.
///
/// Created at runtime by GrappleHook (local car) and by GrappleReplicator (remote players' ropes), so
/// there is no prefab to wire — only the optional material, which falls back to a built-in unlit line.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class GrappleRope : MonoBehaviour
{
    [Tooltip("Number of rope points. More = smoother sag, slightly more cost. 24 reads as string.")]
    public int segments = 12;
    [Tooltip("Verlet solver iterations per frame. Higher = stiffer, less rubbery rope.")]
    public int solverIterations = 6;
    [Tooltip("Gravity applied to the rope points (units/s²). Only affects the LOOK of the sag.")]
    public float ropeGravity = -9f;
    [Tooltip("Velocity retained each frame (0..1). Lower = the rope settles faster.")]
    [Range(0f, 1f)] public float damping = 0.5f;
    [Tooltip("How much longer the rope is than the straight line between its ends. 1 = taut, " +
             "1.06 gives a natural slack curve.")]
    public float slack = .75f;
    [Tooltip("Rope thickness at the muzzle and at the hook end.")]
    public float startWidth = 0.50f;
    public float endWidth = 0.75f;

    private LineRenderer line;
    private Vector3[] points;
    private Vector3[] prevPoints;
    private bool initialised;

    // Live endpoints, pushed in each frame by the owner.
    private Vector3 startPoint;
    private Vector3 endPoint;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.startWidth = startWidth;
        line.endWidth = endWidth;
        line.numCapVertices = 2;
        line.textureMode = LineTextureMode.Tile;
        if (line.sharedMaterial == null)
        {
            // Unlit fallback so the rope is visible with zero setup. Assign a nicer material on the
            // LineRenderer if you want a textured cable.
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
                line.material = new Material(shader) { color = new Color(0f, 0f, 0f) };
        }
        AllocatePoints();
    }

    void AllocatePoints()
    {
        segments = Mathf.Max(segments, 2);
        points = new Vector3[segments];
        prevPoints = new Vector3[segments];
        line.positionCount = segments;
    }

    /// <summary>Owner calls this every frame with the current rope ends. The first call snaps the rope
    /// straight between them (so a freshly fired rope doesn't whip in from wherever it was last).</summary>
    public void SetEnds(Vector3 start, Vector3 end)
    {
        startPoint = start;
        endPoint = end;

        if (!initialised)
        {
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)(segments - 1);
                points[i] = Vector3.Lerp(start, end, t);
                prevPoints[i] = points[i];
            }
            initialised = true;
        }
    }

    /// <summary>Resets the rope so the next <see cref="SetEnds"/> snaps it straight again — call when
    /// the hook is re-fired, so it never lerps across the map from its last position.</summary>
    public void ResetShape() => initialised = false;

    void LateUpdate()
    {
        if (!initialised || points == null) return;

        Simulate();
        line.SetPositions(points);
    }

    void Simulate()
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        Vector3 gravityStep = new Vector3(0f, ropeGravity, 0f) * dt * dt;

        // --- Verlet integration: position implies velocity, so there's no velocity array to drift. ---
        for (int i = 0; i < segments; i++)
        {
            Vector3 velocity = (points[i] - prevPoints[i]) * damping;
            prevPoints[i] = points[i];
            points[i] += velocity + gravityStep;
        }

        // --- Constraint passes: hold each segment near its rest length, ends pinned. ---
        // Rest length includes `slack` so the rope hangs in a curve rather than a rigid straight line.
        float restLength = (Vector3.Distance(startPoint, endPoint) * slack) / (segments - 1);

        for (int pass = 0; pass < solverIterations; pass++)
        {
            points[0] = startPoint;                 // pinned to the muzzle
            points[segments - 1] = endPoint;        // pinned to the hook head

            for (int i = 0; i < segments - 1; i++)
            {
                Vector3 delta = points[i + 1] - points[i];
                float dist = delta.magnitude;
                if (dist < 1e-5f) continue;

                float error = (dist - restLength) / dist;
                Vector3 correction = delta * (error * 0.5f);

                // Endpoints are pinned, so their share of the correction goes entirely to the neighbour.
                if (i == 0) points[i + 1] -= correction * 2f;
                else if (i + 1 == segments - 1) points[i] += correction * 2f;
                else { points[i] += correction; points[i + 1] -= correction; }
            }
        }

        points[0] = startPoint;
        points[segments - 1] = endPoint;
    }
}
