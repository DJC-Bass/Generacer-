# Generacer — Session Handoff

Arcade racing game, **Unity 6, URP**. Persistent-singleton architecture: systems self-bootstrap via
`RuntimeInitializeOnLoadMethod` onto a DontDestroyOnLoad `PlayerSystems` object (inventory, HUDs, menus,
tutorial guide, event-system guard) or their own object (`AudioManager`, `GameLoopManager`,
`SkyboxHueRandomizer`). Most UI is **code-built** (no scene canvases). Scene flow:
`MainMenu → CarSelection → Bootstrap → HubWorld ⇄ TrackScene`, plus `Tutorial`, `GeneracersEnding`,
`ClipperEnding`.

---

## NEXT TASK (this is why a fresh agent is here)
**Settings are COMPLETE in BOTH menus — no settings work is outstanding.** The Main Menu has AUDIO / VIDEO /
CONTROLS, and the in-game Start Menu now mirrors them (AUDIO + CONTROLS sub-screens, and VIDEO living in the
SETTINGS sub-screen next to the Tutorial-tips toggle). Suggested follow-ups (pick per the user): build ONLINE
MULTIPLAYER; assign the still-empty AudioLibrary slots (below); optionally **retrofit `MainMenuController` onto
the shared `SettingsUI` + `RebindController`** (it still has its own inline `CreateSlider` /
`CreateOptionSelector` / rebind — the Start Menu already uses the shared versions, so this is a dedupe cleanup).

## Settings in the in-game Start Menu (mirrors the Main Menu)
`StartMenuController` (persistent, on PlayerSystems) builds its AUDIO / CONTROLS / SETTINGS sub-screens from
two shared helpers so they match the Main Menu's behaviour:
- **`UI/SettingsUI.cs`** (static) — themed widget builders shared by both menus: `VolumeSlider`, `OptionCycler`
  (an `OptionSelector`), `ResolutionOptions`, `FullscreenModes/Labels/FullscreenIndexOf`, `WireVerticalWrap`
  (Selectable overload), `FriendlyActionName`, `PartLabel`, `NewText`. Pass a `SettingsUI.Theme` (colours) so
  each menu keeps its palette (Start Menu = `StartMenuConfig` blues).
- **`UI/RebindController.cs`** (MonoBehaviour) — the shared interactive-rebind flow (Begin / Finish / Cooldown /
  ResetAll / `IsRebinding`), with nav-suppression + Start/Esc cancel. Lives on the Start Menu's canvas (only
  ticks while the menu is open). The host checks `IsRebinding` to suppress its own input during a rebind.
- **Rebind conflict detection.** After a completed rebind, `InputRebinding.IsBindingInConflict(action, bindingIndex,
  out name)` checks whether the chosen control is already used by another binding in the `Driving` map (skips the
  binding itself + composite parents, compares `effectivePath`). On a clash the rebind is **rejected**:
  `InputRebinding.RevertBinding` restores the pre-rebind override (captured in Begin/StartRebind), the row flashes
  "in use" for ~1.1s, and `AudioManager.PlayStoreDenied()` buzzes. BOTH menus use this (the Main Menu keeps its
  own inline rebind but calls the same two `InputRebinding` helpers), so behaviour matches.
- **Start Menu specifics:** AUDIO = Music/SFX slider rows; CONTROLS = binding rows + RESET (compact rows,
  vertically centred); SETTINGS = the Tutorial toggle followed by RESOLUTION / DISPLAY MODE / QUALITY / V-SYNC
  option rows (`BuildSettingsPanel`). `Update`/`LateUpdate` early-out while `rebind.IsRebinding`.
  CONTROLS row size is tunable via `StartMenuConfig` (`controlsRowHeight` / `controlsRowFontScale` /
  `controlsRowSpacing`) so the (now 11-row) list stays clear of the title. All Start-Menu text renders in caps
  via `FontStyles.UpperCase` in `StartMenuController.NewText` + `SettingsUI.NewText` (only the Start Menu uses
  the latter; the Main Menu has its own text factory and is unaffected).
- **In-game rebinds apply immediately:** `InputRebinding` now raises a static `OverridesChanged` event on
  Save/Reset; `CarController`, `CameraSwitcher` and `SDAbilityController` subscribe and re-apply overrides on the
  spot (previously an in-game rebind only took effect on the next scene load).

