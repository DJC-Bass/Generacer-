using System.Collections;
using UnityEngine;

/// <summary>
/// Fades in all renderers on this GameObject and its children. Used when a
/// drone spawns to give it a visual appearance rather than popping in.
/// </summary>
public class DroneFadeIn : MonoBehaviour
{
    public float duration = 1f;

    Renderer[] renderers;
    Material[] originalMaterials;

    void Start()
    {
        renderers = GetComponentsInChildren<Renderer>();

        // Create instanced materials so fading this drone doesn't affect others
        originalMaterials = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            originalMaterials[i] = renderers[i].material;  // .material auto-instances
        }

        StartCoroutine(FadeRoutine());
    }

    IEnumerator FadeRoutine()
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            foreach (var mat in originalMaterials)
            {
                if (mat == null) continue;
                Color c = mat.color;
                c.a = t;
                mat.color = c;
            }

            yield return null;
        }

        Destroy(this);  // fade-in complete, self-destruct script
    }
}