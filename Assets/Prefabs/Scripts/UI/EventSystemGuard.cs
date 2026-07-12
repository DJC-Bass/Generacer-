using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Guarantees exactly ONE active EventSystem at all times.
///
/// The persistent menus (Start menu, inventory) need an EventSystem in every scene, so the game
/// creates one that survives scene loads. But the menu scenes (Main Menu, Car Selection) also ship
/// their own EventSystem, so once the persistent one exists, returning to a menu scene leaves TWO
/// active at once. Unity then logs "There are 2 event systems in the scene" every frame, and — worse
/// with the Input System — two active InputSystemUIInputModules fight over input, which can soft-lock
/// / hang UI processing.
///
/// This owns the single persistent EventSystem and, on every scene load, strips the EventSystem (and
/// its input module) off any OTHER object so exactly one remains. Persistent + bootstrapped on the
/// PlayerSystems object (added FIRST, so its EventSystem exists before any menu tries to make one and
/// their "create if none exists" guards all no-op).
/// </summary>
public class EventSystemGuard : MonoBehaviour
{
    public static EventSystemGuard Instance { get; private set; }

    private EventSystem mine;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        EnsureMine();
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // Awake runs before the first scene's objects exist; Start and each sceneLoaded catch the rest.
    void Start() => Enforce();
    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Enforce();

    /// <summary>Creates the one persistent EventSystem if we don't have it yet.</summary>
    void EnsureMine()
    {
        if (mine != null) return;

        var go = new GameObject("PersistentEventSystem");
        DontDestroyOnLoad(go);
        mine = go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        go.AddComponent<InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }

    /// <summary>Removes every EventSystem except ours, so exactly one stays active.</summary>
    void Enforce()
    {
        EnsureMine();

        foreach (var es in FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (es == mine) continue;

            // Strip the components (not the whole GameObject — it may hold unrelated scene stuff) so
            // the duplicate stops processing input and stops the per-frame warning.
            var module = es.GetComponent<BaseInputModule>();
            if (module != null) Destroy(module);
            Destroy(es);
        }

        EventSystem.current = mine;
    }
}