## Settings screen (complete)
Code-built on the existing `MainMenuCanvas` (no scene setup). `MainMenuController.OnSettings()` opens a
**category chooser** (AUDIO / VIDEO & GRAPHICS / CONTROLS); B/Escape steps back one level. Key methods:
`ShowCategories` / `ShowAudio` / `ShowVideo` / `ShowControls` (panel switching via `SetSettingsPanels`),
`GoBack` + `BackPressed` (gamepad B / Esc), `FocusFirst` (selection + move-SFX priming),
`BuildSettingsScreens` → `BuildCategoryColumn` / `BuildAudioPanel` / `BuildVideoPanel` / `BuildControlsPanel`.

- **AUDIO.** `BuildAudioPanel` has **MUSIC + SFX** sliders (code-built Unity `Slider`s, 0..1) with a live
  `NN%` readout. Up/Down wraps between the two; Left/Right adjusts the focused one. Changes call
  `AudioManager.SetMusicVolume/SetSfxVolume` (live) **and** persist via `UI/GameSettings.cs`. Helpers:
  `CreateSlider` / `BuildAudioRow` / `WireSliderPair`.
- **VIDEO/Graphics.** `BuildVideoPanel` has **RESOLUTION / DISPLAY MODE / QUALITY / V-SYNC** rows, each a
  **`UI/OptionSelector.cs`** — a `Selectable` subclass whose `OnMove` cycles a fixed option list on
  Left/Right (like `Slider` does for its value) while Up/Down navigate. Sources: `Screen.resolutions`
  (deduped by w×h), `FullScreenMode` (Fullscreen/Borderless/Windowed), `QualitySettings.names`,
  `QualitySettings.vSyncCount`. Handlers apply live AND persist. Nav: `WireVerticalSelectors`.
- **CONTROLS (rebinding + reset).** `BuildControlsPanel` lists one row per rebindable binding of the
  `Driving` map (composite parts shown as e.g. "Throttle (+)"; `FriendlyActionName` prettifies a couple,
  e.g. SD→"SD Card", RearView→"Rear View"), each row a Button that A → interactive rebind
  (`InputAction.PerformInteractiveRebinding`), plus a **RESET TO DEFAULTS** button. Rebinds are persisted
  as binding-override JSON by **`UI/InputRebinding.cs`** and re-applied by every consumer —
  `CarController.OnEnable` and `CameraSwitcher.OnEnable` call `InputRebinding.ApplyOverridesTo(controls.asset)`
  right after `new GeneracerControls()` (each consumer builds its OWN asset from JSON, so this is how a
  menu rebind reaches gameplay; it works because gameplay scenes load after the menu). Reset =
  `RemoveAllBindingOverrides` + clear the pref. During a rebind, `EventSystem.sendNavigationEvents` is
  turned off (and a 0.2s cooldown after) so the captured press doesn't leak into the menu; cancel via
  gamepad **Start** (excluded from binding, handled in `Update`) or **Esc** (`WithCancelingThrough`).
- **SD ability is now a rebindable action.** Added an **"SD"** button action (default `<Gamepad>/dpad/up`)
  to the `Driving` map — in BOTH `GeneracerControls.inputactions` AND the generated `GeneracerControls.cs`
  (embedded JSON + `m_Driving_SD` field + `FindAction` + `@SD` accessor; if Unity regenerates the wrapper
  from the asset it stays correct — no class implements `IDrivingActions`). `SDAbilityController` no longer
  reads `Gamepad.dpad.up` directly; it owns a `GeneracerControls`, enables `Driving`, reads
  `controls.Driving.SD.triggered`, and — because it's a persistent singleton created BEFORE the menu —
  re-applies overrides on every `sceneLoaded` (so a menu rebind/reset reaches it). `InputRebinding.ApplyOverridesTo`
  was made idempotent (empty JSON → `RemoveAllBindingOverrides`) so re-syncing and reset both work on a
  long-lived instance. **Pattern for adding another rebindable action later:** add it to the `.inputactions`
  + the `.cs` embedded JSON/accessors, read it via `controls.Driving.<Name>`, and it auto-appears in the
  CONTROLS list.
