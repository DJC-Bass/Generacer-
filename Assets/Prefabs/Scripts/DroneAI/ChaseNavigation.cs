using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Ground routes for chase-mode cars (<see cref="DroneCar.chaseMode"/>): the way around walls, through
/// doorways and up ramps to the player, where a straight line would drive into the first wall. The
/// ClipperEnding garage is the case that needs it — its cars start behind a wall with one opening.
///
/// Builds a NavMesh at runtime, in the background, from the static colliders around the chase (anything
/// with no Rigidbody — cars, drones and boulders are what's being routed, not obstacles), the first time
/// a route is asked for. Nothing needs baking or placing in a scene. Until it's ready, or wherever it
/// can't find a way, chasers simply steer straight at their target as they always did.
///
/// One mesh per scene, grown in the background whenever a chase strays outside it, and replaced when
/// the active scene changes.
///
/// ⚠️ Only READABLE meshes are on the map (a model's import settings: Read/Write). A built game can't read
/// the rest, so they're left off in the editor too — what you test is what ships, and Unity doesn't log an
/// error per mesh per bake reading them editor-only. Where an unreadable floor leaves a gap, a car steers
/// straight; an unreadable wall isn't on the map, so it'll drive into it. The ClipperEnding building is
/// readable; the hub's models aren't, so the hub swarm simply chases straight, as it always did.
/// </summary>
public static class ChaseNavigation
{
    // A car, not a person: wide berth around corners, gentle ramps only.
    const float AgentRadius = 1.5f;
    const float AgentHeight = 2f;
    const float AgentSlope = 35f;
    const float AgentClimb = 0.5f;

    /// <summary>How much ground around the chase each bake covers (units, each way), so the map isn't
    /// rebuilt every time the player moves a little.</summary>
    const float HorizontalMargin = 150f;
    const float VerticalMargin = 60f;

    /// <summary>How far a car or player may be from the mesh and still be placed on it (units) — a
    /// player mid-jump is above it.</summary>
    const float SnapDistance = 8f;

    /// <summary>How far above or below a car its spot on the map may be (units). Any further and it's
    /// the top of something nearby, over a gap in the map — no route then, rather than one over a roof.</summary>
    const float MaxStandHeight = 3f;

    /// <summary>Chases further apart than this (units, across) aren't routed: no ground needs a map that
    /// big, and it keeps a stray target — another area of a multiplayer world, 30 km off — from
    /// asking for one.</summary>
    const float MaxRouteSpan = 1000f;

    static NavMeshBuildSettings settings;
    static bool haveSettings;
    static NavMeshData data;
    static NavMeshDataInstance instance;
    static AsyncOperation bake;
    static Bounds bakedBounds;
    static Scene bakedScene;
    static NavMeshPath path;
    static readonly Vector3[] cornerBuffer = new Vector3[64];
    static readonly List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
    static readonly List<NavMeshBuildMarkup> noMarkups = new List<NavMeshBuildMarkup>();
    static readonly SortedSet<string> unreadable = new SortedSet<string>();
    static Scene reportedScene;

    /// <summary>Fills <paramref name="corners"/> with a ground route from <paramref name="from"/> toward
    /// <paramref name="to"/>, starting with the point on the map under <paramref name="from"/>. A target
    /// the map can't reach gets the nearest reachable point. False when there's no route (yet) — steer
    /// straight instead.</summary>
    public static bool TryRoute(Vector3 from, Vector3 to, List<Vector3> corners)
    {
        Vector3 span = to - from;
        if (Mathf.Abs(span.y) > VerticalMargin || new Vector2(span.x, span.z).magnitude > MaxRouteSpan) return false;
        if (!Covers(from, to)) return false;

        var filter = new NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = NavMesh.AllAreas };
        if (!NavMesh.SamplePosition(from, out var start, SnapDistance, filter)
            || Mathf.Abs(start.position.y - from.y) > MaxStandHeight) return false;
        if (!NavMesh.SamplePosition(to, out var end, SnapDistance, filter)
            || end.position.y > to.y + MaxStandHeight) return false;   // under the player, never above them

        if (path == null) path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, end.position, filter, path)
            || path.status == NavMeshPathStatus.PathInvalid) return false;

        int count = path.GetCornersNonAlloc(cornerBuffer);
        if (count < 2) return false;
        corners.Clear();
        for (int i = 0; i < count; i++) corners.Add(cornerBuffer[i]);
        return true;
    }

    /// <summary>Is there a finished map under both points? If not, starts (or grows) one in the background
    /// and answers false meanwhile.</summary>
    static bool Covers(Vector3 a, Vector3 b)
    {
        var scene = SceneManager.GetActiveScene();
        if (data != null && bakedScene != scene) Clear();   // last scene's map
        if (bake != null && !bake.isDone) return false;     // under way

        if (data != null && Inside(a) && Inside(b)) return true;

        var want = new Bounds(a, Vector3.zero);
        want.Encapsulate(b);
        want.Expand(new Vector3(2f * HorizontalMargin, 2f * VerticalMargin, 2f * HorizontalMargin));
        if (data != null) want.Encapsulate(bakedBounds);   // grow, never lose ground already mapped
        Bake(want, scene);
        return false;
    }

    /// <summary>Inside the mapped area with room to spare, so a route doesn't run off its edge.</summary>
    static bool Inside(Vector3 p)
    {
        Vector3 d = p - bakedBounds.center, e = bakedBounds.extents;
        return Mathf.Abs(d.x) < e.x - 20f && Mathf.Abs(d.z) < e.z - 20f && Mathf.Abs(d.y) < e.y - 5f;
    }

    static void Bake(Bounds bounds, Scene scene)
    {
        if (!haveSettings)
        {
            settings = NavMesh.CreateSettings();
            settings.agentRadius = AgentRadius;
            settings.agentHeight = AgentHeight;
            settings.agentSlope = AgentSlope;
            settings.agentClimb = AgentClimb;
            haveSettings = true;
        }

        sources.Clear();
        NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, noMarkups, sources);
        unreadable.Clear();
        sources.RemoveAll(s =>
        {
            if (s.component is Collider c && (c.isTrigger || c.attachedRigidbody != null)) return true;
            if (s.shape == NavMeshBuildSourceShape.Mesh && s.sourceObject is Mesh m && !m.isReadable)
            {
                unreadable.Add(m.name);
                return true;
            }
            return false;
        });
        if (unreadable.Count > 0 && reportedScene != scene)
        {
            reportedScene = scene;   // once per scene, not per bake
            Debug.Log($"[ChaseNavigation] {scene.name}: the chase map leaves out {unreadable.Count} mesh(es) without " +
                      $"Read/Write ({string.Join(", ", unreadable)}). Chasing cars drive straight across gaps they " +
                      "leave. Not an error — tick Read/Write on a model's import settings only if chasers need to " +
                      "route around it.");
        }

        if (data == null)
        {
            data = new NavMeshData(settings.agentTypeID);
            instance = NavMesh.AddNavMeshData(data);
        }
        bake = NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
        bakedBounds = bounds;
        bakedScene = scene;
    }

    static void Clear()
    {
        if (instance.valid) instance.Remove();
        data = null;
        bake = null;
    }
}
