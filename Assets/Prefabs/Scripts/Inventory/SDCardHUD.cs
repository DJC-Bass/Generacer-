using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Top-left readout of the player's currently equipped SD card, shown just below the credits.
/// It lists only the SD items the player actually holds — any inventory item whose name ends
/// in " SD" (e.g. "Fire SD", "Wind SD") — defaulting to the one highest in the inventory. The
/// player cycles the equipped SD with D-pad left/right; the readout hides entirely when no SD
/// is held.
///
/// Persistent (lives on the DontDestroyOnLoad PlayerSystems object), so the readout and the
/// selection survive scene loads. The equipped SD itself is stored on PlayerInventory, so the
/// (future) activation ability — D-pad up — can read which SD is equipped.
/// </summary>
[DefaultExecutionOrder(1000)]
public class SDCardHUD : MonoBehaviour
{
    public static SDCardHUD Instance { get; private set; }

    private TextMeshProUGUI label;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        BuildUI();
    }

    void OnEnable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged += Refresh;
        Refresh();
    }

    void OnDisable()
    {
        if (PlayerInventory.Instance != null)
            PlayerInventory.Instance.OnChanged -= Refresh;
    }

    void Update()
    {
        if (MenuState.AnyOpen) return;   // don't switch SDs while a menu is open
        var gp = Gamepad.current;
        if (gp == null) return;

        // D-pad left/right switch the equipped SD. (Up is reserved for activation, later.)
        if (gp.dpad.left.wasPressedThisFrame) Cycle(-1);
        else if (gp.dpad.right.wasPressedThisFrame) Cycle(1);
    }

    /// <summary>Cycles the equipped SD through the held set, wrapping around.</summary>
    void Cycle(int dir)
    {
        var inv = PlayerInventory.Instance;
        if (inv == null) return;

        var held = HeldSDs();
        if (held.Count == 0) return;

        int idx = held.IndexOf(inv.EquippedSD);
        if (idx < 0) idx = 0;
        idx = (idx + dir + held.Count) % held.Count;

        inv.SetEquippedSD(held[idx]);
        Refresh();
    }

    void Refresh()
    {
        if (label == null) return;

        var inv = PlayerInventory.Instance;
        var held = (inv != null) ? HeldSDs() : new List<string>();

        // No SDs held — hide the readout and clear any stale equip.
        if (held.Count == 0)
        {
            if (inv != null && !string.IsNullOrEmpty(inv.EquippedSD)) inv.SetEquippedSD("");
            label.gameObject.SetActive(false);
            return;
        }

        // Default the equip to the top-of-inventory SD if nothing valid is equipped.
        string equipped = inv.EquippedSD;
        if (string.IsNullOrEmpty(equipped) || !held.Contains(equipped))
        {
            equipped = held[0];
            inv.SetEquippedSD(equipped);
        }

        label.gameObject.SetActive(true);
        label.text = "SD: " + DisplayName(equipped);
    }

    /// <summary>The SD items the player currently holds, in inventory (first-acquired) order.</summary>
    static List<string> HeldSDs()
    {
        var list = new List<string>();
        var inv = PlayerInventory.Instance;
        if (inv == null) return list;

        foreach (var name in inv.Order)
            if (IsSD(name) && inv.GetCount(name) > 0)
                list.Add(name);
        return list;
    }

    // An item is an SD if its name ends in " SD" (e.g. "Fire SD", "Lightning SD", "Wind SD").
    static bool IsSD(string name) => !string.IsNullOrEmpty(name) && name.EndsWith(" SD");

    // "Fire SD" -> "Fire" for the readout.
    static string DisplayName(string sd) =>
        sd.EndsWith(" SD") ? sd.Substring(0, sd.Length - 3) : sd;

    void BuildUI()
    {
        var canvasGO = new GameObject("SDHudCanvas");
        DontDestroyOnLoad(canvasGO);
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var go = new GameObject("SDText", typeof(RectTransform));
        go.transform.SetParent(canvasGO.transform, false);
        label = go.AddComponent<TextMeshProUGUI>();
        label.fontSize = 40;
        label.color = new Color(0.55f, 0.85f, 1f);   // light blue, distinct from the gold credits
        label.alignment = TextAlignmentOptions.TopLeft;

        // Top-left, just below the credits readout (credits sits at y -24, ~70 tall).
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(500f, 64f);
        rt.anchoredPosition = new Vector2(30f, -100f);

        UiLayer.Apply(canvasGO);   // keep code-built UI off the Default layer (see UiLayer)
    }
}
