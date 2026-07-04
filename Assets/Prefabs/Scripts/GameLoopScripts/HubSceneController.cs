using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Manages hub-side reactions to GameLoopManager events. Spawns and
/// despawns the boost gate and portal as round phases change. Also runs the two game-over endings:
/// the Drone ending (portal spawns and a relentless drone swarm chases the player) and the player
/// Victory ending (no portal; a "BOTS DEFEATED" banner flashes centre-screen, then fades).
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

    [Header("Drone Ending (game over)")]
    [Tooltip("DroneCar prefab spawned during the game-over Drone ending swarm.")]
    public GameObject droneCarPrefab;
    [Tooltip("Drones spawned per second during the Drone ending.")]
    public float droneEndingSpawnRate = 5f;
    [Tooltip("Total drones spawned over the Drone ending (the swarm cap).")]
    public int droneEndingMaxDrones = 30;
    [Tooltip("Random horizontal scatter around the portal so spawned drones don't stack exactly (units).")]
    public float droneEndingSpawnScatter = 12f;
    [Tooltip("Layer assigned to the spawned ending drones (matches the track drone spawner).")]
    public string droneLayerName = "Drone";

    [Header("Player Victory (game over — you win)")]
    [Tooltip("Banner text shown centre-screen when the player wins by collecting enough SDs.")]
    public string victoryBannerText = "BOTS DEFEATED";
    [Tooltip("Seconds the banner stays fully visible before it begins to fade.")]
    public float victoryBannerHoldSeconds = 2f;
    [Tooltip("Seconds the banner takes to fade away after the hold.")]
    public float victoryBannerFadeSeconds = 1f;
    [Tooltip("Banner font size, at the 1920x1080 reference resolution.")]
    public float victoryBannerFontSize = 160f;
    [Tooltip("Banner text colour.")]
    public Color victoryBannerColor = new Color(0.4f, 1f, 0.5f);

    private GameObject spawnedBoostGate;
    private GameObject spawnedPortal;
    private bool droneEndingStarted;
    private bool playerVictoryStarted;
    private Vector3 droneSpawnOrigin;

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
        GameLoopManager.Instance.OnDroneEnding += BeginDroneEnding;
        GameLoopManager.Instance.OnPlayerWin += BeginPlayerVictory;

        // The player wins by collecting enough SDs — scored as they return from the track, so the
        // flag is already set by the time this hub loads. The portal stays down; show the banner.
        if (GameLoopManager.Instance.PlayerWinActive)
        {
            BeginPlayerVictory();
        }
        // The game-over Drone ending may have triggered during the trip back to the hub (a track
        // failure, or a drone beating the player); start it the instant we load in.
        else if (GameLoopManager.Instance.DroneEndingActive)
        {
            BeginDroneEnding();
        }
        // Otherwise, if the player returned mid-round (portal already active), spawn it now.
        else if (GameLoopManager.Instance.CurrentPhase == GameLoopManager.Phase.HubPortalActive)
        {
            SpawnPortalAndGate();
        }
    }

    void OnDestroy()
    {
        if (GameLoopManager.Instance != null)
        {
            GameLoopManager.Instance.OnPortalShouldSpawn -= SpawnPortalAndGate;
            GameLoopManager.Instance.OnPortalShouldDespawn -= DespawnPortalAndGate;
            GameLoopManager.Instance.OnDroneEnding -= BeginDroneEnding;
            GameLoopManager.Instance.OnPlayerWin -= BeginPlayerVictory;
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
        // Null the refs after destroying so a same-frame re-spawn (e.g. the Drone ending firing
        // right after a hub timeout despawns the portal) isn't blocked by the stale reference.
        if (spawnedBoostGate != null) { Destroy(spawnedBoostGate); spawnedBoostGate = null; }
        if (spawnedPortal != null) { Destroy(spawnedPortal); spawnedPortal = null; }
    }

    // -------------------------------------------------------
    //  Game-over Drone ending — portal spawns immediately and a relentless drone swarm hunts
    //  the player (~droneEndingSpawnRate / sec up to droneEndingMaxDrones).
    // -------------------------------------------------------

    void BeginDroneEnding()
    {
        if (droneEndingStarted) return;
        droneEndingStarted = true;

        Debug.Log("[HubSceneController] Drone ending — portal spawns and the swarm closes in.");
        AudioManager.RefreshSceneMusic();            // swap the hub theme for the Drone-ending track
        SpawnPortalAndGate();                        // portal spawns immediately
        droneSpawnOrigin = ComputePortalCenter();    // swarm spawns at the portal's visible centre
        StartCoroutine(SpawnDroneSwarm());
    }

    /// <summary>World-space centre of the spawned hub portal, taken from its renderer bounds so a
    /// prefab whose pivot sits off to one side still spawns the swarm at the VISIBLE portal disc
    /// rather than behind it. Falls back to the portal's transform / spawn point.</summary>
    Vector3 ComputePortalCenter()
    {
        if (spawnedPortal != null)
        {
            var rends = spawnedPortal.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                Bounds b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                return b.center;
            }
            return spawnedPortal.transform.position;
        }
        return portalSpawnPoint != null ? portalSpawnPoint.position : Vector3.zero;
    }

    IEnumerator SpawnDroneSwarm()
    {
        if (droneCarPrefab == null)
        {
            Debug.LogWarning("[HubSceneController] Drone ending: no Drone Car Prefab assigned — no swarm.");
            yield break;
        }

        var wait = new WaitForSeconds(1f / Mathf.Max(0.1f, droneEndingSpawnRate));
        for (int i = 0; i < droneEndingMaxDrones; i++)
        {
            SpawnChasingDrone();
            yield return wait;
        }
    }

    void SpawnChasingDrone()
    {
        // Spawn at the portal's centre so the drones look like they're pouring out of it.
        Vector2 scatter = Random.insideUnitCircle * droneEndingSpawnScatter;
        Vector3 pos = droneSpawnOrigin + new Vector3(scatter.x, 0f, scatter.y);

        GameObject drone = Instantiate(droneCarPrefab, pos, Quaternion.identity);

        int layer = LayerMask.NameToLayer(droneLayerName);
        if (layer >= 0) SetLayerRecursively(drone, layer);

        var dc = drone.GetComponent<DroneCar>();
        if (dc != null) dc.BeginChase();
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // -------------------------------------------------------
    //  Player victory ending — the player collected enough SDs to win. The portal never spawns;
    //  we flash a centre-screen "BOTS DEFEATED" banner that holds, then fades away.
    // -------------------------------------------------------

    void BeginPlayerVictory()
    {
        if (playerVictoryStarted) return;
        playerVictoryStarted = true;

        Debug.Log("[HubSceneController] Player victory — BOTS DEFEATED. Portal stays down.");
        AudioManager.RefreshSceneMusic();            // swap the hub theme for the player-victory track
        StartCoroutine(ShowVictoryBanner());
    }

    IEnumerator ShowVictoryBanner()
    {
        // Dedicated overlay canvas, ordered above every gameplay HUD and the start menu.
        var canvasGO = new GameObject("PlayerVictoryCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 400;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // CanvasGroup drives the fade in one place (text + any future children).
        var group = canvasGO.AddComponent<CanvasGroup>();

        var textGO = new GameObject("VictoryText", typeof(RectTransform));
        textGO.transform.SetParent(canvasGO.transform, false);
        var label = textGO.AddComponent<TextMeshProUGUI>();
        label.text = victoryBannerText;
        label.fontSize = victoryBannerFontSize;
        label.color = victoryBannerColor;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;

        // Centred on screen.
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(1600f, 400f);
        rt.anchoredPosition = Vector2.zero;

        // Hold fully visible, then fade to nothing.
        yield return new WaitForSeconds(victoryBannerHoldSeconds);

        float fade = Mathf.Max(0.01f, victoryBannerFadeSeconds);
        float t = 0f;
        while (t < fade)
        {
            t += Time.deltaTime;
            group.alpha = Mathf.Clamp01(1f - t / fade);
            yield return null;
        }

        Destroy(canvasGO);
    }
}