- **Airborne self-leveling is now MANUAL (hold Self-Level, default Y/buttonNorth).** Added a `SelfLevel`
  button action to the `Driving` map (both `.inputactions` + generated `.cs`, same pattern as SD; shows as
  "Self-Level" in both Controls screens). `CarController` changes: `airDriftGracePeriod` renamed
  `airAbilitiesGracePeriod` (`[FormerlySerializedAs]` keeps tuned values); after that grace, **manual
  rotation on ALL THREE local axes is available immediately** (`ApplyManualAirRotation`): right stick
  up/down = PITCH on local X (`Pitch` action); right stick left/right = YAW on local Y (the `Yaw` action,
  `<Gamepad>/rightStick/x`, rebindable like SD/SelfLevel; left = negative, right = positive, speed
  `manualYawSpeed`); right stick left/right **while holding Throttle− (default LT, the reverse trigger,
  inert midair) past a quarter-pull** = ROLL on local Z (left = positive, right = negative, speed
  `manualRollSpeed`; the modifier reads the Throttle ACTION's value — `throttleInput < -0.25f` — not the
  raw trigger, so it follows a rebound throttle and needs no extra action/binding; RT stays pure throttle). All pure MoveRotation — linear velocity
  untouched, so it can't add speed and drift stays lossless (it re-derives its basis from the new heading
  every step). Otherwise rotation is pure physics; the old constant roll
  auto-level is gone) and **self-leveling only runs while Y is held**
  (`UpdateManualSelfLevel` + `selfLevelHeld`/`selfLevelArmed`): a fresh press arms the hold, releasing
  mid-level stops it (press again to continue), and reaching fully level (`IsFullyLevel`, ~0° epsilon —
  distinct from the looser `airDriftLevelThreshold` that still gates air drift) consumes the hold so a NEW
  press is needed once tilted again. Leveling takes priority over manual pitch for the frames it runs.
