using UnityEngine;

/// <summary>
/// Central bank of the game's audio clips + default mix levels. Because <see cref="AudioManager"/>
/// is created at runtime (no Inspector instance), the clips it plays live here in an asset instead.
/// The manager loads <c>Resources/AudioLibrary</c>; drop your clips onto that asset to wire up sound.
/// Add new slots here as more audio is needed (engine loops, gameplay stingers, ending themes, ...).
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Generacer/Audio Library")]
public class AudioLibrary : ScriptableObject
{
    [Header("Music")]
    [Tooltip("Looping song for the Main Menu scene.")]
    public AudioClip mainMenuMusic;
    [Tooltip("Looping song for the Car Selection scene (its own track, separate from the Main Menu).")]
    public AudioClip carSelectionMusic;
    [Tooltip("Looping song for the HUB world.")]
    public AudioClip hubMusic;
    [Tooltip("Looping song that REPLACES the hub theme while the game-over Drone ending is active.")]
    public AudioClip droneEndingMusic;
    [Tooltip("Looping song that REPLACES the hub theme during the player-victory sequence (BOTS DEFEATED).")]
    public AudioClip playerVictoryMusic;
    [Tooltip("Looping song for the Generacers (flawless-win) ending scene.")]
    public AudioClip generacersEndingMusic;
    [Tooltip("Looping song for the Clipper (Drone-ending escape) ending scene.")]
    public AudioClip clipperEndingMusic;
    [Tooltip("TrackScene song pool — one is picked at random each time you enter the track (never the " +
             "same one twice in a row), so races don't get a stale single theme. Add as many as you like.")]
    public AudioClip[] trackMusic;

    [Header("Menu SFX")]
    [Tooltip("Played when the highlighted menu item changes (navigating up/down the list).")]
    public AudioClip menuMove;
    [Tooltip("Played when a menu item is chosen/confirmed (A / click / Submit).")]
    public AudioClip menuSelect;
    [Tooltip("Played when backing out of a selection or sub-screen (B / Cancel).")]
    public AudioClip menuBack;

    [Header("Store SFX (separate from the main-menu buttons)")]
    [Tooltip("Played when the highlighted store row changes (navigating up/down).")]
    public AudioClip storeMove;
    [Tooltip("Played when buying / selecting a store row (A) succeeds.")]
    public AudioClip storeSelect;
    [Tooltip("Played when a store purchase is REJECTED — the player can't afford it or already " +
             "owns the maximum allowed of that item.")]
    public AudioClip storeDenied;

    [Header("Upgrade Ramp SFX")]
    [Tooltip("Looping sound while a Turbo is being crafted on the ramp — plays with the progress bar " +
             "and cuts when it stops (release / cancel / close).")]
    public AudioClip turboCraftLoop;
    [Tooltip("One-shot when a Turbo finishes crafting and is added to the inventory.")]
    public AudioClip turboCrafted;
    [Tooltip("Looping sound while a Jet is being crafted on the ramp — plays with the progress bar " +
             "and cuts when it stops (release / cancel / close).")]
    public AudioClip jetCraftLoop;
    [Tooltip("One-shot when a Jet finishes crafting and is added to the inventory.")]
    public AudioClip jetCrafted;

    [Header("Vehicle SFX (universal — same for every car)")]
    [Tooltip("One-shot played when Turbo Boost activates.")]
    public AudioClip turboBoost;
    [Tooltip("One-shot played when the car jumps (Jet).")]
    public AudioClip jump;
    [Tooltip("Looping tire-screech played while drifting; its volume + pitch rise with steering sharpness.")]
    public AudioClip driftScreech;
    [Tooltip("One-shot played when the car lands back on the ground after being airborne.")]
    public AudioClip carLanding;
    [Tooltip("One-shot (3D, at the car) played when the car enters a loop and the Loop Speed " +
             "Multiplier kicks in — the same moment the loop FOV kick starts.")]
    public AudioClip loopBoost;

    [Header("Obstacle SFX")]
    [Tooltip("Played (in 3D, at the strike location) when a lightning warning column appears.")]
    public AudioClip lightningWarning;
    [Tooltip("Pool of strike sounds — one is chosen at random each time the bolt strikes, so strikes " +
             "aren't all the same clip. Add as many as you like.")]
    public AudioClip[] lightningStrike;
    [Tooltip("Played (3D) when a boulder spawns / launches.")]
    public AudioClip boulderSpawn;
    [Tooltip("Looping 3D 'on fire' sound while a boulder is airborne.")]
    public AudioClip boulderFly;
    [Tooltip("Played (3D) when a boulder impacts something.")]
    public AudioClip boulderImpact;
    [Tooltip("Played (3D, at the muzzle) when a drone fires its DronePissBall projectile.")]
    public AudioClip droneShoot;
    [Tooltip("Played (3D) when the DronePissBall projectile hits an object / the environment.")]
    public AudioClip projectileHitEnvironment;
    [Tooltip("Played (3D) when the DronePissBall projectile hits the Player.")]
    public AudioClip projectileHitPlayer;

    [Header("SD Ability SFX (uniform across all SDs)")]
    [Tooltip("One-shot (3D, at the car) when an SD ability is activated.")]
    public AudioClip sdActivate;
    [Tooltip("Looping 3D sound at the car while an SD ability is active.")]
    public AudioClip sdActiveLoop;
    [Tooltip("One-shot (3D, at the car) when an SD ability is deactivated.")]
    public AudioClip sdDeactivate;

    [Header("Portal SFX (uniform across all portals)")]
    [Tooltip("Played (3D) when a portal spawns.")]
    public AudioClip portalSpawn;
    [Tooltip("Looping 3D hum while a portal is active.")]
    public AudioClip portalActiveLoop;
    [Tooltip("Played (3D) when a car / physics object passes into a portal.")]
    public AudioClip portalCollision;
    [Tooltip("Played (3D) when a portal despawns (e.g. it times out) — NOT when the player travels through it.")]
    public AudioClip portalDespawn;

    [Header("Boost Gate SFX")]
    [Tooltip("Played (3D, at the gate) when a Boost Gate spawns in the hub.")]
    public AudioClip boostGateSpawn;
    [Tooltip("Played (3D, at the gate) when the player car drives through the gate and receives the boost.")]
    public AudioClip boostGateBoost;

    [Header("Player Victory SFX")]
    [Tooltip("One-shot (2D — screen UI) played when the BOTS DEFEATED banner begins fading in.")]
    public AudioClip victoryBanner;

    [Header("Speed Barrier SFX")]
    [Tooltip("One-shot (3D, riding the car) when the player FIRST breaks the speed barrier. Bypasses " +
             "the broken-barrier low-pass muffle so it's heard clean.")]
    public AudioClip speedBarrierBreak;
    [Range(0f, 1f)] [Tooltip("Volume of the speed-barrier BREAK stinger. Scales on top of the global SFX level.")]
    public float speedBarrierBreakVolume = 1f;
    [Tooltip("One-shot (3D, riding the car) when the player drops back below the barrier. Bypasses the " +
             "muffle so it's heard clean.")]
    public AudioClip speedBarrierLeave;
    [Range(0f, 1f)] [Tooltip("Volume of the speed-barrier LEAVE stinger. Scales on top of the global SFX level.")]
    public float speedBarrierLeaveVolume = 1f;

    [Header("Default Mix (0..1)")]
    [Range(0f, 1f)] public float musicVolume = 0.6f;
    [Range(0f, 1f)] public float sfxVolume = 0.9f;
}
