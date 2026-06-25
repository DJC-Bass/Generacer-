using UnityEngine;

/// <summary>
/// Remembers the car chosen on the Car Selection screen across the scene load into the game loop.
/// A DontDestroyOnLoad holder created on demand when START is pressed; gameplay can later read
/// <see cref="SelectedCarPrefab"/> to spawn the chosen car. (Wiring the game loop to actually use
/// it is a follow-up — this just preserves the choice.)
/// </summary>
public class SelectedCarStore : MonoBehaviour
{
    public static SelectedCarStore Instance { get; private set; }

    public string SelectedCarName { get; private set; }
    public GameObject SelectedCarPrefab { get; private set; }

    /// <summary>Stores the chosen car, creating the persistent holder if needed.</summary>
    public static void Set(string carName, GameObject prefab)
    {
        if (Instance == null)
        {
            var go = new GameObject("SelectedCarStore");
            Instance = go.AddComponent<SelectedCarStore>();
            DontDestroyOnLoad(go);
        }
        Instance.SelectedCarName = carName;
        Instance.SelectedCarPrefab = prefab;
        Debug.Log($"[SelectedCarStore] Selected car: {carName}");
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }
}
