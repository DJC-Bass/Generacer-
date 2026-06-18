using UnityEngine;

/// <summary>
/// Ramps environmental-obstacle difficulty by game-loop round. On each TrackScene load it
/// reads <see cref="GameLoopManager.RoundNumber"/> and enables a random subset of the
/// assigned obstacle spawners:
///
///   • Round 1  -> 1 spawner active (random), the rest inactive.
///   • Round 2  -> 2 spawners active (random), the rest inactive.
///   • Round 3+ -> all spawners active.
///
/// The active set is re-rolled every round (the scene reloads fresh each track run). Disabled
/// spawners are switched off via SetActive, which is safe because each spawner is fully
/// self-contained (nothing else references them) and they delay their first spawn — combined
/// with the early execution order below, a disabled spawner never produces an obstacle.
/// </summary>
[DefaultExecutionOrder(-50)]   // run before the spawners' own Start so disabled ones never spawn
public class RoundObstacleSelector : MonoBehaviour
{
    [Tooltip("Environmental obstacle spawners gated by round (e.g. Boulder, Lightning, Fan). " +
             "Order doesn't matter — the active subset is chosen randomly each round.")]
    public GameObject[] spawners;

    void Start()
    {
        if (spawners == null || spawners.Length == 0) return;

        // No manager (e.g. playing the TrackScene on its own) -> leave everything active.
        int round = GameLoopManager.Instance != null
            ? GameLoopManager.Instance.RoundNumber
            : spawners.Length;

        // Round 1 -> 1 active, round 2 -> 2 active, round 3+ -> all active.
        int activeCount = Mathf.Clamp(round, 1, spawners.Length);

        // Fisher–Yates shuffle of indices so the active subset is random each round.
        int[] order = new int[spawners.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // Activate the first activeCount of the shuffled order; deactivate the rest.
        for (int rank = 0; rank < order.Length; rank++)
        {
            GameObject spawner = spawners[order[rank]];
            if (spawner != null) spawner.SetActive(rank < activeCount);
        }

        Debug.Log($"[RoundObstacleSelector] Round {round}: {activeCount}/{spawners.Length} obstacle spawners active.");
    }
}
