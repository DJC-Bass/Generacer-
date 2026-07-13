using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays a 3D one-shot when the player car ENTERS this trigger volume, and another when it EXITS.
/// Put it on the Windows prefab (the object with the box collider). Adding it flips the collider to a
/// trigger via <see cref="Reset"/>. Clips come from the shared <see cref="AudioLibrary"/> (Windows
/// Enter / Windows Exit); the 3D playback settings are exposed here for per-prefab tuning.
///
/// The player is found by walking up from the touching collider to the Player tag, so a wheel or
/// sub-collider entering still counts. Every player collider currently inside is tracked, so a
/// multi-collider car fires the enter sound once (first collider in) and the exit once (last one out)
/// instead of once per collider. Self-contained — nothing else needs wiring.
/// </summary>
[RequireComponent(typeof(Collider))]
public class WindowsAudio : MonoBehaviour
{
    [Tooltip("Tag on the player car (or any of its child colliders).")]
    public string playerTag = "Player";

    [Tooltip("3D playback settings for the enter/exit sounds — spatial blend, volume, min/max distance.")]
    public Spatial3DSettings audio3D = new Spatial3DSettings();

    // Player colliders currently inside, so a multi-collider car doesn't fire enter/exit per-collider.
    private readonly HashSet<Collider> inside = new HashSet<Collider>();

    void Reset()
    {
        // Auto-configure the collider as a trigger when the component is added in the editor.
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!IsPlayer(other)) return;

        // First player collider to enter → play the enter one-shot and swap to the interior music.
        if (inside.Count == 0)
        {
            AudioManager.PlayWindowsEnter(transform.position, audio3D);
            AudioManager.EnterWindowsInterior();   // duck the scene theme, crossfade in the interior loop
        }
        inside.Add(other);
    }

    void OnTriggerExit(Collider other)
    {
        // Only react to colliders we counted; when the last one leaves, play the exit one-shot and
        // crossfade back to the scene theme.
        if (inside.Remove(other) && inside.Count == 0)
        {
            AudioManager.PlayWindowsExit(transform.position, audio3D);
            AudioManager.ExitWindowsInterior();
        }
    }

    void OnDisable()
    {
        // Torn down / disabled while the player was inside: restore the scene theme so the interior
        // override doesn't stay stuck on. (No exit one-shot here — this isn't the player driving out.)
        if (inside.Count > 0)
        {
            inside.Clear();
            AudioManager.ExitWindowsInterior();
        }
    }

    bool IsPlayer(Collider other)
    {
        Transform t = other.transform;
        while (t != null)
        {
            if (t.CompareTag(playerTag)) return true;
            t = t.parent;
        }
        return false;
    }
}
