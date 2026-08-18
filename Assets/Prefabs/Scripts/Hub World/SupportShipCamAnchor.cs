using UnityEngine;

/// <summary>
/// The transform the Support Ship pilot's chase camera actually frames. It reproduces the ship's pose
/// with two deliberate differences, and both exist so the view answers to the PILOT rather than to the
/// racer they're escorting:
///
///  • ROTATION comes from <see cref="SupportShip.FollowFrame"/> — the ship's level-flight frame,
///    WITHOUT the pilot's aim angles. CameraFollow places and aims itself from its target's rotation,
///    so framing the ship directly would swing the whole view with every yaw: angling the ship to shoot
///    left would just drag the shot left and nothing would look aimed. Held apart, the camera keeps the
///    car's heading and the ship visibly angles inside the shot.
///  • POSITION is smoothed in the car's FRAME, not in world space. The ship's world position is
///    "wherever the car is" plus "where the pilot has put it"; only the second half is the pilot's
///    doing. Lagging the world position would make the camera trail by an amount proportional to the
///    car's SPEED — glued at a standstill, dragged along at 600 mph — which reads as the camera being
///    yanked around rather than as the ship being flown. Lagging only <see cref="SupportShip.LocalOffset"/>
///    and rebuilding from the car's CURRENT position means the chase looks identical at any speed and
///    the softness shows up exactly where it was asked for: the pilot sliding the ship about.
///
/// EXECUTION ORDER IS LOAD-BEARING. This has to run after the ship has moved for the frame
/// (SupportShip is -50) and before the camera reads it (CameraFollow is unordered, i.e. 0). Doing it
/// from PilotControlCenter instead would not work — that runs at +1000, after both.
/// </summary>
[DefaultExecutionOrder(-25)]
public class SupportShipCamAnchor : MonoBehaviour
{
    /// <summary>The ship being flown. Cleared when the pilot hands the controls back.</summary>
    public SupportShip ship;

    /// <summary>Seconds of lag behind the pilot's own movement of the ship. The car's motion is NOT
    /// smoothed and never lags, however fast it is going. 0 = rigid.</summary>
    public float followLag = 0.08f;

    private Vector3 smoothedLocal;
    private bool hasPose;

    /// <summary>Jump straight onto the ship, so taking the controls doesn't fly the camera in from
    /// wherever the anchor was last parked.</summary>
    public void Snap()
    {
        hasPose = false;
        LateUpdate();
    }

    void LateUpdate()
    {
        if (ship == null) return;

        Quaternion frame = ship.FollowFrame;
        Transform car = ship.Car;

        // No car to be relative to (mid-swap, or a wreck): fall back to the ship's world pose. Nothing
        // to smooth against in that state, and it only lasts a frame or two.
        if (car == null)
        {
            transform.SetPositionAndRotation(ship.transform.position, frame);
            hasPose = false;
            return;
        }

        Vector3 target = ship.LocalOffset;
        smoothedLocal = (!hasPose || followLag <= 0f)
            ? target
            : Vector3.Lerp(smoothedLocal, target,
                           1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(followLag, 1e-4f)));

        // Rebuilt from where the car is RIGHT NOW, so all of the car's travel is carried rigidly and
        // only the smoothed local part can lag.
        transform.SetPositionAndRotation(car.position + frame * smoothedLocal, frame);
        hasPose = true;
    }
}
