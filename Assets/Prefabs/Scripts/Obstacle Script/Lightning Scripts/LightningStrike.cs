using System.Collections;
using UnityEngine;

public class LightningStrike : MonoBehaviour
{
    [Header("Timing")]
    public float warningDuration = 0.5f;
    public float boltDuration = 0.5f;

    [Header("Geometry")]
    public float strikeHeight = 9000f;
    public float warningRadius = 30f;
    public float boltThickness = 60f;
    public int boltSegments = 12;
    public float zigzagRadius = 200f;

    [Header("Materials")]
    public Material warningMaterial;
    public Material boltMaterial;

    private Vector3 strikePoint;

    public void Trigger(Vector3 point, Material warning, Material bolt)
    {
        strikePoint = point;
        warningMaterial = warning;
        boltMaterial = bolt;

        transform.position = strikePoint;

        // Spawn the warning immediately. It self-destructs after its fade.
        SpawnWarning();

        // Schedule the bolt independently — it fires after warningDuration
        // regardless of what happens to the warning object. Even if the
        // warning errors out or gets destroyed early, the bolt still spawns.
        Invoke(nameof(SpawnBolt), warningDuration);

        // The strike controller itself self-destructs after the full cycle
        // completes (warning + bolt durations + a small safety margin).
        Destroy(gameObject, warningDuration + boltDuration + 0.5f);
    }

    void SpawnWarning()
    {
        var obj = new GameObject("WarningColumn");
        // NOT parented to this — independent lifetime so this controller's
        // destruction doesn't take the warning down with it
        obj.transform.position = strikePoint;

        var mesh = LightningMeshBuilder.BuildWarningColumn(warningRadius, strikeHeight);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;

        var renderer = obj.AddComponent<MeshRenderer>();
        renderer.material = new Material(warningMaterial);

        // Self-fade and self-destruct
        var fader = obj.AddComponent<LightningFader>();
        fader.duration = warningDuration;
    }

    void SpawnBolt()
    {
        var obj = new GameObject("LightningBolt");
        obj.transform.position = strikePoint;

        float facingAngle = Random.Range(0f, 360f);
        Vector3 facingDir = new Vector3(Mathf.Cos(facingAngle * Mathf.Deg2Rad),
                                         0f,
                                         Mathf.Sin(facingAngle * Mathf.Deg2Rad));

        var mesh = LightningMeshBuilder.BuildLightningBolt(
            boltThickness, strikeHeight, boltSegments, zigzagRadius, facingDir);

        obj.AddComponent<MeshFilter>().sharedMesh = mesh;

        var renderer = obj.AddComponent<MeshRenderer>();
        renderer.material = new Material(boltMaterial);

        // Trigger collider — physically passes through everything, but reports
        // collisions via OnTriggerEnter on the LightningHitDetector script.
        var collider = obj.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        collider.convex = true;
        collider.isTrigger = true;

        // Trigger detection requires a Rigidbody on at least one of the colliders
        // involved. The bolt's body is kinematic (doesn't fall, doesn't get pushed)
        // so it stays in place but still triggers OnTriggerEnter.
        var rb = obj.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Hit detector — only responds to objects tagged "Player"
        obj.AddComponent<LightningHitDetector>();

        var fader = obj.AddComponent<LightningFader>();
        fader.duration = boltDuration;
    }
}

/// <summary>
/// Detects when the player car drives through a lightning bolt and fires
/// a hit response. Filters out everything except objects tagged "Player",
/// so obstacles, track meshes, and other lightning bolts pass through
/// without triggering anything.
/// </summary>
public class LightningHitDetector : MonoBehaviour
{
    [Tooltip("How long the player must wait before this bolt can hit them again. " +
             "Prevents repeated hits as the car drives through a bolt's mesh.")]
    public float hitCooldown = 0.1f;

    private bool hasHit;

    void OnTriggerEnter(Collider other)
    {
        if (hasHit) return;

        // Walk up the hierarchy to find the actual root that holds the Player tag.
        // Cars often have wheels or sub-colliders that wouldn't have the tag directly.
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag("Player"))
            {
                OnPlayerHit(t.gameObject);
                hasHit = true;
                return;
            }
            t = t.parent;
        }
    }

    /// <summary>
    /// Hook for whatever effect you want when the player gets struck.
    /// Currently logs a message and applies an upward impulse for a satisfying
    /// "zap" reaction. Replace or extend with damage, slow-down, screen shake, etc.
    /// </summary>
    void OnPlayerHit(GameObject player)
    {
        Debug.Log("[LightningHitDetector] Player struck by lightning!");

        var rb = player.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // Visible reaction — kicks the car upward and slightly forward
            // so a hit feels like an actual lightning strike, not just a sound effect
            rb.AddForce(Vector3.up * 80f, ForceMode.VelocityChange);
        }
    }
}

/// <summary>
/// Self-contained fade-and-destroy helper. Each lightning piece manages its
/// own lifetime so the orchestrator can fire-and-forget.
/// </summary>
public class LightningFader : MonoBehaviour
{
    public float duration = 0.5f;

    void Start()
    {
        StartCoroutine(FadeRoutine());
    }

    IEnumerator FadeRoutine()
    {
        var renderer = GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            Destroy(gameObject, duration);
            yield break;
        }

        Material mat = renderer.material;
        Color startColor = mat.color;

        Color startEmission = Color.black;
        bool hasEmission = mat.HasProperty("_EmissionColor");
        if (hasEmission) startEmission = mat.GetColor("_EmissionColor");

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            Color c = startColor;
            c.a = 1f - t;
            mat.color = c;

            if (hasEmission)
                mat.SetColor("_EmissionColor", startEmission * (1f - t));

            yield return null;
        }

        Destroy(gameObject);
    }
}