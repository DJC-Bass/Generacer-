using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

/// <summary>
/// Shared builders for the settings widgets used by the code-built menus, so AUDIO (volume sliders),
/// VIDEO (option cyclers) and CONTROLS look and behave the same wherever they appear (Main Menu, in-game
/// Start Menu). Each builder makes ONE self-contained widget and returns it; the caller sizes/positions
/// it or drops it into its own layout, and wires navigation. Pass a <see cref="Theme"/> for the host
/// menu's colours so each menu keeps its own palette.
/// </summary>
public static class SettingsUI
{
    /// <summary>The host menu's button/selectable colours, so a shared widget matches that menu.</summary>
    public struct Theme
    {
        public Color normal, highlighted, selected, pressed, text;
        public float fade;
    }

    // VIDEO display-mode options, index-aligned with these FullScreenMode values.
    public static readonly string[] FullscreenLabels = { "Fullscreen", "Borderless", "Windowed" };
    public static readonly FullScreenMode[] FullscreenModes =
        { FullScreenMode.ExclusiveFullScreen, FullScreenMode.FullScreenWindow, FullScreenMode.Windowed };

    public static int FullscreenIndexOf(FullScreenMode mode)
    {
        for (int i = 0; i < FullscreenModes.Length; i++)
            if (FullscreenModes[i] == mode) return i;
        return 1;   // MaximizedWindow (mac) / anything unlisted -> Borderless
    }

    // Prettier CONTROLS row names (the action's own name is the key).
    public static string FriendlyActionName(string actionName)
    {
        switch (actionName)
        {
            case "SD":        return "SD Card";
            case "RearView":  return "Rear View";
            default:          return actionName;
        }
    }

    public static string PartLabel(string partName)
    {
        if (partName == "positive") return "+";
        if (partName == "negative") return "-";
        return partName;
    }

    public static TextMeshProUGUI NewText(Transform parent, string name, float fontSize, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.fontSize = fontSize;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.fontStyle = FontStyles.UpperCase;   // settings widgets render in caps (matches the menus' all-caps text)
        return t;
    }

    /// <summary>Explicit Up/Down wrap navigation for a column of selectables, with Left/Right cleared so
    /// each Slider/OptionSelector keeps them for changing its own value.</summary>
    public static void WireVerticalWrap(IList<Selectable> items)
    {
        if (items == null) return;
        int n = items.Count;
        for (int i = 0; i < n; i++)
        {
            var s = items[i];
            if (s == null) continue;
            var nav = s.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp   = items[(i - 1 + n) % n];
            nav.selectOnDown = items[(i + 1) % n];
            nav.selectOnLeft = null; nav.selectOnRight = null;
            s.navigation = nav;
        }
    }

    /// <summary>Builds a horizontal 0..1 <see cref="Slider"/> widget (Background / Fill / Handle). The
    /// caller positions it. Value is set BEFORE the listener so construction doesn't fire onChanged.</summary>
    public static Slider VolumeSlider(Transform parent, Theme t, float initial, UnityAction<float> onChanged)
    {
        var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        var slider = go.GetComponent<Slider>();

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(go.transform, false);
        bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0f, 0.25f); bgrt.anchorMax = new Vector2(1f, 0.75f);
        bgrt.offsetMin = Vector2.zero; bgrt.offsetMax = Vector2.zero;

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(go.transform, false);
        var fart = fillArea.GetComponent<RectTransform>();
        fart.anchorMin = new Vector2(0f, 0.25f); fart.anchorMax = new Vector2(1f, 0.75f);
        fart.offsetMin = new Vector2(8f, 0f); fart.offsetMax = new Vector2(-18f, 0f);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        fill.GetComponent<Image>().color = t.highlighted;   // accent
        var fillrt = fill.GetComponent<RectTransform>();
        fillrt.anchorMin = new Vector2(0f, 0f); fillrt.anchorMax = new Vector2(1f, 1f);
        fillrt.offsetMin = Vector2.zero; fillrt.offsetMax = Vector2.zero;

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(go.transform, false);
        var hart = handleArea.GetComponent<RectTransform>();
        hart.anchorMin = new Vector2(0f, 0f); hart.anchorMax = new Vector2(1f, 1f);
        hart.offsetMin = new Vector2(10f, 0f); hart.offsetMax = new Vector2(-10f, 0f);

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        handle.GetComponent<Image>().color = Color.white;
        var handlert = handle.GetComponent<RectTransform>();
        handlert.sizeDelta = new Vector2(22f, 0f);

