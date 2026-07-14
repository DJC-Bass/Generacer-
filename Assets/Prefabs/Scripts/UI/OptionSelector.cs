using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// A gamepad/keyboard-friendly "&lt; value &gt;" option cycler for the code-built settings menus. Extends
/// <see cref="Selectable"/> so it gets focus highlighting + navigation exactly like a Button, but
/// overrides horizontal Move to CYCLE a fixed list of options (Left = previous, Right = next, wrapping)
/// instead of navigating sideways — the same trick <see cref="Slider"/> uses to consume Left/Right for
/// its value. Up/Down fall through to normal navigation. Fires <see cref="OnChanged"/> with the new index.
///
/// Built entirely from code (see MainMenuController.CreateOptionSelector): a background Image is the
/// target graphic and a child TMP shows the current option — no prefab required.
/// </summary>
public class OptionSelector : Selectable
{
    private TextMeshProUGUI valueLabel;
    private readonly List<string> options = new List<string>();
    private int index;

    /// <summary>Raised with the new index when the player cycles to a different option. NOT raised by
    /// <see cref="Configure"/> or <see cref="SetIndexSilent"/>.</summary>
    public event Action<int> OnChanged;

    public int Index => index;
    public int Count => options.Count;

    /// <summary>Sets the option list + value label and shows <paramref name="startIndex"/> without firing
    /// <see cref="OnChanged"/>.</summary>
    public void Configure(TextMeshProUGUI label, IList<string> opts, int startIndex)
    {
        valueLabel = label;
        options.Clear();
        if (opts != null) options.AddRange(opts);
        index = options.Count > 0 ? Mathf.Clamp(startIndex, 0, options.Count - 1) : 0;
        Refresh();
    }

    /// <summary>Jumps to an index and updates the label WITHOUT firing <see cref="OnChanged"/> — used to
    /// keep a selector in sync after another setting changed its value (e.g. quality resetting vsync).</summary>
    public void SetIndexSilent(int i)
    {
        if (options.Count == 0) return;
        index = Mathf.Clamp(i, 0, options.Count - 1);
        Refresh();
    }

    public override void OnMove(AxisEventData eventData)
    {
        switch (eventData.moveDir)
        {
            case MoveDirection.Left:  Step(-1); break;
            case MoveDirection.Right: Step(+1); break;
            default: base.OnMove(eventData); break;   // up/down/submit navigation as usual
        }
    }

    void Step(int dir)
    {
        if (options.Count < 2) return;
        int n = options.Count;
        index = ((index + dir) % n + n) % n;   // wrap both directions
        Refresh();
        OnChanged?.Invoke(index);
    }

    void Refresh()
    {
        if (valueLabel == null) return;
        valueLabel.text = options.Count > 0 ? "<  " + options[index] + "  >" : "--";
    }
}
