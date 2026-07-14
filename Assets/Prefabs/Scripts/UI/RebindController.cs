using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Shared interactive-rebinding flow for the CONTROLS screens (Main Menu + in-game Start Menu). The host
/// owns a <see cref="GeneracerControls"/> instance and passes it via <see cref="Init"/>; this component
/// runs the rebind operations against it and persists the diff through <see cref="InputRebinding"/>.
///
/// While a rebind is listening, menu navigation is turned off (so the captured press doesn't also drive
/// the UI) and re-enabled after a short cooldown (so releasing the bound button doesn't leak a stray
/// Submit). The host checks <see cref="IsRebinding"/> to suppress its OWN input (e.g. the Start Menu's
/// Start-to-close) during that window. Cancel with gamepad Start (excluded from binding, polled here) or
/// Esc (the operation's cancel binding).
/// </summary>
public class RebindController : MonoBehaviour
{
    private GeneracerControls controls;
    private InputActionRebindingExtensions.RebindingOperation op;

    // Rows registered for a "reset to defaults" label refresh.
    private readonly List<(InputAction action, int bindingIndex, TextMeshProUGUI label)> rows
        = new List<(InputAction, int, TextMeshProUGUI)>();

    /// <summary>True while an interactive rebind is listening (including the short post-rebind cooldown).</summary>
    public bool IsRebinding { get; private set; }

    /// <summary>The controls instance whose bindings this rebinds + saves. Call once before building rows.</summary>
    public void Init(GeneracerControls c) { controls = c; }

    public void ClearRows() => rows.Clear();
    public void RegisterRow(InputAction action, int bindingIndex, TextMeshProUGUI label)
        => rows.Add((action, bindingIndex, label));

    public string DisplayString(InputAction action, int bindingIndex)
        => action != null ? action.GetBindingDisplayString(bindingIndex) : "";

    void Update()
    {
        if (!IsRebinding) return;
        var gp = Gamepad.current;
        if (gp != null && gp.startButton.wasPressedThisFrame) op?.Cancel();
    }

    /// <summary>Starts listening for a control to bind to (action, bindingIndex). Shows "[ press ]" on the
    /// row's value label until a control is picked or the rebind is cancelled.</summary>
    public void Begin(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel)
    {
        if (IsRebinding || action == null) return;
        IsRebinding = true;

        // Stop the UI from reacting to the very press we're about to capture.
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = false;
        if (valueLabel != null) valueLabel.text = "[ press ]";

        string prevOverride = action.bindings[bindingIndex].overridePath;   // captured so a conflict can revert
        action.Disable();   // interactive rebinding requires the action be disabled
        op = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>")
            .WithControlsExcluding("<Gamepad>/start")   // reserved as the gamepad cancel (see Update)
            .WithCancelingThrough("<Keyboard>/escape")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(o => Finish(action, bindingIndex, valueLabel, prevOverride, true))
            .OnCancel(o => Finish(action, bindingIndex, valueLabel, prevOverride, false))
            .Start();
    }

    void Finish(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel, string prevOverride, bool completed)
    {
        if (op != null) { op.Dispose(); op = null; }

        // Reject a rebind onto a control already used elsewhere in the map: revert it, flag it, buzz.
        if (completed && InputRebinding.IsBindingInConflict(action, bindingIndex, out string conflictName))
        {
            InputRebinding.RevertBinding(action, bindingIndex, prevOverride);
            if (controls != null) InputRebinding.Save(controls.asset);
            AudioManager.PlayStoreDenied();
            Debug.Log($"[Rebind] Control already used by '{conflictName}' — rebind rejected.");
            if (isActiveAndEnabled) StartCoroutine(ShowConflictThenRestore(action, bindingIndex, valueLabel));
            else { if (valueLabel != null) valueLabel.text = DisplayString(action, bindingIndex); EndRebindNow(); }
            return;
        }

        if (controls != null) InputRebinding.Save(controls.asset);
        if (valueLabel != null) valueLabel.text = DisplayString(action, bindingIndex);

        // A coroutine can't run on an inactive object (e.g. the menu closed mid-rebind) — clean up now.
        if (isActiveAndEnabled) StartCoroutine(Cooldown());
        else EndRebindNow();
    }

    // Briefly shows "in use" on the row after a rejected (conflicting) rebind, then restores the label to
    // the binding it reverted to.
    IEnumerator ShowConflictThenRestore(InputAction action, int bindingIndex, TextMeshProUGUI valueLabel)
    {
        if (valueLabel != null) valueLabel.text = "in use";
        yield return new WaitForSecondsRealtime(1.1f);
        if (valueLabel != null) valueLabel.text = DisplayString(action, bindingIndex);
        EndRebindNow();
    }

    // Re-enable menu navigation a beat after the rebind, so releasing the captured button doesn't leak
    // through as a stray Submit on the row we just rebound.
    IEnumerator Cooldown()
    {
        yield return new WaitForSecondsRealtime(0.2f);
        EndRebindNow();
    }

    void EndRebindNow()
    {
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = true;
        IsRebinding = false;
    }

    /// <summary>Clears all binding overrides (back to the authored defaults) and refreshes every
    /// registered row's label. No-op while a rebind is in progress.</summary>
    public void ResetAll()
    {
        if (IsRebinding || controls == null) return;
        InputRebinding.ResetToDefaults(controls.asset);
        RefreshLabels();
    }

    /// <summary>Re-reads every registered row's current binding into its label — after a reset, or when a
    /// panel re-opens and the bindings may have changed elsewhere.</summary>
    public void RefreshLabels()
    {
        foreach (var r in rows)
            if (r.label != null) r.label.text = DisplayString(r.action, r.bindingIndex);
    }

    // If the menu closes mid-rebind (its canvas deactivates) or this is torn down, abort cleanly so
    // navigation isn't left disabled. Dispose (not Cancel) so no completion callback re-enters here.
    void OnDisable() => Abort();
    void OnDestroy() => Abort();

    void Abort()
    {
        if (op != null) { op.Dispose(); op = null; }
        EndRebindNow();
    }
}