- **Grounded camera swivel on the right stick (`CameraFollow`), both axes.** While the car is on the ground
  the right stick orbits the follow camera around it — left/right (`maxSwivelYawAngle`, default 90°) and
  up/down (`maxSwivelPitchUpAngle` 60° / `maxSwivelPitchDownAngle` 25°) — easing back to neutral on release.
  Both cameras get it, main and Rear View, and the rear one needs no special case: its offset AND its
  look-ahead are both mirrored, so the same signed angles pan its view the same way on screen.
  **Diagonals work** (a northeast push orbits northeast): the two angles compose as
  `AngleAxis(yaw, up) * AngleAxis(pitch, right)` (yaw outermost — the standard orbit order, so the angles
  don't skew as they compound), the deadzone is **radial** on the stick vector rather than per-axis (a
  per-axis deadzone clips one component on a gentle diagonal and bends it back toward a cardinal), and the
  pair is eased with a single `Vector2.SmoothDamp` so a diagonal transition stays a straight line instead
  of the axes arriving apart. Up/down have separate limits, so the envelope is an ellipse — a full diagonal
  reaches ~71% of each. Down is deliberately tighter: nothing in this project does camera collision and the
  default offset only sits ~23° above the car, so a large down-swivel dips through the ground.
  The orbit is applied to the offset (*inside* `smoothedRot`, so it rides on the lazy-Susan lag; being a
  pure rotation it preserves the offset distance — the camera moves on a sphere, never toward the car)
  **and** to the look-ahead point (`target.rotation * (SwivelRotation * Vector3.forward)` — the same orbit
  in the car's frame), so the rig swings as one piece and the car stays framed instead of sliding out of shot.
  **The stick hand-off is gated on `CarController.IsAirborne`** — the very flag that unlocks the car's air
  rotation — so the two are exact complements: the stick never drives both at once and never drives
  neither. Going airborne mid-swivel drops the target angle to 0 and the camera glides home even if the
  player never released; landing picks the stick back up. Because `IsAirborne` is false through the
  air-abilities grace window, crests and short hops keep the camera under player control rather than
  snapping it back. Input comes from `CarController.ManualYawInput` / `ManualPitchInput` (new properties) —
  the car's own poll of the rebindable `Yaw`/`Pitch` actions — so there's no second `InputActionAsset` to
  keep in sync and rebinds are free; also gated on `!MenuState.AnyOpen` so menu navigation doesn't swing
  the camera. Note this means the SAME stick axes drive the camera on the ground and the car in the air,
  which is the whole point of gating them on complementary conditions.
  Fields: `enableSwivel`, `maxSwivelYawAngle`, `maxSwivelPitchUpAngle`, `maxSwivelPitchDownAngle`,
  `swivelSmoothTime` (out), `swivelReturnSmoothTime` (home), `swivelDeadzone`, `invertSwivelHorizontal`,
  `invertSwivelVertical`. The two renames carry `[FormerlySerializedAs]` (`maxSwivelAngle`,
  `invertSwivel`), and the pitch angles are `[Range]`-capped at 85° — at 90°+ the camera sits directly
  over the car and `Quaternion.LookRotation` can go degenerate against a world-up `camUp`.
  **Direction defaults:** pushing RIGHT orbits the camera to the car's RIGHT side and pushing UP lifts it
  ABOVE the car (literal "orbits in the direction you input"); `invertSwivelHorizontal` gives the
  conventional look-stick feel where pushing right pans the VIEW right, `invertSwivelVertical` is the
  classic invert-Y.
  **`rotationSmoothTime` is forced to 0 for the duration of a swivel** (`swivelEngaged` latch). The swivel
  already carries its own smoothing, so leaving the aim lag layered on top double-smooths the look-around
  and it drags behind the stick. The latch sets on the first frame off neutral and clears only once the
  camera is fully home — deliberately spanning the glide back too, so the return is as crisp as the swing
  out. "Fully home" needs a threshold (`SwivelNeutralEpsilon`, 0.05°) because `SmoothDamp` only approaches
  asymptotically; under it the swivel is snapped to exact zero so neutral is a real state, not a shrinking
  tail. The removal is **ramped, not cut**: `aimSmoothTime = rotationSmoothTime * (1 - aimEase)`, where
  `aimEase` climbs 0→1 over the public `swivelAimEaseTime` (default 0.15s, linear `MoveTowards` so the
  field reads as real seconds). Without that ramp, starting a swivel mid-corner snaps away whatever aim
  lag had accumulated — a few degrees, invisible on a straight but a visible pop in a turn. The ramp is
  **one-directional by design**: `aimEase` drops straight back to 0 on release, which is pop-free because
  unsmoothed aim leaves the camera exactly ON its desired rotation every frame, so restoring the easing
  has no gap to reveal. Set `swivelAimEaseTime` to 0 for the old hard cut.
- **`CameraFollow` target cache is now self-healing** (`RefreshTargetCache` in `LateUpdate`). `targetRb`/
  `targetCar` were resolved once in `Start`, but `PlayerCarSwapper` (car select) and
  `TrackGenerator.AttachCamera` (track spawn) both re-point `target` afterwards — leaving those cached on a
  destroyed car, or null. That silently killed the turbo/loop FOV kicks and the speed-barrier grounded
  grace; the swivel would have hit the same. Re-resolves only when `target` actually changes, and re-seeds
  `smoothedRot` so a swap doesn't swing the camera.
- **Air-drift runaway-speed bug ROOT-CAUSED & FIXED; drift restored to grace-period unlock.** The old
  `ApplyAirDrift` flattened BOTH car forward and car right onto the horizontal plane; with pitch+roll
  combined (angled launches) those flattened axes are non-perpendicular, and splitting/rebuilding the
  velocity on skewed axes double-counts their overlap — injecting speed every physics step (exponential
  runaway; explosive with the prefab tuning `airDrag 0.05`, `airDriftSpeed 75`). The old auto-leveler only
  masked it by pinning roll to 0 (the one orientation where the math is safe). Fix: `driftAxis =
  Vector3.Cross(Vector3.up, forwardAxis)` — perpendicular by construction, so the rebuild is lossless and
  only the sideways component can change (bounded by MoveTowards), plus a belt-and-braces horizontal-speed
  clamp (old horizontal speed + one drift step, never triggers with the orthonormal axes). Vertical speed
  untouched — drift never fights gravity. The interim `airDriftUnlocked` self-level-prerequisite latch was
  REMOVED. **Air drift AND the air-brake dive now work at ANY orientation** (rolled, sideways, inverted)
  the moment the grace period expires: the 45° tilt guard was dropped from `ApplyAirDrift` (safe — the
  orthonormal rebuild is lossless in every pose; drift only skips the odd frame where the nose is
  near-vertical and "sideways to the heading" is undefined), and `ApplyAirBrakeGravity` is ungated.
  `IsRollLevel()` and `airDriftLevelThreshold` were DELETED (prefabs' stale `airDriftLevelThreshold: 5`
  YAML line is silently ignored by Unity). Air drift models a steady side-wind: a world-horizontal push
  left/right of the heading (matches the screen even while inverted), vertical speed untouched. Self-Level
  (Y) is now purely cosmetic re-orientation — no air ability depends on it.
- **Persistence.** `UI/GameSettings.cs` (static PlayerPrefs, mirrors `TutorialSettings`):
  `MusicVolume`/`SfxVolume`, `ResolutionWidth/Height` (+`SetResolution`), `FullScreenModeValue`,
  `QualityLevel`, `VSync`, each with a `Has*` flag. `AudioManager.Awake` applies audio volumes on boot
  over the AudioLibrary defaults; `GameSettings.ApplyVideoSettings()`
  (`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`) applies quality→vsync→resolution/fullscreen. Control
  rebinds live in their own pref via `InputRebinding`.
- **Do NOT create another EventSystem** — `EventSystemGuard` (bootstrapped) enforces exactly one.
  MainMenuController's own `EnsureEventSystem` is a safe no-op.
- **Gotchas:** (1) Code-built `Slider` needs `fillRect`/`handleRect`/`targetGraphic` + the standard
  child hierarchy (see `CreateSlider`); set `slider.value` BEFORE `AddListener`. (2) An
  `OptionSelector`/`Slider` consumes Left/Right only when `selectOnLeft/Right == null` (explicit nav).
  (3) Resolution/fullscreen are largely **no-ops in the editor** — test in a build; quality/vsync show
  live. (4) `QualitySettings.SetQualityLevel` can reset vSync per level, so `OnQualityChanged` re-applies
  the saved VSync. (5) Rebinding: the cancel control must be **excluded from binding** (Start) or set via
  `WithCancelingThrough` (Esc), else the cancel press would itself be bound. (6) Rebinds only reach a
  consumer that calls `InputRebinding.ApplyOverridesTo` after creating its `GeneracerControls` — if a new
  input consumer is added later, it must do the same.

---

## Design rules (keep these — final architectural decisions)
- **Audio 2D vs 3D:** only menu SFX + music are 2D; every world/gameplay sound is 3D (so remote players
  hear each other once multiplayer lands).
- **Per-listener effects for local-only:** the speed-barrier low-pass muffle is an `AudioLowPassFilter`
  on the **AudioListener** (not a mixer, not per-source) so only the local player who broke the barrier
  is muffled. Sounds that must punch through it set `AudioSource.bypassListenerEffects = true`.
- **Recolor a material INSTANCE, never the shared asset** — both the random road hue (TrackGenerator) and
  the random skybox hue (SkyboxHueRandomizer) `new Material(...)` and free the instance later, so the
  `.mat`/skybox assets on disk keep their authored colors.
- **Loops** are each owned by their component (engine, boulder-fly, portal-active, drift, SD-active,
  craft, LRA-charge, windows-interior) — its own `AudioSource`, `dopplerLevel = 0`.
- **Use `.ogg`/`.wav` for all music + looping SFX, never `.mp3`** (MP3 encoder padding gaps the loop).

---

## Current state
Everything below is **code-complete and compiles**. The prior session built the audio-system
foundation (`AudioManager` + `AudioLibrary` ScriptableObject at `Assets/Resources/AudioLibrary.asset`,
`Spatial3DSettings`, `PortalAudio`, `CarEngineAudio`, `BoulderAudio`, per-scene music, endings). **This
session** extended audio and added visual FX + gameplay systems + fixed two editor-freeze bugs (below).

### AudioLibrary slots STILL EMPTY (`{fileID: 0}`) — assign clips (OGG/WAV)
As of the current `AudioLibrary.asset`: `playerVictoryMusic`, `menuClose`, `carLanding`,
`lightningWarning`, `sdActiveLoop`, `portalSpawn`, `portalDespawn`, `victoryBanner`.
(The user has been assigning clips live; several slots intentionally reuse a **placeholder** clip and may
want distinct ones: `turboCraftLoop`==`jetCraftLoop`; `projectileHitEnvironment`==`projectileHitPlayer`;
`loopBoost`/`boostGateBoost` reuse `turboBoost`; `menuOpen` reuses `menuSelect`; `windowsEnter`==
`windowsExit`; `speedBarrierBreak`==`speedBarrierLeave`.)

---

## What this session added / changed

### New scripts (each has a hand-written `.meta` with a fixed GUID)
- `Audio/WindowsAudio.cs` — on the Windows prefab's box collider (trigger). 3D enter/exit one-shots
  (`windowsEnter`/`windowsExit`), fires once per multi-collider car via an inside-collider `HashSet`, and
  drives the **interior-music crossfade** (ducks scene music to the interior track while inside).
- `Car Scripts/PortalExitAudio.cs` — on each **selectable player-car prefab**. `Start()` calls
  `AudioManager.TryPlayPortalExit(transform)`; plays `portalExit` off the car if it spawned into a
  scene reached via a portal.
- `Car Scripts/JetFlames.cs` — on the JetFlames accessory (child of the car). Subscribes to
  `CarController.OnJumped`, switches its child flame visuals on for ~1s per jump.
- `Hub World/SpeedCheck.cs` — speed-gated barrier: collider is solid unless the player car is faster than
  `minSpeedMph` (default 400), then flips `isTrigger` so a fast car passes. Tracks player speed each
  FixedUpdate.
- `GameLoopScripts/RoundDirectionalLightToggle.cs` — 33% chance per TrackScene load to disable the
  directional light for a "blackout" round. No-op without a `GameLoopManager`.
- `UI/TutorialGuide.cs` + `UI/TutorialGuideConfig.cs` + `Resources/TutorialGuideConfig.asset` — Tutorial
  scene on-screen guide: top-center messages, ~3s auto-advance, D-pad ◄ ► to browse, **loops** (wraps
  both ways). Hides + pauses its timer while any menu is open or when turned off in Settings.
- `UI/TutorialSettings.cs` — PlayerPrefs wrapper for the tutorial-guide on/off toggle. **Template for the
  new GameSettings.**
- `UI/EventSystemGuard.cs` — bootstrapped; guarantees exactly one EventSystem (freeze fix, see below).
- `SkyboxHueRandomizer.cs` — bootstrapped; when a scene's skybox is the `SimpleSkybox` (Skybox/Procedural),
  randomizes `_SkyTint` and `_GroundColor` to **independent** random hues (S/V preserved) on an instance,
  each scene load.

### Modified systems
- `CameraFollow.cs` — (1) turbo FOV-kick hook `TriggerTurboFOVKick()` for the BoostGate; (2) sustained
  **speed-barrier FOV kick** with hysteresis (engage `speedBarrierMph`=750, release
  `speedBarrierReleaseMph`=700); (3) **per-listener low-pass muffle** during the barrier (crossfades with
  the FOV smoothing, log-space cutoff), only on the camera holding the active AudioListener; (4) fires
  `speedBarrierBreak`/`speedBarrierLeave` stingers on the barrier edge (bypass the muffle); (5)
  grounded-grace gate (`speedBarrierGroundedGrace`, default 1s) — force-exits the barrier when airborne,
  which also avoids the kill-floor audio pop.
- `CarController.cs` — turbo **tire trails** (rear `TrailRenderer`s; emit on real turbo **or**
  `TriggerTurboTrail()` from BoostGate **or** `IsLoopGravityCut`; `turboTrail*` fields incl.
  `turboTrailHeightOffset`); `AirborneTime` property; `loopBoost` one-shot on the loop-flag rising edge;
  `OnJumped` event (for JetFlames).
- `AudioManager.cs` — **interior-music crossfade layer** (2nd `interiorSource`, `PlayInteriorMusic` /
  `StopInteriorMusic`, per-frame crossfade in `Update`, `interiorMusicCrossfadeSeconds` from library);
  **portal-exit** system (`ArmPortalExit` static flag / `TryPlayPortalExit(transform)` flag-consuming /
  `PlayPortalExit(transform)` unconditional; stale flag cleared when a non-gameplay scene loads);
  `PlaySfxFollow(clip, follow, settings, bypassListenerEffects, volumeScale)` (rides a transform);
  volume refactor to `musicBaseVolume`; many new static `Play*` helpers.
- `AudioLibrary.cs` — ~20 new slots + `portalExitVolume` + `portalExitAudio3D` (Spatial3DSettings) +
  `interiorMusicCrossfadeSeconds` + `speedBarrier*Volume`.
- `BoostGate.cs` — spawn/boost 3D audio, FOV kick, turbo trail on drive-through.
- `StoreController.cs` — `storeDenied` on failed buy; **item descriptions** in the info line (purchase
  message takes over then reverts; success=green, fail=red); store open/close audio.
- `UpgradeRampController.cs` — ramp open/close audio.
- `StartMenuController.cs` — real SETTINGS panel (Tutorial-tips toggle) **← reuse for MainMenu**; Start-menu
  open/close audio; `extraScenes` so Start opens in the Tutorial scene.
- `InventoryView.cs` — menu open/close audio.
- `DroneCar.cs` — `knockoffBounty` 2D one-shot in `AwardKnockoffBounty`.
- `LraAbortController.cs` — looping `lraActivateLoop` while holding L+R+A; arms portal-exit on the hub
  return.
- `HubSceneController.cs` — drone-ending swarm plays `portalExit` per spawned drone; `droneEndingSpawnRateJitter`.
- `TrackGenerator.cs` — random **road-material hue** per generation (`_BaseColor` on an instance).
- `PlayerInventory.cs` — bootstrap now also adds `EventSystemGuard` (first) + `TutorialGuide`.

---

## Bugs hit & fixed this session (the "failed attempts")
1. **Editor freeze on TrackScene exit** — `LightningStrike.SpawnBolt` built a **convex `MeshCollider`
   from a 9000-unit zigzag ribbon mesh**; PhysX cooked a degenerate hull every strike (10k+ "triangles
   > 500 units" warnings) and hung the editor during scene-switch physics teardown (worse with many
   boulders/hulls near the car). **Fix:** replaced with a primitive vertical `CapsuleCollider`. Diagnosed
   from `%LOCALAPPDATA%\Unity\Editor\Editor-prev.log`.
2. **Editor freeze / input hang from duplicate EventSystems** — `StartMenuController` created a
   DontDestroyOnLoad EventSystem that then coexisted with the menu scenes' own → "There are 2 event
   systems" spam every frame + two `InputSystemUIInputModule`s fighting. **Fix:** `EventSystemGuard`
   (bootstrapped first) keeps one EventSystem and strips extras on each scene load.
3. **Portal-exit regression** — first attempt had AudioManager poll for `"Player"` and attach via
   `PlaySfxFollow` to whatever it found; in the hub it grabbed the **placed** car right before
   `PlayerCarSwapper` destroyed it, so the parented sound died. **Fix:** moved playback to the
   `PortalExitAudio` component ON the car prefab (plays off the real, persistent car on its `Start`).
4. **`GameLoopManager.Awake` called `LoadScene` before the singleton guard** (latent landmine) — moved
   behind the guard so a duplicate manager can't bounce scenes.
5. **Diagnosis method:** on a freeze, read `Editor.log` / `Editor-prev.log` **before** killing Unity —
   the repeated spam / last real lines reveal the cause (a native hang dump ≠ the cause).

### Carried-over gotchas (still true)
- Changing a C# **field default** does NOT update already-serialized prefab instances — hand-edit the
  prefab or reset the component.
- **Bootstrap ordering** across `RuntimeInitializeOnLoadMethod`s is unguaranteed — don't read
  `AudioManager.Instance` from another bootstrapped component's `Awake`; fetch lazily (LRA loop &
  SD loop do this).
