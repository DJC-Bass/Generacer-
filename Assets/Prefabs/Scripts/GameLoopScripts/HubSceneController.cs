using UnityEngine;

/// <summary>
/// Manages hub-side reactions to GameLoopManager events. Spawns and
/// despawns the boost gate and portal as round phases change.
/// </summary>
public class HubSceneController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("BoostGate prefab to spawn when round becomes active.")]
    public GameObject boostGatePrefab;
    [Tooltip("Portal prefab that loads the track scene.")]
    public GameObject hubPortalPrefab;

    [Header("Spawn Positions")]
    public Transform boostGateSpawnPoint;
    public Transform portalSpawnPoint;

    private GameObject spawnedBoostGate;
    private GameObject spawnedPortal;

    void Start()
    {
        if (GameLoopManager.Instance == null)
        {
            Debug.LogError("[HubSceneController] No GameLoopManager in scene. " +
                           "Add it to a bootstrap scene or to this scene.");
            return;
        }

        GameLoopManager.Instance.OnPortalShouldSpawn += SpawnPortalAndGate;
        GameLoopManager.Instance.OnPortalShouldDespawn += DespawnPortalAndGate;

        // If the player just returned to hub mid-round (e.g. portal already
        // active when this scene loaded), spawn immediately
        if (GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.HubPortalActive)
            SpawnPortalAndGate();
    }

    void OnDestroy()
    {
        if (GameLoopManager.Instance != null)
        {
            GameLoopManager.Instance.OnPortalShouldSpawn -= SpawnPortalAndGate;
            GameLoopManager.Instance.OnPortalShouldDespawn -= DespawnPortalAndGate;
        }
    }

    void SpawnPortalAndGate()
    {
        if (spawnedBoostGate == null && boostGatePrefab != null && boostGateSpawnPoint != null)
            spawnedBoostGate = Instantiate(boostGatePrefab, boostGateSpawnPoint.position,
                                            boostGateSpawnPoint.rotation);

        if (spawnedPortal == null && hubPortalPrefab != null && portalSpawnPoint != null)
            spawnedPortal = Instantiate(hubPortalPrefab, portalSpawnPoint.position,
                                         portalSpawnPoint.rotation);
    }

    void DespawnPortalAndGate()
    {
        if (spawnedBoostGate != null) Destroy(spawnedBoostGate);
        if (spawnedPortal != null) Destroy(spawnedPortal);
    }
}