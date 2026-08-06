using UnityEngine;

/// <summary>
/// One place that decides which physics layer the game's CODE-BUILT UI lives on.
///
/// Every HUD, menu and overlay in this project is built in code with `new GameObject(...)`, which lands
/// on the **Default** layer — so anything doing a physics query against "everything except X" sees them.
/// That's how the grappling hook managed to latch onto the CreditsHUD canvas: excluding the UI layer
/// achieves nothing while the canvases aren't actually ON the UI layer.
///
/// Call <see cref="Apply"/> on a canvas root once it's built. It's recursive, so call it AFTER the
/// children exist (children created later keep the Default layer — <see cref="GrappleHook"/> also skips
/// anything parented to a Canvas, which is the belt-and-braces guarantee for UI added in future).
/// </summary>
public static class UiLayer
{
    /// <summary>Name of the layer UI is parked on. Must exist in Project Settings → Tags and Layers
    /// (Unity ships with a built-in "UI" layer at index 5).</summary>
    public const string LayerName = "UI";

    private static bool warned;

    /// <summary>Moves <paramref name="root"/> and every descendant onto the UI layer.</summary>
    public static void Apply(GameObject root)
    {
        if (root == null) return;

        int layer = LayerMask.NameToLayer(LayerName);
        if (layer < 0)
        {
            if (!warned)
            {
                warned = true;
                Debug.LogWarning($"[UiLayer] Layer '{LayerName}' not found in Tags and Layers — UI left " +
                                 "on its current layer, so physics queries may hit it.");
            }
            return;
        }

        SetRecursive(root, layer);
    }

    static void SetRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetRecursive(child.gameObject, layer);
    }
}