- `�` encoding artifacts exist in `BoulderObstacle.cs`, `DroneProjectile.cs`, `LightningStrike.cs`
  (em-dashes) — anchor edits on clean ASCII lines.
- `AudioLibrary.asset` is hand-authored YAML; a new `AudioLibrary.cs` slot needs a matching key in the
  `.asset` (Unity fills defaults on import, but add it explicitly to be safe). The user edits it live —
  **re-read it before editing**.

---

## Exact next steps for a fresh agent
1. **Build the Main Menu Settings screen** (the task above): wire `MainMenuController.OnSettings()` to a
   sub-menu (AUDIO / VIDEO / CONTROLS) modeled on `StartMenuController`'s SETTINGS panel; back Audio with
   the existing `AudioManager` volume setters; add a `GameSettings` PlayerPrefs static (copy
   `TutorialSettings`) applied at bootstrap; Video via `Screen`/`QualitySettings`; Controls read-only for
   now. Rely on `EventSystemGuard`; don't spawn EventSystems.
2. **Assign the 8 empty AudioLibrary slots** and, if desired, give distinct clips to the placeholder-shared
   pairs listed above.
3. **Verify component wiring in the editor** (GUIDs match the `.meta`s, so they should bind — confirm no
   "missing script"): `PortalExitAudio` on every selectable player-car prefab; `JetFlames` on the
   JetFlames accessory (child visuals toggle, root stays active); `SpeedCheck` on the SpeedCheck object
   (BoxCollider, Is Trigger unchecked — the script forces it); `WindowsAudio` on the Windows prefab;
   `RoundDirectionalLightToggle` in the TrackScene; **`SDAbilityVFX` on the PlayerCar root** — add it and
   fill its `effects` list with one entry per SD (exact name "Fire SD"/"Wind SD"/"Lightning SD" + that SD's
   particle system, all parented to the car root). `SDAbilityController` plays the matching system for the
   duration of the ability and stops it (and all others) otherwise; the component is optional (null-safe)
   so cars without it just have no SD VFX.
