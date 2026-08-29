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
    [Tooltip("Looping song for the Tutorial scene.")]
    public AudioClip tutorialMusic;
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
    [Tooltip("Looping interior track that crossfades OVER the scene music while the player is inside the " +
             "Windows building (via WindowsAudio), ducking the scene theme until they leave.")]
    public AudioClip windowsInteriorMusic;
    [Tooltip("Seconds for the scene <-> interior music crossfade. Lower = snappier swap (0 = instant).")]
    public float interiorMusicCrossfadeSeconds = 0.3f;

    [Header("Menu SFX")]
    [Tooltip("Played when the highlighted menu item changes (navigating up/down the list).")]
    public AudioClip menuMove;
    [Tooltip("Played when a menu item is chosen/confirmed (A / click / Submit).")]
    public AudioClip menuSelect;
    [Tooltip("Played when backing out of a selection or sub-screen (B / Cancel).")]
    public AudioClip menuBack;
    [Tooltip("Played when a full-screen menu OPENS (Start menu, Inventory). Shared across those menus.")]
    public AudioClip menuOpen;
    [Tooltip("Played when a full-screen menu CLOSES (Start menu, Inventory). Shared across those menus.")]
    public AudioClip menuClose;

    [Header("Store SFX (separate from the main-menu buttons)")]
    [Tooltip("Played when the highlighted store row changes (navigating up/down).")]
    public AudioClip storeMove;
    [Tooltip("Played when buying / selecting a store row (A) succeeds.")]
    public AudioClip storeSelect;
    [Tooltip("Played when a store purchase is REJECTED — the player can't afford it or already " +
             "owns the maximum allowed of that item.")]
    public AudioClip storeDenied;
    [Tooltip("Played when the Store menu opens (car drives into the store).")]
    public AudioClip storeOpen;
    [Tooltip("Played when the Store menu closes (B, or the car drives out).")]
    public AudioClip storeClose;

    [Header("Upgrade Ramp SFX")]
    [Tooltip("Played when the Upgrade Ramp menu opens (car drives onto the ramp).")]
    public AudioClip rampOpen;
    [Tooltip("Played when the Upgrade Ramp menu closes (B, or the car drives off).")]
    public AudioClip rampClose;
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
    [Tooltip("Looping sound while a Shield is being crafted on the ramp (hold Y) — plays with the " +
             "progress bar and cuts when it stops (release / cancel / close).")]
    public AudioClip shieldCraftLoop;
    [Tooltip("One-shot when a Shield finishes crafting and is added to the inventory.")]
    public AudioClip shieldCrafted;
    [Tooltip("Looping sound while the player is ROTATING the right stick to craft a Grappling Hook — " +
             "plays with the radial gauge and cuts the moment the stick stops or is released.")]
    public AudioClip grappleCraftLoop;
    [Tooltip("One-shot when a Grappling Hook finishes crafting (one full revolution) and is added to " +
             "the inventory.")]
    public AudioClip grappleCrafted;

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

    [Header("Shield Ability SFX (3D, at the car)")]
    [Tooltip("One-shot (3D, at the car) when the Shield is summoned with L3.")]
    public AudioClip shieldActivate;
    [Tooltip("Looping 3D sound at the car while the Shield is up (the ~2 s it stays summoned).")]
    public AudioClip shieldActiveLoop;
    [Tooltip("One-shot (3D, at the car) when the Shield expires and vanishes.")]
    public AudioClip shieldDeactivate;
    [Tooltip("3D playback tuning shared by ALL THREE Shield sounds above (activate / active loop / " +
             "deactivate) — spatial blend, volume, min/max distance, rolloff, doppler. THIS is the " +
             "block to edit when tuning how far away the shield can be heard.")]
    public Spatial3DSettings shieldAudio3D = new Spatial3DSettings();

    [Header("Grappling Hook SFX (3D)")]
    [Tooltip("One-shot at the car's nose when the hook is FIRED (RB).")]
    public AudioClip grappleFire;
    [Tooltip("One-shot AT THE HIT POINT when the hook successfully latches on — it plays out where the " +
             "hook landed, so it doubles as a positional cue for what you caught.")]
    public AudioClip grappleAttach;
    [Tooltip("One-shot at the car when the tether ends — released with RB, deflected by a shield, or " +
             "recalled after missing.")]
    public AudioClip grappleRelease;
    [Tooltip("3D playback tuning shared by all THREE Grappling Hook sounds above. Note the attach sound " +
             "can play up to the hook's full range away (200 m+), so the max distance here wants to be " +
             "generous or a long successful shot will land silently.")]
    public Spatial3DSettings grappleAudio3D = new Spatial3DSettings();

    [Header("Support Ship SFX (3D, at the ship)")]
    [Tooltip("One-shot (3D) when the racer summons their Support Ship with L3+Y.")]
    public AudioClip supportShipActivate;
    [Tooltip("Looping 3D engine/hover sound riding the Support Ship the whole time it is out. This is " +
             "what makes an escorting ship audible to everyone nearby, so keep it quiet and steady.")]
    public AudioClip supportShipLoop;
    [Tooltip("One-shot (3D) when the racer dismisses their Support Ship with L3+Y.")]
    public AudioClip supportShipDeactivate;
    [Tooltip("One-shot (3D) each time the Support Ship takes a NON-FATAL hit — clipped scenery, ate a " +
             "drone round — and survived it. One point of a 5-point pool, so this plays up to four " +
             "times before the ship is lost: it is the pilot's only warning that the pool is draining, " +
             "and it wants to read as clearly SURVIVABLE next to Support Ship Destroyed.")]
    public AudioClip supportShipHit;
    [Tooltip("One-shot (3D) when a 'Support Ship Repair' is spent and the ship gets hit points back. " +
             "Plays AT THE SHIP on every machine. It is the repair's only outward sign - the damage " +
             "tint is flash-only, so a patched-up ship looks no different from a pristine one - and it " +
             "wants to read as clearly RESTORATIVE against Support Ship Hit.")]
    public AudioClip supportShipRepair;
    [Tooltip("One-shot (3D) when the Support Ship is downed by a collision or a projectile — plays at " +
             "the wreck, so it doubles as a positional cue for where the ship was lost. The FINAL hit " +
             "plays this INSTEAD of Support Ship Hit, never both, so the kill is never muddied.")]
    public AudioClip supportShipDestroyed;
    [Tooltip("One-shot (3D) at the muzzle each time the pilot fires the twin lasers. Holding A fires a " +
             "3-round burst, so this plays up to three times in quick succession — keep it short and dry.")]
    public AudioClip supportShipLaserFire;
    [Tooltip("One-shot when a laser round hits something it does NOTHING to — the track, a wall, " +
             "scenery. The dull 'that was a miss' tick. Plays AT THE IMPACT, which can be a long way " +
             "from the ship, and is tuned by the laser prefab's own Environment Audio 3D block rather " +
             "than by supportShipAudio3D.")]
    public AudioClip supportShipLaserHitEnvironment;
    [Tooltip("One-shot when a laser round actually DOES something — pops a player car, damages a drone, " +
             "bursts a boulder. This is the gunner's feedback that a shot counted, so it wants to read " +
             "clearly different from the environment tick. Tuned by the laser prefab's Entity Audio 3D " +
             "block. NOTE: a round absorbed by a car's invulnerability window plays the ENVIRONMENT " +
             "sound instead, because nothing happened — same convention DroneProjectile uses.")]
    public AudioClip supportShipLaserHitEntity;
    [Tooltip("3D playback tuning shared by ALL the Support Ship sounds above — spatial blend, volume, " +
             "min/max distance, rolloff, doppler. THIS is the block to edit when tuning how far away " +
             "the ship can be heard. Note the ship flies up to its offset limits away from its racer, " +
             "so a tight max distance will make it drop out at the edges of the pilot's box.")]
    public Spatial3DSettings supportShipAudio3D = new Spatial3DSettings();

    [Header("LRA SFX")]
    [Tooltip("Looping 2D sound while the player is holding the L+R+A combo to activate the LRA abort " +
             "(plays with the progress bar; cuts when the combo is released or the abort completes).")]
    public AudioClip lraActivateLoop;

    [Header("Portal SFX (uniform across all portals)")]
    [Tooltip("Played (3D) when a portal spawns.")]
    public AudioClip portalSpawn;
    [Tooltip("Looping 3D hum while a portal is active.")]
    public AudioClip portalActiveLoop;
    [Tooltip("Played (3D) when a car / physics object passes into a portal.")]
    public AudioClip portalCollision;
    [Tooltip("Played (3D) when a portal despawns (e.g. it times out) — NOT when the player travels through it.")]
    public AudioClip portalDespawn;
    [Tooltip("One-shot (3D, at the player's arrival point) played at the START of the next scene after " +
             "the player travels through ANY portal — the 'exiting the portal' sound.")]
    public AudioClip portalExit;
    [Range(0f, 1f)] [Tooltip("Master volume for the Portal Exit sound. Scales on top of the global SFX " +
             "level AND the 3D block's own volume below — leave that at 1 and use this as the main knob.")]
    public float portalExitVolume = 1f;
    [Tooltip("3D playback tuning for the Portal Exit sound — spatial blend, volume, min/max distance, " +
             "rolloff, doppler.")]
    public Spatial3DSettings portalExitAudio3D = new Spatial3DSettings();

    [Header("Boost Gate SFX")]
    [Tooltip("Played (3D, at the gate) when a Boost Gate spawns in the hub.")]
    public AudioClip boostGateSpawn;
    [Tooltip("Played (3D, at the gate) when the player car drives through the gate and receives the boost.")]
    public AudioClip boostGateBoost;

    [Header("Windows SFX")]
    [Tooltip("One-shot (3D) when the player car ENTERS the Windows trigger volume.")]
    public AudioClip windowsEnter;
    [Tooltip("One-shot (3D) when the player car EXITS the Windows trigger volume.")]
    public AudioClip windowsExit;

    [Header("Reward SFX")]
    [Tooltip("One-shot (2D) played when the player earns credits by knocking a Drone / Challenger car " +
             "into the kill floor.")]
    public AudioClip knockoffBounty;

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
