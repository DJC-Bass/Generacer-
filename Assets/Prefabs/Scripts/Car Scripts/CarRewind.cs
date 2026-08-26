using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Records the car's world transform and velocity every physics step and can play that history
/// back in reverse — a "rewind" effect. While rewinding, the car is moved kinematically straight back
/// along the path it travelled; its normal <see cref="CarController"/> and rigidbody physics are
/// suspended for the duration and restored when the rewind ends.
///
/// The Lightning SD ability drives this via <see cref="BeginRewind"/> / <see cref="EndRewind"/>;
/// the rest of the time it just records, so there is always recent history to rewind into.
/// Auto-stops (and restores control) once it reaches the oldest recorded state. Added to the
/// player car at runtime by <see cref="SDAbilityController"/>.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarRewind : MonoBehaviour
{
    [Tooltip("Seconds of recent movement kept available to rewind into.")]
    public float historySeconds = 5f;
    [Tooltip("Seconds of history consumed per second of rewind. 1 = real-time reverse, " +
             "2 = rewinds twice as fast.")]
    public float rewindSpeed = 1f;

    struct Snap { public Vector3 pos; public Quaternion rot; public Vector3 vel; public Vector3 angVel; }

    private readonly List<Snap> history = new List<Snap>();
    private Rigidbody rb;
    private CarController controller;

    private bool rewinding;
    private bool prevKinematic;
    private bool prevControllerEnabled;
    private float playhead;          // fractional index into history while rewinding

    /// <summary>True while actively playing the recorded history back in reverse.</summary>
    public bool IsRewinding => rewinding;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        controller = GetComponent<CarController>();
    }

    void FixedUpdate()
    {
        if (rewinding) StepRewind();
        else Record();
    }

    void Record()
    {
        history.Add(new Snap
        {
            pos = transform.position,
            rot = transform.rotation,
            vel = rb != null ? rb.linearVelocity : Vector3.zero,
            angVel = rb != null ? rb.angularVelocity : Vector3.zero,
        });

        // Keep only the most recent `historySeconds` worth of steps (one entry per step).
        int max = Mathf.Max(1, Mathf.CeilToInt(historySeconds / Mathf.Max(Time.fixedDeltaTime, 1e-4f)));
        int overflow = history.Count - max;
        if (overflow > 0) history.RemoveRange(0, overflow);   // drop the oldest beyond the window
    }

    /// <summary>Begins rewinding from the most recent recorded state. No-op if there's no history.</summary>
    public void BeginRewind()
    {
        if (rewinding || history.Count == 0) return;

        rewinding = true;
        playhead = history.Count - 1;

        // Hand transform control fully to the rewind: make the body kinematic and suspend the
        // controller so neither physics nor driving fights the snapshots written each step.
        if (rb != null)
        {
            prevKinematic = rb.isKinematic;
            rb.isKinematic = true;
        }
        if (controller != null)
        {
            prevControllerEnabled = controller.enabled;
            controller.enabled = false;
        }
    }

    /// <summary>Stops rewinding and restores normal physics / control.</summary>
    public void EndRewind()
    {
        if (!rewinding) return;
        rewinding = false;

        // Capture the velocity recorded at the state we rewound to, then discard the "undone
        // future" so recording resumes cleanly from where we stopped.
        int idx = Mathf.Clamp(Mathf.RoundToInt(playhead), 0, history.Count - 1);
        Vector3 resumeVel = history.Count > 0 ? history[idx].vel : Vector3.zero;
        Vector3 resumeAngVel = history.Count > 0 ? history[idx].angVel : Vector3.zero;
        if (idx + 1 < history.Count) history.RemoveRange(idx + 1, history.Count - (idx + 1));

        if (controller != null) controller.enabled = prevControllerEnabled;
        if (rb != null)
        {
            rb.isKinematic = prevKinematic;
            if (!rb.isKinematic)
            {
                // Resume with the velocity the car had at this point in its history, so it carries
                // on moving forward as it was then instead of stopping dead.
                rb.linearVelocity = resumeVel;
                rb.angularVelocity = resumeAngVel;
            }
        }
    }

    void StepRewind()
    {
        if (history.Count == 0) { EndRewind(); return; }

        int idx = Mathf.Clamp(Mathf.RoundToInt(playhead), 0, history.Count - 1);
        Snap snap = history[idx];
        transform.SetPositionAndRotation(snap.pos, snap.rot);
        // EVERY step of a rewind is a placement, not travel — consecutive snapshots are a whole frame
        // apart and played back at rewindSpeed, so an interpolator left to blend between them would
        // render the car smearing backwards through poses it never held.
        MultiplayerWorld.ClearInterpolationHistory(rb);

        if (playhead <= 0f) { EndRewind(); return; }   // reached the oldest state — can't go further
        playhead -= Mathf.Max(0f, rewindSpeed);
    }
}