4. **Saturation caveat for the hue randomizers:** the road `RoadMaterial` Base Map color and the
   `SimpleSkybox` Sky Tint / Ground colors must have **non-zero Saturation** or the random hue won't
   show (a 0-sat color has no hue to shift). Ground color especially defaults to near-gray.
5. **Playtest the freeze fixes:** leave the TrackScene every way (end portal, LRA abort, kill floor,
   quit-to-menu) with many boulders/drones active; confirm gamepad menu nav works in every scene
   (MainMenu, CarSelection, Store, Inventory, Start menu, Tutorial).

### Deferred
- **Player Victory sequence** still ends at the "BOTS DEFEATED" banner in a portal-less hub — a fuller win
  presentation has no design yet.
- **Input rebinding** UI (live control remapping) — start Controls as read-only; rebinding is its own task.

---

## Architecture quick-reference
- **`AudioManager`** (persistent, self-bootstrapped): scene music via `MusicForScene`
  (menu/hub/tutorial/endings/random-TrackScene pool, hub swaps to drone-ending / player-victory tracks);
  `PlayMusic`/`StopMusic`/`RefreshSceneMusic`; a 2nd **interior-music** source that crossfades over scene
  music (`PlayInteriorMusic`/`StopInteriorMusic`); `PlaySfx` (2D), `PlaySfxAt(clip, pos, Spatial3DSettings?)`
  (3D temp source), `PlaySfxFollow(clip, follow, ...)` (3D riding a transform, optional
  `bypassListenerEffects`/`volumeScale`); portal-exit flag API; static `Play*` helpers for every event;
  `musicBaseVolume`/sfx volume + setters (**these are your Audio-settings hooks**).
- **`AudioLibrary`** ScriptableObject (`Resources/AudioLibrary.asset`, script GUID `4a6b8c0d…`): every
  clip slot + `musicVolume`/`sfxVolume` + `interiorMusicCrossfadeSeconds` + `portalExit`
  volume/Spatial3DSettings + `speedBarrier*Volume`.
- **`Spatial3DSettings`**: shared tweakable 3D block (spatialBlend, volume, min/max distance, rolloff,
  doppler) used by Boulder/Lightning/DroneProjectile/Portal/Windows/BoostGate audio and the portal-exit.
- **Menus/UI**: code-built, persistent (`StartMenuController`, `InventoryView`, HUDs, `TutorialGuide`);
  `MenuState.AnyOpen` suppresses driving input while a menu is up; `EventSystemGuard` keeps exactly one
  EventSystem. `MainMenuController`/`CarSelectionController` are per-scene code-built menus.
- **Player car**: spawned per gameplay scene by `PlayerCarSwapper` (from `SelectedCarStore`) or the
  `TrackGenerator`'s delayed spawn; components read live state off `CarController` (`SpeedMph`,
  `IsTurboActive`, `IsLoopGravityCut`, `IsAirborne`, `AirborneTime`, `OnJumped`).
