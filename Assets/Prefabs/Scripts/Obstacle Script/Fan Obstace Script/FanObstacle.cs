using UnityEngine;

/// <summary>
/// Spinning, drifting fan obstacle. Spins around its local X axis and
/// optionally drifts forward (toward the level entrance). Larger fans
/// (bigger scale) spin and drift slower than smaller ones.
///
/// MULTIPLAYER: fans are never replicated. They are static hazards spawned locally on every machine
/// from the round seed (see FanSpawner), so the only thing that has to be true is that every machine
/// computes the SAME pose at the same moment. Two things used to break that, and both are fixed here:
///
///  1. The spin direction came from UnityEngine.Random, which is per-machine — so roughly half the
///     fans spun the opposite way for each player.
///  2. The pose was ACCUMULATED (`transform.Rotate(... * Time.deltaTime)` every frame from Start).
///     An accumulation can only stay in sync if two machines start at the same instant and never
///     differ by a single frame, which is false: clients finish loading at different times and run at
///     different frame rates. The error had no ceiling, so the fans drifted further apart the longer
///     a round ran.
///
/// The pose is now a pure FUNCTION of a shared clock (<see cref="SharedElapsed"/>) — absolute, never
/// integrated. That makes it self-correcting rather than merely synchronised: a machine that hitches,
/// drops frames or joins late lands on the correct pose on its very next frame, and costs no
/// bandwidth to do it.
/// </summary>
public class FanObstacle : MonoBehaviour
{
    [Header("Spin")]
    [Tooltip("Base spin speed in degrees per second for a 1-unit-scale fan. " +
             "Actual speed is divided by this fan's scale, so larger fans spin slower.")]
    public float baseSpinSpeed = 360f;
    [Tooltip("Give each fan a spin direction of its own. The direction is derived from the fan's " +
             "position, so it is the same on every machine in a multiplayer session.")]
    public bool randomizeSpinDirection = true;

    [Header("Drift")]
    [Tooltip("Base drift speed for a 1-unit fan. Divided by scale so larger fans drift slower.")]
    public float baseDriftSpeed = 0f;        // assigned by spawner
    [Tooltip("Direction of drift (set by spawner — points back toward entrance).")]
    public Vector3 driftDirection = Vector3.zero;
    [Tooltip("Distance traveled before the fan despawns or wraps. " +
             "Used to clean up off-track fans that have drifted away.")]
    public float maxDriftDistance = 2000f;

    private float spinDirection = 1f;
    private float scale;
    private Vector3 startLocalPosition;
    private Quaternion startLocalRotation;
    private Vector3 driftLocal;

    void Start()
    {
        scale = Mathf.Max(0.0001f, transform.localScale.x);  // assume uniform scale
        if (randomizeSpinDirection) spinDirection = DirectionFor(transform.position);

        // LOCAL pose is the baseline, not world: the track area is teleported wholesale between the
        // hub and the track offset, and a fan pinned to an absolute world point would be left behind.
        startLocalPosition = transform.localPosition;
        startLocalRotation = transform.localRotation;
        driftLocal = transform.parent != null
            ? transform.parent.InverseTransformDirection(driftDirection.normalized)
            : driftDirection.normalized;
    }

    void Update()
    {
        float t = SharedElapsed();

        // Spin around LOCAL X — the axis the blades rotate around. Speed is inversely proportional to
        // scale: bigger fan = slower spin. Written as an ABSOLUTE angle for elapsed time rather than a
        // per-frame Rotate, so the pose depends only on the clock and not on this machine's frame history.
        // Wrapped to one turn: a long session would otherwise push this into the tens of thousands of
        // degrees, where a float has too few bits left to place the blades smoothly.
        float angle = Mathf.Repeat((baseSpinSpeed / scale) * spinDirection * t, 360f);
        transform.localRotation = startLocalRotation * Quaternion.Euler(angle, 0f, 0f);

        // Drift toward the entrance. Speed also inversely proportional to scale — and likewise absolute.
        if (baseDriftSpeed > 0f && driftLocal.sqrMagnitude > 0.0001f)
        {
            float travelled = (baseDriftSpeed / scale) * t;
            transform.localPosition = startLocalPosition + driftLocal * travelled;

            // Clean up fans that have drifted too far so we don't accumulate
            // hundreds of off-screen fans behind the player. Distance comes from the same clock, so
            // every machine also retires the same fan at the same moment.
            if (travelled > maxDriftDistance)
                Destroy(gameObject);
        }
    }

    /// <summary>The clock every machine agrees on: seconds since this round went live, from the
    /// replicated round timer. Each machine ticks its own copy, but they were all started from the
    /// host's value (GO, or the mid-join sync), so they differ by latency once — not cumulatively.
    ///
    /// Zero while the track is FROZEN during preload, which parks every fan at its start pose and
    /// means the round begins from an identical state on all machines, host included.
    ///
    /// Single-player has nobody to agree with, so it just uses the local clock — that keeps the fans
    /// turning regardless of what the round timer is doing offline.</summary>
    static float SharedElapsed()
    {
        if (!MultiplayerWorld.IsMultiplayerGame) return Time.time;
        if (MultiplayerWorld.TrackFrozen) return 0f;

        var glm = GameLoopManager.Instance;
        if (glm == null) return Time.time;
        return Mathf.Max(0f, glm.roundDuration - glm.RoundTimeRemaining);
    }

    /// <summary>A deterministic +1 / -1 for this fan, hashed from where it stands.
    ///
    /// Every machine already agrees on that position — the spawner derives it from the round seed —
    /// so hashing it hands every machine the same answer with no plumbing, no extra bytes and nothing
    /// to keep in sync, and it works for hand-placed fans too. The position is quantised to 1/16 of a
    /// unit first, so the answer cannot hinge on the last bit of a float.</summary>
    static float DirectionFor(Vector3 p)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(p.x * 16f);
            h = h * 31 + Mathf.RoundToInt(p.y * 16f);
            h = h * 31 + Mathf.RoundToInt(p.z * 16f);
            h ^= h >> 15;
            return (h & 1) == 0 ? 1f : -1f;
        }
    }
}