        slider.fillRect = fillrt;
        slider.handleRect = handlert;
        slider.targetGraphic = handle.GetComponent<Image>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f; slider.maxValue = 1f; slider.wholeNumbers = false;
        slider.value = initial;                          // set BEFORE the listener
        slider.onValueChanged.AddListener(onChanged);

        var cb = slider.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = t.highlighted;
        cb.selectedColor = t.selected;
        cb.pressedColor = t.pressed;
        cb.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        cb.colorMultiplier = 1f; cb.fadeDuration = t.fade;
        slider.colors = cb;

        return slider;
    }

    /// <summary>Builds an <see cref="OptionSelector"/> "&lt; value &gt;" cycler widget (background box +
    /// value label). The caller positions it and wires navigation. Cycling plays the shared move SFX.</summary>
    public static OptionSelector OptionCycler(Transform parent, Theme t, float fontSize,
                                              IList<string> options, int startIndex, Action<int> onChanged)
    {
        var go = new GameObject("OptionSelector", typeof(RectTransform), typeof(Image), typeof(OptionSelector));
        go.transform.SetParent(parent, false);

        var img = go.GetComponent<Image>();
        img.color = Color.white;   // ColorBlock states provide the real colours

        var sel = go.GetComponent<OptionSelector>();
        sel.transition = Selectable.Transition.ColorTint;
        sel.targetGraphic = img;
        var cb = sel.colors;
        cb.normalColor = t.normal;
        cb.highlightedColor = t.highlighted;
        cb.selectedColor = t.selected;
        cb.pressedColor = t.pressed;
        cb.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        cb.colorMultiplier = 1f; cb.fadeDuration = t.fade;
        sel.colors = cb;

        var valueLabel = NewText(go.transform, "Value", fontSize, TextAlignmentOptions.Center);
        valueLabel.color = t.text;
        var vrt = valueLabel.rectTransform;
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;

        sel.Configure(valueLabel, options, startIndex);
        sel.OnChanged += _ => AudioManager.PlayMenuMove();   // tick on each cycle
        sel.OnChanged += onChanged;
        return sel;
    }

    /// <summary>De-duplicated (by width×height) resolution labels, plus the width/height list and the
    /// index of the current/saved resolution.</summary>
    public static List<string> ResolutionOptions(out List<Vector2Int> options, out int currentIndex)
    {
        options = new List<Vector2Int>();
        var labels = new List<string>();
        var seen = new HashSet<long>();

        foreach (var r in Screen.resolutions)
        {
            long key = ((long)r.width << 32) | (uint)r.height;
            if (!seen.Add(key)) continue;
            options.Add(new Vector2Int(r.width, r.height));
            labels.Add(r.width + " x " + r.height);
        }
        if (options.Count == 0)   // editor / headless fallback: at least the current resolution
        {
            options.Add(new Vector2Int(Screen.width, Screen.height));
            labels.Add(Screen.width + " x " + Screen.height);
        }

        int targetW = GameSettings.HasResolution ? GameSettings.ResolutionWidth  : Screen.width;
        int targetH = GameSettings.HasResolution ? GameSettings.ResolutionHeight : Screen.height;
        currentIndex = 0;
        for (int i = 0; i < options.Count; i++)
            if (options[i].x == targetW && options[i].y == targetH) { currentIndex = i; break; }

        return labels;
    }
}
