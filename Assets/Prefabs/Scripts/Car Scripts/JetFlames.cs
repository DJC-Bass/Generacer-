using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Car accessory that flares the Jet flames for a moment whenever the car fires its Jet (jump).
/// Put this on the JetFlames prefab attached to the player car; on each jump it switches its flame
/// visuals ON for <see cref="flameDuration"/> seconds, then OFF. It finds the car's
/// <see cref="CarController"/> in its parents and listens for the jump event, so attaching the prefab
/// is all the wiring needed.
///
/// The visuals to toggle default to every direct CHILD of this object, so keep this component on an
/// always-active root with the flame meshes/particles as children — that way switching the visuals
/// off never disables this component. You can also assign the exact objects in <see cref="flames"/>.
/// </summary>
public class JetFlames : MonoBehaviour
{
    [Tooltip("Seconds the flames stay visible after a jump.")]
    public float flameDuration = 1f;

    [Tooltip("Objects switched on for the flare (SetActive). Leave empty to use every direct child of " +
             "this object. Don't include this object itself, or it would switch the component off too.")]
    public GameObject[] flames;

    private CarController car;
    private float timer;

    void Awake()
    {
        car = GetComponentInParent<CarController>();

        // Default to this object's direct children as the flame visuals.
        if (flames == null || flames.Length == 0)
        {
            var list = new List<GameObject>();
            foreach (Transform child in transform) list.Add(child.gameObject);
            flames = list.ToArray();
        }
    }

    void OnEnable()
    {
        if (car != null) car.OnJumped += Flare;
        timer = 0f;
        SetFlames(false);   // start hidden until the first jump
    }

    void OnDisable()
    {
        if (car != null) car.OnJumped -= Flare;
    }

    void Flare()
    {
        timer = flameDuration;   // re-jumping mid-flare just refreshes the second
        SetFlames(true);
    }

    void Update()
    {
        if (timer > 0f)
        {
            timer -= Time.deltaTime;
            if (timer <= 0f) SetFlames(false);
        }
    }

    void SetFlames(bool on)
    {
        if (flames == null) return;
        foreach (var f in flames)
            if (f != null) f.SetActive(on);
    }
}
