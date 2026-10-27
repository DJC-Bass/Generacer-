using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The hub's start lights (the HUB Lights prefab, under the round clock): a 3-2-1 to the portal opening.
///   • 3 seconds to go — the left light turns red; at 2 the middle joins it; at 1 the right.
///   • GO (the portal opens) — all three switch to green and stay green while the round runs.
///   • Round over — all three go dark (their Off bulb) until the next countdown.
/// Each light shows exactly one of its bulbs at a time. Each red beeps as it lights and the green
/// plays a GO shot (AudioLibrary's Hub Start Lights slots, 3D at the lights). Every machine runs its
/// own lights, so in multiplayer each player hears their own hub's countdown with no extra traffic.
///
/// Reads GameLoopManager every frame rather than listening for events, so it is right the moment it
/// appears — the hub reloads after every single-player race, and the lights simply pick up wherever the
/// round is. Works in single-player and multiplayer alike (see GameLoopManager.SecondsToPortal).
///
/// Attach to the HUB Lights prefab root. The bulbs are found by name — the "Off Bulb…", "Red Bulb…"
/// and "Green Bulb…" children of one parent make one light — and put in order left to right as seen
/// from the front, the side the bulbs stand out on. Nothing needs wiring; fill in Lights by hand only
/// to override that.
/// </summary>
public class HubCountdownLights : MonoBehaviour
{
    [System.Serializable]
    public class StartLight
    {
        public GameObject offBulb;
        public GameObject redBulb;
        public GameObject greenBulb;
    }

    [Tooltip("The lights, LEFT to right as the player sees them. One second of countdown each. Leave " +
             "empty to find them by bulb name.")]
    public StartLight[] lights;

    private int shownReds = -1;   // -1 = nothing applied yet
    private bool shownGreen;

    void Awake()
    {
        if (lights == null || lights.Length == 0) lights = FindLights();
    }

    // Straight away too, so the authored prefab (every bulb on) never shows for a frame.
    void OnEnable() => Refresh();

    void Update() => Refresh();

    void Refresh()
    {
        var glm = GameLoopManager.Instance;
        bool green = false;
        int reds = 0;
        if (glm != null)
        {
            var phase = glm.CurrentPhase;
            green = phase == GameLoopManager.Phase.HubPortalActive || phase == GameLoopManager.Phase.InTrack;

            // One more light each second: 3 s to go lights one, 2 s two, the last second all three.
            float toGo = glm.SecondsToPortal;
            if (!green && toGo >= 0f && toGo <= lights.Length)
                reds = lights.Length + 1 - Mathf.Max(1, Mathf.CeilToInt(toGo));
        }
        Show(reds, green);
    }

    void Show(int reds, bool green)
    {
        if (reds == shownReds && green == shownGreen) return;
        // Sounds mark changes the player watches happen, so none on arrival: a hub that loads (or
        // reloads after a race) mid-countdown or mid-round just shows where things are, silently.
        bool arriving = shownReds < 0;
        bool newRed = !arriving && !green && reds > shownReds;
        bool newGreen = !arriving && green && !shownGreen;
        shownReds = reds;
        shownGreen = green;

        for (int i = 0; i < lights.Length; i++)
        {
            var light = lights[i];
            if (light == null) continue;
            bool red = !green && i < reds;
            SetActive(light.redBulb, red);
            SetActive(light.greenBulb, green);
            SetActive(light.offBulb, !red && !green);
        }

        if (newRed && lights[reds - 1] != null && lights[reds - 1].redBulb != null)
            AudioManager.PlayStartLightBeep(PositionOf(lights[reds - 1].redBulb));   // at the light that just lit
        if (newGreen)
            PlayGo();
    }

    /// <summary>The GO shot, from the middle of the row.</summary>
    void PlayGo()
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (var light in lights)
            if (light != null && light.greenBulb != null) { sum += PositionOf(light.greenBulb); count++; }
        if (count > 0) AudioManager.PlayStartLightGo(sum / count);
    }

    /// <summary>Where a bulb really is: its renderer's centre rather than its transform, since imported
    /// pivots needn't sit on the geometry (the prefab root's is ~100 m from the lights).</summary>
    static Vector3 PositionOf(GameObject bulb) =>
        bulb.TryGetComponent(out Renderer r) ? r.bounds.center : bulb.transform.position;

    static void SetActive(GameObject bulb, bool active)
    {
        if (bulb != null && bulb.activeSelf != active) bulb.SetActive(active);
    }

    StartLight[] FindLights()
    {
        var byLight = new Dictionary<Transform, StartLight>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.parent == null) continue;
            string n = t.name;
            if (n.StartsWith("Off Bulb")) LightOf(byLight, t).offBulb = t.gameObject;
            else if (n.StartsWith("Red Bulb")) LightOf(byLight, t).redBulb = t.gameObject;
            else if (n.StartsWith("Green Bulb")) LightOf(byLight, t).greenBulb = t.gameObject;
        }

        // The bulbs stand out from each light's body toward the viewer, so that offset is the way the
        // row faces; "right" is then right as seen by someone looking at it.
        Vector3 facing = Vector3.zero;
        foreach (var pair in byLight)
        {
            var bulb = pair.Value.redBulb != null ? pair.Value.redBulb : pair.Value.offBulb;
            if (bulb != null) facing += bulb.transform.position - pair.Key.position;
        }
        Vector3 right = Vector3.Cross(transform.up, -facing);

        var bodies = new List<Transform>(byLight.Keys);
        bodies.Sort((a, b) => Vector3.Dot(a.position, right).CompareTo(Vector3.Dot(b.position, right)));

        var ordered = new StartLight[bodies.Count];
        for (int i = 0; i < bodies.Count; i++) ordered[i] = byLight[bodies[i]];
        if (ordered.Length == 0)
            Debug.LogWarning($"[HubCountdownLights] No bulbs named Off/Red/Green Bulb under {name}.", this);
        return ordered;
    }

    static StartLight LightOf(Dictionary<Transform, StartLight> byLight, Transform bulb)
    {
        if (!byLight.TryGetValue(bulb.parent, out var light))
            byLight[bulb.parent] = light = new StartLight();
        return light;
    }
}
