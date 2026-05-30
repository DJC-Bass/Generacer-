using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PortalTrigger : MonoBehaviour
{
    [Header("Scene Loading")]
    [Tooltip("The Build Index of your track scene (check File > Build Settings)")]
    public int trackSceneIndex = 1;

    [Header("Visual Feedback")]
    public GameObject portalVisual;           // Drag your PortalMesh here
    public ParticleSystem portalParticles;    // Optional — drag particle system here
    public float rotationSpeed = 90f;         // Portal spin speed (degrees per second)

    [Header("Activation")]
    public float activationDelay = 0.5f;      // Seconds before scene loads (feels better)
    public bool portalActive = true;          // Can disable portal via other scripts

    private bool isLoading = false;           // Prevents double-triggering
    private Renderer portalRenderer;
    private MaterialPropertyBlock propBlock;

    void Start()
    {
        // Grab the renderer on the visual for colour pulsing
        if (portalVisual != null)
        {
            portalRenderer = portalVisual.GetComponent<Renderer>();
            propBlock = new MaterialPropertyBlock();
        }

        if (portalParticles != null)
            portalParticles.Play();
    }

    void Update()
    {
        SpinPortal();
        PulsePortalColour();
    }

    // -------------------------------------------------------
    //  Trigger Detection
    // -------------------------------------------------------

    void OnTriggerEnter(Collider other)
    {
        // Inside OnTriggerEnter, after confirming the player entered the portal,
        // before SceneManager.LoadScene
        if (GameLoopManager.Instance != null)
            GameLoopManager.Instance.NotifyEnteredTrack();

        // Only react to the player car, and only once
        if (!portalActive || isLoading) return;

        if (other.CompareTag("Player"))
        {
            isLoading = true;
            StartCoroutine(LoadTrackScene());
        }
    }

    IEnumerator LoadTrackScene()
    {
        // Brief pause so the player feels the portal "activate"
        yield return new WaitForSeconds(activationDelay);

        // Load asynchronously so Unity doesn't freeze on larger scenes
        AsyncOperation load = SceneManager.LoadSceneAsync(trackSceneIndex);

        // Optionally show a loading bar by reading load.progress (0–0.9)
        // 0.9 means the scene is ready but waiting — let it finish automatically
        load.allowSceneActivation = true;
    }

    // -------------------------------------------------------
    //  Visuals
    // -------------------------------------------------------

    void SpinPortal()
    {
        if (portalVisual == null) return;
        portalVisual.transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
    }

    void PulsePortalColour()
    {
        if (portalRenderer == null) return;

        // Oscillate emission intensity using a sine wave
        float pulse = (Mathf.Sin(Time.time * 2f) + 1f) / 2f;  // 0 to 1

        portalRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor("_EmissionColor", Color.cyan * pulse * 3f);
        portalRenderer.SetPropertyBlock(propBlock);
    }
}