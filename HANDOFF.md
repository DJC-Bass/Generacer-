# Generacer — Session Handoff

## Goal
Build out the **complete audio system** for the Generacer arcade racing game (Unity 6): music per scene, UI/menu SFX, per-vehicle engine sound, and 3D positional SFX for every gameplay event (drift, turbo/jump/landing, obstacles, portals, crafting, SD abilities). This session also finished a few **gameplay** features first (player-victory banner, two secret endings, a "return to menu" collider) before the audio work, which became the bulk of the session.

Audio design rule adopted mid-session: **only menu SFX and music are 2D; everything world-driven is 3D** (so remote players hear each other once multiplayer lands).

## Current State — code complete, a few editor tasks left
All audio is **wired in code and compiles**. What remains is Unity-editor side: assigning the last few clips and attaching one component to two prefabs. The system is a persistent `AudioManager` singleton that reads every clip from one `AudioLibrary` ScriptableObject at `Assets/Resources/AudioLibrary.asset`.

### AudioLibrary clip slots STILL EMPTY (`{fileID: 0}`) — need clips assigned
As of the last read of `Assets/Resources/AudioLibrary.asset`:
- `playerVictoryMusic` — hub theme during the "BOTS DEFEATED" win
- `carLanding` — car touches down after airtime
- `lightningWarning` — warning-column telegraph
- `sdDeactivate` — SD ability turned off
- `portalSpawn`, `portalCollision`, `portalDespawn` — (portalActiveLoop IS assigned)
- `boostGateSpawn`, `boostGateBoost` — hub Boost Gate appearing / player driving through it (added after this handoff)
- `victoryBanner` — 2D stinger as the BOTS DEFEATED banner starts fading in (added after this handoff)
- `storeDenied` — 2D buzzer when a store purchase is rejected (can't afford / at max owned) (added after this handoff)

Everything else is assigned. **Two slots currently reuse a placeholder clip** and may want distinct sounds:
- `turboCraftLoop` and `jetCraftLoop` share the same clip.
- `projectileHitEnvironment` and `projectileHitPlayer` share the same clip.

> ⚠️ **Use `.ogg` or `.wav` for all music and looping SFX, never `.mp3`.** MP3s keep encoder-padding silence that makes loops gap ("won't loop"). This bit us once (see below).

## Files created this session
Audio system:
- `Assets/Prefabs/Scripts/Audio/AudioManager.cs` — persistent singleton (self-bootstrapped `BeforeSceneLoad`); music source + 2D sfx source; per-scene music via `MusicForScene`; `PlaySfx`, `PlaySfxAt` (3D temp source), and all the static `Play*` helpers; volume setters.
- `Assets/Prefabs/Scripts/Audio/AudioLibrary.cs` — ScriptableObject holding every clip slot + `musicVolume`/`sfxVolume`.
- `Assets/Resources/AudioLibrary.asset` — the single instance the manager loads (hand-authored, GUID `4a6b8c0d…` script ref).
- `Assets/Prefabs/Scripts/Audio/Spatial3DSettings.cs` — reusable, Inspector-exposed 3D settings (`spatialBlend`, volume, min/max distance, rolloff, doppler) + `ApplyTo(source, sfxScale)`.
- `Assets/Prefabs/Scripts/Audio/PortalAudio.cs` — portal spawn/active-loop/collision/despawn (despawn keyed off `gameObject.scene.isLoaded` in `OnDestroy` to distinguish timeout vs. player-travel).
- `Assets/Prefabs/Scripts/Car Scripts/CarEngineAudio.cs` — per-vehicle engine loop, pitch/volume by Rigidbody speed.
- `Assets/Prefabs/Scripts/Obstacle Script/Boulder Scripts/BoulderAudio.cs` — boulder spawn one-shot, looping "on fire" flight, impact one-shot.
- `Assets/Prefabs/Scripts/MainMenuReturnTrigger.cs` — collider that tears down the run and loads the Main Menu (earlier in session).

Each new `.cs` has a hand-written `.meta` with a fixed GUID.

## Files modified this session (scripts)
- `Audio/AudioManager.cs`, `Audio/AudioLibrary.cs` — grew continuously as slots/helpers were added.
- `Car Scripts/CarController.cs` — drift-screech loop (raw-steer 1:1, speed-scaled, grounded-only, doppler-off, smoothing pass), turbo/jump one-shots (now 3D positional), car-landing on airborne→grounded edge, `ShortenSuspensionRayForPopUp()`.
- `Car Scripts/CarEngineAudio.cs` — default `spatialBlend` 0 → 1 (engines are 3D).
- `Inventory/SDAbilityController.cs` — SD activate/active-loop/deactivate audio (loop on a car-following `SDAbilityLoopAudio` object); also `NotifySDAbilityUsed` from earlier flawless-ending work.
- `Inventory/StartMenuController.cs`, `UI/MainMenuController.cs`, `UI/CarSelectionController.cs`, `UI/MenuNavigation.cs`, `Hub World/StoreController.cs` — menu move/select/back + store SFX; `MenuNavigation.PlayMoveSfxOnSelectionChange`; `CarSelectionController.MakeInert` now also stops AudioSources.
- `Hub World/UpgradeRampController.cs` — turbo/jet craft loop (one source, clip swapped per bar) + craft-complete one-shots; turbo loop restarts per craft.
- `DroneAI/DroneCar.cs`, `DroneAI/DroneProjectile.cs` — drone shoot at muzzle; projectile env/player hit; `DroneProjectile.audio3D` (Spatial3DSettings); suspension-shorten on player hit.
- `Obstacle Script/Lightning Scripts/LightningStrike.cs`, `LightningSpawner.cs` — warning/strike audio; `LightningSpawner.lightningAudio` (Spatial3DSettings) propagated to each strike; suspension-shorten on player hit.
- `GameLoopScripts/GameLoopManager.cs`, `GameLoopScripts/HubSceneController.cs`, `ReturnPortalTriggerr.cs`, `PortalTrigger.cs`, `Inventory/GameplayHud.cs` — earlier gameplay: player-victory (`PlayerWinActive`), flawless `GeneracersEnding` (`UsedAnySDThisRun`/`SpecialEndingEarned`), `ClipperEnding` (portal during Drone ending), plus drone-ending & player-victory hub music (`RefreshSceneMusic`).

## Prefabs / assets edited by hand-writing YAML (verify these bound in the editor)
- `Car Models/DroneCar/DroneCar.prefab`, `Car Models/Challenger Cars/ChallengerCar.prefab` — added **CarEngineAudio** (3D, spatialBlend 1).
- `Car Models/S-Sen7[Black_White].prefab`, `Car Models/Deora II Test Car [Programmed].prefab` — flipped CarEngineAudio `spatialBlend` 0 → 1.
- `Obstacles/LavaBoulder.prefab` — added **BoulderAudio**.
- `Environment Model/Portal.prefab`, `ReturnPortal.prefab`, `MainMenuPortal.prefab` — added **PortalAudio** (on the trigger-collider child).
- `ProjectSettings/EditorBuildSettings.asset` — added `GeneracersEnding` and `ClipperEnding` scenes.

## Bugs hit & fixed / gotchas (the "failed attempts")
1. **MainMenuTheme wouldn't loop** — the assigned clip was an **MP3** (encoder padding → silent gap on the loop seam). Not a code bug. Fix: use OGG/WAV. Applies to every loop/music slot.
2. **Drift pitch descended-then-ascended while holding full lock** — the 3D drift AudioSource had **`dopplerLevel` at its default (1)**, so the car's motion relative to the listener bent the pitch. Fix: `dopplerLevel = 0` (all my other 3D sources already did this; the drift one was missed).
3. **Drift pitch sagged when moving the stick** — it read `smoothedSteer` (lagged average). Fix: read **raw `steerInput`**.
4. **Making engine/drift 3D didn't affect the player cars at first** — changing a C# public-field *default* does **not** update already-serialized prefab instances. Had to hand-edit `spatialBlend: 1` into S-Sen7 and Deora II prefabs.
5. **Bootstrap ordering** — `AudioManager` and the `PlayerSystems` bootstrap are separate `RuntimeInitializeOnLoadMethod`s with **no guaranteed order**; don't read `AudioManager.Instance` from another bootstrapped component's `Awake`. SD loop source is created **lazily** on first activation for this reason.
6. **`�` encoding artifact** exists in `BoulderObstacle.cs`, `DroneProjectile.cs`, `LightningStrike.cs` (em-dashes). Anchor any future edits on clean ASCII lines.
7. Drift smoothing was **removed for 1:1 then re-added** as a tunable (`driftScreechResponsiveness`) once the doppler fix made 1:1 usable — net result: it's back and tunable.
8. Early in the session, edits occasionally **reverted between turns** — re-read a file before editing it.

## Exact next steps
1. **Assign the 7 empty AudioLibrary slots** (import OGG/WAV first): `playerVictoryMusic`, `carLanding`, `lightningWarning`, `sdDeactivate`, `portalSpawn`, `portalCollision`, `portalDespawn`. Open `Assets/Resources/AudioLibrary.asset` in the Inspector.
2. **Give distinct clips** to `turboCraftLoop` vs `jetCraftLoop`, and `projectileHitEnvironment` vs `projectileHitPlayer` (currently each pair shares one placeholder), if you want them to differ.
3. **Add `CarEngineAudio`** to the **D404** and **Clipper** player-car prefabs (they don't have it yet — only S-Sen7, Deora II, DroneCar, ChallengerCar do). It now defaults to 3D, so just Add Component + assign that car's engine clip.
4. **Verify hand-edited prefabs** show their component (not "missing script"): LavaBoulder→BoulderAudio; Portal/ReturnPortal/MainMenuPortal→PortalAudio; DroneCar/ChallengerCar→CarEngineAudio. GUIDs match the `.meta`s, so they should bind.
5. **Check the HubWorld/TrackScene placed fallback cars** are 3D (prefab-inherited spatialBlend) — only matters when play-testing without picking a car in Car Selection.
6. **Playtest each category** in the actual scenes: menu/store nav, per-car engine + drift, turbo/jump/landing, lightning/boulder/drone-projectile 3D, portals, upgrade-ramp crafting, SD activate/loop/deactivate, and the per-scene music (incl. drone-ending & victory hub swaps, random TrackScene pool).

### Deferred (non-audio)
- The **Player Victory sequence** still ends at the "BOTS DEFEATED" banner fading in a portal-less hub — a fuller win presentation was flagged earlier as "next thing to define" and has no design yet.

## Architecture quick-reference
- **`AudioManager`** (persistent): `PlayMusic/StopMusic/RefreshSceneMusic`, per-scene `MusicForScene` (menu/hub/endings/random-TrackScene, with hub swapping to drone-ending / player-victory tracks); `PlaySfx` (2D), `PlaySfxAt(clip, pos, Spatial3DSettings?)` (3D temp source); static `Play*` helpers for every event; `SfxVolume`/`MusicVolume` + setters (for a future Audio menu).
- **`Spatial3DSettings`** is the shared, tweakable 3D block used by BoulderAudio, LightningSpawner→LightningStrike, DroneProjectile, PortalAudio (and mirrored by CarEngineAudio / CarController's own fields).
- **Loops** are managed by their owning component (engine, boulder-fly, portal-active, drift, SD-active, craft) — each its own AudioSource with `dopplerLevel = 0`.
