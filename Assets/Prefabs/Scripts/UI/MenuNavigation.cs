using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Shared navigation helpers for the code-built menus (main menu, car select, in-game start menu).
/// </summary>
public static class MenuNavigation
{
    /// <summary>
    /// Wires a vertical button list for WRAP-AROUND gamepad/keyboard navigation: pressing Up on the
    /// top entry jumps to the bottom, and Down on the bottom jumps to the top. Left/right links are
    /// cleared so navigation can't slip sideways out of the list. Null/empty input is ignored.
    /// </summary>
    public static void WireVerticalWrap(IList<Button> buttons)
    {
        if (buttons == null) return;
        int n = buttons.Count;
        if (n == 0) return;

        for (int i = 0; i < n; i++)
        {
            var b = buttons[i];
            if (b == null) continue;

            var nav = b.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = buttons[(i - 1 + n) % n];     // top wraps to bottom
            nav.selectOnDown = buttons[(i + 1) % n];       // bottom wraps to top
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            b.navigation = nav;
        }
    }

    /// <summary>
    /// Rescues gamepad/keyboard navigation from a "nothing selected" soft-lock (e.g. after a mouse
    /// click on empty space clears the selection): if the EventSystem has no live selected object and
    /// the player presses Up/Down (D-pad or arrow keys), it highlights <paramref name="defaultSelection"/>
    /// (the top menu item). Call once per frame from a menu's LateUpdate so it runs AFTER the UI input
    /// module has processed input — that way the first press just highlights the top item instead of
    /// also stepping past it. Pass null to do nothing (e.g. a screen with no list to focus).
    /// </summary>
    public static void EnsureSelectionOnNavigate(GameObject defaultSelection)
    {
        if (defaultSelection == null) return;
        var es = EventSystem.current;
        if (es == null) return;

        var cur = es.currentSelectedGameObject;
        if (cur != null && cur.activeInHierarchy) return;   // already have a live selection

        if (NavUpOrDownPressed())
            es.SetSelectedGameObject(defaultSelection);
    }

    /// <summary>
    /// Plays the shared "move" menu SFX (<see cref="AudioManager.PlayMenuMove"/>) whenever the
    /// EventSystem's highlighted item changes to a different live item. Call once per frame from a
    /// menu's LateUpdate, passing a field that remembers the last selection. The opening highlight
    /// (nothing → first item) and losing the selection are silent; only genuine navigation between
    /// two items plays. No-op without an EventSystem.
    /// </summary>
    public static void PlayMoveSfxOnSelectionChange(ref GameObject lastSelected)
    {
        var es = EventSystem.current;
        var cur = es != null ? es.currentSelectedGameObject : null;
        if (cur == lastSelected) return;

        if (cur != null && lastSelected != null)   // moving between two real items, not open/close
            AudioManager.PlayMenuMove();
        lastSelected = cur;
    }

    static bool NavUpOrDownPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var gp = Gamepad.current;
        if (gp != null && (gp.dpad.up.wasPressedThisFrame || gp.dpad.down.wasPressedThisFrame))
            return true;
        var kb = Keyboard.current;
        if (kb != null && (kb.upArrowKey.wasPressedThisFrame || kb.downArrowKey.wasPressedThisFrame))
            return true;
        return false;
#else
        return Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow);
#endif
    }
}
