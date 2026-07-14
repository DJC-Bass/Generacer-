using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Persists the player's control rebinds and applies them to every <see cref="GeneracerControls"/>
/// instance. The generated <c>GeneracerControls</c> builds a SEPARATE <see cref="InputActionAsset"/>
/// from JSON each time it's constructed (CarController and CameraSwitcher each make their own), so a
/// rebind done on one instance would not reach the others. The Input System's answer — used here — is
/// to store the diff as binding-override JSON in PlayerPrefs and re-apply it to each fresh instance:
///
///  • The Main Menu CONTROLS screen rebinds its own instance and calls <see cref="Save"/> after each change.
///  • Every consumer calls <see cref="ApplyOverridesTo(GeneracerControls)"/> right after <c>new GeneracerControls()</c>.
///  • Because gameplay scenes load AFTER the menu, a car spawned later picks up the latest rebinds.
///
/// "Reset to defaults" (<see cref="ResetToDefaults"/>) clears the overrides, returning to the bindings
/// authored in <c>GeneracerControls.inputactions</c>.
/// </summary>
public static class InputRebinding
{
    const string OverridesKey = "controls.bindingOverrides";

    /// <summary>Raised after the saved overrides change (a rebind or a reset), so already-live input
    /// consumers can re-sync immediately (e.g. a rebind from the in-game Start Menu) instead of only on
    /// the next scene load.</summary>
    public static event System.Action OverridesChanged;

    /// <summary>True if the player has any saved rebinds (i.e. the defaults have been changed).</summary>
    public static bool HasOverrides => !string.IsNullOrEmpty(PlayerPrefs.GetString(OverridesKey, string.Empty));

    /// <summary>Applies the saved binding overrides (if any) to a freshly-created controls instance.
    /// Call right after <c>new GeneracerControls()</c> in every consumer.</summary>
    public static void ApplyOverridesTo(GeneracerControls controls)
    {
        if (controls != null) ApplyOverridesTo(controls.asset);
    }

    public static void ApplyOverridesTo(InputActionAsset asset)
    {
        if (asset == null) return;
        string json = PlayerPrefs.GetString(OverridesKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
            asset.LoadBindingOverridesFromJson(json);   // replaces any prior overrides (removeExisting = true)
        else
            asset.RemoveAllBindingOverrides();          // no saved rebinds -> (re)assert the authored defaults,
                                                        // also clearing a long-lived instance's stale overrides
    }

    /// <summary>Persists the current binding overrides from the given asset to PlayerPrefs.</summary>
    public static void Save(InputActionAsset asset)
    {
        if (asset == null) return;
        PlayerPrefs.SetString(OverridesKey, asset.SaveBindingOverridesAsJson());
        PlayerPrefs.Save();
        OverridesChanged?.Invoke();
    }

    /// <summary>Clears all binding overrides on the asset (back to the authored defaults) and forgets the
    /// saved JSON, so future instances also start from defaults.</summary>
    public static void ResetToDefaults(InputActionAsset asset)
    {
        if (asset != null) asset.RemoveAllBindingOverrides();
        PlayerPrefs.DeleteKey(OverridesKey);
        PlayerPrefs.Save();
        OverridesChanged?.Invoke();
    }

    /// <summary>True if the control now bound to (action, bindingIndex) is ALSO used by another binding in
    /// the same action map — i.e. a rebind conflict. Outs the friendly name of the action it clashes with.
    /// Composite parents (which have no control of their own) and the binding itself are ignored.</summary>
    public static bool IsBindingInConflict(InputAction action, int bindingIndex, out string conflictName)
    {
        conflictName = null;
        if (action == null) return false;
        var map = action.actionMap;
        if (map == null) return false;

        InputBinding target = action.bindings[bindingIndex];
        string path = target.effectivePath;
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var b in map.bindings)
        {
            if (b.id == target.id) continue;   // skip the binding we just set
            if (b.isComposite) continue;       // composite parents have no control of their own
            if (b.effectivePath == path)
            {
                conflictName = SettingsUI.FriendlyActionName(b.action);
                return true;
            }
        }
        return false;
    }

    /// <summary>Reverts (action, bindingIndex) to the override it had BEFORE a rebind — used to reject a
    /// conflicting rebind. Pass the <c>overridePath</c> captured before the rebind (null/empty = the
    /// authored default).</summary>
    public static void RevertBinding(InputAction action, int bindingIndex, string prevOverridePath)
    {
        if (action == null) return;
        if (string.IsNullOrEmpty(prevOverridePath))
            action.RemoveBindingOverride(bindingIndex);
        else
            action.ApplyBindingOverride(bindingIndex, prevOverridePath);
    }
}
