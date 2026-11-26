using System;
using UnityEngine;

/// <summary>
/// One driver's set of portrait animations for the top-left character box (see
/// <see cref="CharacterPortraitHUD"/>). Make one per character (Create → Generacer → Portrait Character),
/// keep it in <c>Assets/Resources/Portraits/</c> so the HUD can find it, list the car(s) it drives, and
/// drop the character's animation clips into the slots.
///
/// The clips are ordinary Animation-window clips. A clip made by dragging sprite frames into the Scene
/// (which keys a SpriteRenderer's sprite) works, and so does one recorded on a UI Image. Every slot is
/// optional: an empty slot falls back to the next sensible thing (usually Idle), so art can arrive one
/// animation at a time.
///
/// Which state wins when several apply, and the reactions' lengths, are decided by the HUD and the
/// durations below — no Animator Controller to wire.
/// </summary>
[CreateAssetMenu(fileName = "New Portrait Character", menuName = "Generacer/Portrait Character")]
public class PortraitCharacter : ScriptableObject
{
    /// <summary>A one-shot reaction: a clip plus how long the reaction holds the box.</summary>
    [Serializable]
    public class Reaction
    {
        [Tooltip("The reaction's animation. Empty = this reaction is skipped and the portrait carries on.")]
        public AnimationClip clip;

        [Tooltip("How long the reaction holds the box, in seconds. 0 = the clip's own length. A clip " +
                 "shorter than this loops if its Loop Time is ticked, otherwise it holds its last frame.")]
        public float duration;

        public Reaction(float duration) { this.duration = duration; }

        public bool HasClip => clip != null;

        /// <summary>Seconds the reaction lasts: the set duration, else the clip's length.</summary>
        public float Length => duration > 0f ? duration : (clip != null ? clip.length : 0f);
    }

    [Header("Who")]
    [Tooltip("Car names exactly as the Car Selection screen lists them (e.g. GEORGE). Driving one of these " +
             "cars shows this character.")]
    public string[] carNames = { };

    [Tooltip("Shown for any car that has no character of its own yet. Tick it on exactly one character.")]
    public bool fallback;

    [Header("Framing")]
    [Tooltip("1 = the Idle's first frame is exactly as tall as the box's inner square. Every other frame is " +
             "drawn at that same scale and placed by its sprite pivot, so frames of different sizes line up " +
             "and anything bigger than the idle frame spills out over the box (it is never clipped).")]
    public float size = 1f;

    [Tooltip("Nudges the character from the box's centre, in HUD reference pixels (1920×1080). +x right, +y up.")]
    public Vector2 offset = Vector2.zero;

    [Header("Grounded idle")]
    [Tooltip("Looping idle while driving on the ground. A single-frame clip is fine.")]
    public AnimationClip idle;

    [Tooltip("Plays once at random moments while idling on the ground.")]
    public AnimationClip blink;

    [Tooltip("Shortest gap between blinks, in seconds of grounded idle time.")]
    public float blinkIntervalMin = 1f;

    [Tooltip("Longest gap between blinks. The default 1–3 s averages one blink every 2 s.")]
    public float blinkIntervalMax = 3f;

    [Header("Other idles (loop while the state lasts; empty = grounded idle)")]
    [Tooltip("Loops while the car is airborne.")]
    public AnimationClip airborne;

    [Tooltip("Replaces the idle while the car is past the speed barrier.")]
    public AnimationClip limitBreak;

    [Header("Drifting hard (stick fully over while drifting)")]
    [Tooltip("Plays once as the stick reaches full LEFT during a drift, then Hold takes over. Empty = straight to Hold.")]
    public AnimationClip driftLeftTransition;
    [Tooltip("Loops while the stick stays at full left. Empty = the transition's last frame holds.")]
    public AnimationClip driftLeftHold;
    [Tooltip("Plays once as the stick reaches full RIGHT during a drift, then Hold takes over. Empty = straight to Hold.")]
    public AnimationClip driftRightTransition;
    [Tooltip("Loops while the stick stays at full right. Empty = the transition's last frame holds.")]
    public AnimationClip driftRightHold;

    [Header("Reactions (one-shot, play over any of the above)")]
    public Reaction turbo = new Reaction(2f);
    public Reaction boulderHit = new Reaction(5f);
    public Reaction lightningHit = new Reaction(2f);
    public Reaction moneyGet = new Reaction(0.5f);

    /// <summary>True if <paramref name="carName"/> is one of this character's cars (case-insensitive).</summary>
    public bool Drives(string carName)
    {
        if (string.IsNullOrEmpty(carName) || carNames == null) return false;
        foreach (var n in carNames)
            if (!string.IsNullOrEmpty(n) && string.Equals(n.Trim(), carName.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    void OnValidate()
    {
        if (blinkIntervalMin < 0.1f) blinkIntervalMin = 0.1f;
        if (blinkIntervalMax < blinkIntervalMin) blinkIntervalMax = blinkIntervalMin;
        if (size < 0.05f) size = 0.05f;
    }
}
