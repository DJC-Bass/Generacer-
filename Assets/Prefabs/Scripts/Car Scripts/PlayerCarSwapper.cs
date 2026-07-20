using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Makes the game loop use the car chosen on the Car Selection screen. Persistent + bootstrapped
/// on the PlayerSystems object, so it spans scenes; on every gameplay scene load it replaces the
/// scene's placed Player car with the selected prefab (see <see cref="SelectedCarStore"/>), keeping
/// the placed car's spawn transform and re-pointing the follow cameras at the new car. It also
/// hands the selected prefab to any <see cref="TrackGenerator"/> so a track that spawns its car
/// from a prefab uses the chosen one too.
///
/// No-op when no car was chosen (e.g. play-testing a gameplay scene directly), so the scene's own
/// placed car is the fallback.
/// </summary>
public class PlayerCarSwapper : MonoBehaviour
{
    [Tooltip("Tag identifying the player car to replace.")]
    public string playerTag = "Player";

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Additive loads never carry the player's car (only the multiplayer world additively loads
        // scenes, and its TrackScene's authored car is stripped by MultiplayerWorld) — swapping here
        // would grab the LIVE hub car via FindWithTag and destroy it.
        if (mode == LoadSceneMode.Additive) return;

        // Only gameplay scenes have a player car (reuse the HUD's gameplay-scene rule).
        if (!GameplayHud.VisibleInScene(scene.name)) return;

        var store = SelectedCarStore.Instance;
        if (store == null || store.SelectedCarPrefab == null) return;   // nothing chosen — keep the placed car

        GameObject prefab = store.SelectedCarPrefab;

        // A track that spawns its own car from a prefab should use the chosen one.
        var generator = FindFirstObjectByType<TrackGenerator>();
        if (generator != null) generator.carPrefab = prefab;

        SwapPlacedCar(prefab);
    }

    void SwapPlacedCar(GameObject prefab)
    {
        var placed = GameObject.FindWithTag(playerTag);
        if (placed == null) return;   // no placed car — a TrackGenerator (above) will spawn the prefab

        // Keep where the scene put the car, then remove the placed one so only the new car is "Player".
        Vector3 pos = placed.transform.position;
        Quaternion rot = placed.transform.rotation;
        Transform parent = placed.transform.parent;

        placed.SetActive(false);   // drop it from FindWithTag immediately; Destroy finishes the cleanup
        Destroy(placed);

        var car = Instantiate(prefab, pos, rot);
        if (parent != null) car.transform.SetParent(parent, true);
        car.tag = playerTag;
        PlayerRegistry.SetLocalCar(car);   // register before anything can race the tag lookup

        // Re-point every follow camera (main + rear view) at the new car.
        foreach (var cam in FindObjectsByType<CameraFollow>(FindObjectsSortMode.None))
            cam.target = car.transform;

        Debug.Log($"[PlayerCarSwapper] Spawned selected car '{prefab.name}'.");
    }
}
