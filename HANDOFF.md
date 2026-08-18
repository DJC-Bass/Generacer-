## NEXT TASK (this is why a fresh agent is here)

**THE SUPPORT SHIP IS FEATURE-COMPLETE (2026-08-16).** Summoning, the hub pilot station, camera
hand-off, flight and aim, destruction, replication, Star Fox 64-style semi-auto fire on **A**, and what
every round does on hit — all built and compiling. **Do not rebuild any of it**; the detail is in the
"SUPPORT SHIP" section further down and the message index just below.

What remains is **editor wiring, not code** — see "⚠️ OUTSTANDING EDITOR WIRING". The load-bearing ones
for the Support Ship: the `SupportShip` layer's collision matrix, `trackSkybox` and `laserPrefab`
assignments, a collider on `SupportShipLasers`, and the ship child on the five car prefabs that lack it.

Nothing else is queued. Ask the user what they want next.

> ⚠️ **The multiplayer sections below are the HISTORICAL design record.** Multiplayer is **BUILT** —
> Phases 1–6 are code-complete (lobby/sessions, shared additive world, networked cars, server-authoritative
> scoring, host-simulated AI, UX polish) plus voice (now **Vivox**), the grappling hook, shields, drone
> planes, the Support Ship and more. Read **"MULTIPLAYER MESSAGE INDEX"** just below for the live wire
> protocol, then the dated bullets in the roadmap for how each piece actually ended up. Do NOT rebuild any
> of it.

### SUPPORT SHIP — what was built (2026-08-16)

The user's mental model: **a rail shooter** — the hub teammate is the gunner, and the racer in the
TrackScene *is the rail*. Four new files, plus the settled answers to the six questions that were open:

| Question | The user's answer |
|---|---|
| Who gets the controls | A **PilotControlCenter** prefab in the hub (box-collider trigger, like the store). Driving in lists teammates with active ships; pick one and you fly it. Nobody in the hub → the ship just holds its current offset. |
| What the gunner sees/presses | Cuts to a chase camera on the ship, set up like the MainCamera (offset + Position/Pitch/Roll smooth times). Left stick slides the ship in a horizontal/vertical box. Nose tilts into the turn. |
| Activation | The racer presses **L3 + Y together** to summon/dismiss, if they hold a "Support Ship". |
| Lifetime | Lives as long as its car. **Any collision downs it** — 2 s ragdoll then despawn, exactly like a DronePlane. |
| Cost of fire | Unlimited. **Guns built 2026-08-16** — see the firing notes below. |
| Visibility | Enemies see it and can shoot it down. **LavaBoulders must not target it** — they don't: `BoulderObstacle` targets via `MultiplayerWorld.PickStickyTarget`, which only ever returns player cars. |

Plus four decisions the user made when asked:
- **Destruction consumes one "Support Ship".** Summoning and dismissing are free. That's the whole point
  of dismissing it when nobody is flying it.
- **The pilot's hub car is input-locked** (`CarController.InputSuppressed` + `MenuState.AnyOpen`) — NOT
  frozen — and they must stay on the pad. **Revised 2026-08-16:** control is now held until one of
  exactly four things happens: **SELECT** (manual hand-back), the ship is destroyed, its owner dismisses
  it, or the pilot's car is **shoved off the pad by an external force**. That last one is why the car
  can't be frozen: a pinned Rigidbody is unpushable, which would have made a rival knocking a distracted
  pilot off the pad impossible.
- **What can down the ship is the collision MATRIX** on a new `SupportShip` layer, not code.
- **The pilot's AudioListener moves to the ship**, so they hear the racer's world, not the hub.

**`SupportShip.cs`** (Car Scripts) — the flyer. Deliberately NOT a physics flyer like `DronePlane`: it is
a FOLLOWER, and the follow model is *the chase camera's*. The lazy-Susan per-axis lag (yaw/pitch/roll) is
duplicated from `CameraFollow` — keep the two in sync by hand if the camera feel is ever re-tuned. This is
not arbitrary: the ship's authored position on `Melody.prefab` is `(0, 2.5, -7)`, which **is**
`CameraFollow.offset`, so an unpiloted ship sits exactly at the driver's viewpoint. ⚠️ That also means the
racer effectively **cannot see their own ship** — move the child up/forward on the prefab if you want it
visible from the cockpit. `PilotOffset` slides it inside that frame; tilt is derived from the ship's ACTUAL
travel through the car's frame (not the stick), so it banks both when the pilot slides it and when the car
corners hard enough to swing it.

- **Death detection is by TRIGGER, not collision**, for two reasons that are easy to get wrong: a kinematic
  body driven by transform writes raises **no collision events against static scenery** (so it would sail
  straight through the track), and triggers still honour the **collision matrix** — which is what keeps
  "what can down the ship" a Project Settings decision. The owner's own car is excluded in code
  (`BelongsToCar`), because the matrix cannot tell it apart from an enemy's — both are on `Player`.
- `detectCrashes` gates whether *this copy* may call a crash. True on the owner's machine and on the host;
  **false on every other viewer**, whose copy is derived from an interpolated puppet and would invent hits.

**`SupportShipAbility.cs`** (Inventory) — the racer's half, bootstrapped on `PlayerSystems`. The child on
the car prefab is only ever a **TEMPLATE**: summoning CLONES it and cuts the clone loose, so the wreck can
be destroyed without permanently stripping the prefab of its ship, and so it can trail with lag (a rigidly
parented child is welded to the car's rotation). The clone is built from the template's **world** pose, so
nesting depth on the prefab doesn't matter.

⚠️ **The L3+Y chord required two other systems to stand down**: `ShieldAbility` and `GrappleHook`'s
break-free both fire on bare L3, so both now ignore an L3 press made while Y is held. If you add another
L3 binding, it needs the same exclusion.

**`SupportShipReplicator.cs`** (Multiplayer) — see the message index below. This is the **first entity
whose input and whose subject live on different machines**, so authority splits three ways along the line
of who actually knows the answer: the racer owns *whether the ship is out*, the **pilot** owns *where in
its box it sits* (routing that through the racer would put a full round trip between the pilot's stick and
their own screen — unflyable), and the **server** owns *whether it is still alive* (so it dies once,
everywhere, and exactly one item is deducted). Nothing about the flight is streamed; each machine glues
its own ship to its own copy of the owner's car.

**`PilotControlCenter.cs`** (Hub World) — the pad. Same trigger-menu shape as `StoreController`, and it
flies a LOCAL copy of the ship the same way `HubSpectatorTV` films a local copy of a remote racer. Claims
are **server-arbitrated, never optimistic** — two hub players can reach for the same ship in the same frame
and exactly one must get it — so the pad waits for the verdict, with a 3 s timeout because a refusal looks
identical to a slow network. **Your own ship is listed too**, which is what makes the whole feature
testable solo: summon a ship, park on the pad, fly it.

---

## MULTIPLAYER MESSAGE INDEX (live wire protocol — all custom named messages)

Everything is `CustomMessagingManager` named messages (**no NetworkObjects/NetworkVariables anywhere**).
Listen server: the host is also a player. Movement is **owner-authoritative**; game state is
**server-authoritative**. Voice does NOT appear here — it left this layer entirely when it moved to Vivox.

| Message | Owner | Direction | Payload / purpose |
|---|---|---|---|
| `GNRC_HELLO` | RemoteCarManager | client → server | name, car, team — roster join |
| `GNRC_ROSTER` | RemoteCarManager | server → all | full roster; drives puppet lifecycle |
| `GNRC_CAR` | RemoteCarManager | owner → all (host relays) | 30 Hz pos/rot/vel + **1 effect byte** |
| `GNRC_READY` | MultiplayerWorld | client → server | hub loaded, ready for rounds |
| `GNRC_ROUND_START` | MultiplayerWorld | server → all | round, seed, live, remaining — preload + freeze |
| `GNRC_ROUND_GO` | MultiplayerWorld | server → all | portal spawns, unfreeze, timers start |
| `GNRC_ROUND_END` | MultiplayerWorld | server → all | reason: 0 timeout, 1 all racers left |
| `GNRC_AREA` | MultiplayerWorld | client → server | inTrack flag |
| `GNRC_RACER_FIN` | MultiplayerWorld | server → all | an AI racer finished — first place forfeit |
| `GNRC_FINISH` | MultiplayerScoring | client → server | localFirstPlace claim |
| `GNRC_SDS` | MultiplayerScoring | client → server | distinct SD names held (team aggregate) |
| `GNRC_SD_AWARD` | MultiplayerScoring | server → one | the team-validated SD |
| `GNRC_FIRST_BONUS` | MultiplayerScoring | server → one | first-place credits (ONE winner/round) |
| `GNRC_SCORE` | MultiplayerScoring | server → all | droneWins |
| `GNRC_ENDING` | MultiplayerScoring | server → all | isDroneEnding, winningTeam |
| `GNRC_RIVALS` | MultiplayerScoring | server → all | rival pairings |
| `GNRC_RIVAL_BONUS` | MultiplayerScoring | server → one | beat-your-rival credits |
| `GNRC_NPC_SPAWN` / `_STATE` / `_DESPAWN` | NpcReplicator | server → all | host-simulated AI + obstacles |
| `GNRC_STRIKE` | NpcReplicator | server → all | lightning, event-replicated |
| `GNRC_NPC_HIT` | NpcReplicator | server → victim | projectile hit YOUR car (victim applies it) |
| `GNRC_BOUNTY` | NpcReplicator | server → one | knockoff credits |
| `GNRC_GRAPPLE` | GrappleReplicator | owner → all (host relays) | state + **anchor KIND** + ids/offsets |
| `GNRC_GRAPPLE_PULL` | GrappleReplicator | → victim (host relays) | acceleration applied on the OWNER's machine |
| `GNRC_GRAPPLE_BREAK` | GrappleReplicator | victim → all | L3: release any hook attached to me |
| `GNRC_SHIP` | SupportShipReplicator | owner → all (host relays) | ship is out / put away; 2 Hz heartbeat, Reliable on change |
| `GNRC_SHIP_AIM` | SupportShipReplicator | **pilot** → all (host relays) | 20 Hz {Vector3 offset, Vector3 aim angles} for a named owner’s ship |
| `GNRC_SHIP_PILOT` | SupportShipReplicator | client → server (request) / server → all (verdict) | claim/release the controls — server arbitrates |
| `GNRC_SHIP_DOWN` | SupportShipReplicator | any → server (report) / server → all (verdict) | the ship was destroyed; owner spends one item |
| `GNRC_SHIP_FIRE` | SupportShipReplicator | pilot → server | fire owner X’s lasers once; host spawns + NpcReplicator streams the round |
| `GNRC_SHIP_LHIT` | SupportShipReplicator | server → victim | a Support Ship round popped YOUR car — victim applies the pop-up and judges its own i-frames |

**Four patterns this codebase leans on — copy them:**
1. **Route effects to the authority.** A remote car is a kinematic puppet — pushing it locally is erased
   by its next update. `GNRC_GRAPPLE_PULL` / `GNRC_NPC_HIT` send the *effect* to the machine that owns
   the car and let IT apply the force. **The Support Ship's gun will need exactly this.**
2. **Replicate identity, not position.** `GNRC_GRAPPLE` sends *what* it's attached to, so each viewer
   derives the position from its own already-smoothed copy. Streaming a world point for a moving,
   replicated object always lags and snaps. `GNRC_SHIP_AIM` is the same idea one step further: it sends
   only the pilot's *offset*, and every machine derives the ship's world pose from its own copy of the
   racer's car.
3. **Level-triggered flags self-heal.** The `GNRC_CAR` effect byte and `GNRC_SHIP` are the owner's current
   state, not edge events, so a dropped Unreliable packet fixes itself on the next tick.
4. **Whoever supplies the input owns the result.** `GNRC_SHIP_AIM` is broadcast by the PILOT, not by the
   ship's owner — the alternative (pilot → owner → everyone) puts a full round trip between a player's
   stick and their own screen. Anything genuinely contested (who holds the controls, whether a ship
   died) still goes through the server.

**Two message-shape idioms in use.** Both are load-bearing and look like bugs if you don't know them:
- **Direction-dependent meaning.** `GNRC_GRAPPLE_PULL`, `GNRC_SHIP_PILOT` and `GNRC_SHIP_DOWN` all mean
  *"please"* when the server receives them and *"it is so"* when a client does. One name, one payload
  layout, `if (IsServer)` picks the branch.
- **Fixed-size payloads.** `GNRC_GRAPPLE` reuses its two Vector3 slots for different things per state;
  `GNRC_SHIP_PILOT`'s verdict writes a dummy trailing bool so it matches the request layout.

---

### The multiplayer design (historical — from the user, 2026-07-19)
- **Lobby screen** reached from the Main Menu's Online Multiplayer button: **host a lobby**, **join a lobby**,
  and **set the lobby's rules** — including **how many players per team**, so team size is a lobby parameter,
  never a hard-coded 3.
- **Two teams**, 3 players each by default (6 total), running the existing game loop against each other.
- **Win:** the first team to hold **3 SDs collectively across its members** wins. Note this is a *team*
  aggregate — one player holding 3, or three players holding 1 each, both win it. SDs are counted as
  **distinct SD names within a team** (same rule `CountPlayerSDs` already uses), and a team can't be awarded
  an SD it already holds; **duplicates ACROSS teams are fine** (both teams may own a Lightning SD).
- **Lose:** **two drone wins is still a game over for EVERYONE** — both teams, exactly as in single-player.
- **Individual track entry/exit (2026-07-19):** players enter the TrackScene **separately** through the hub
  portal, each on their own timing. When one player dies/finishes/leaves, the others keep racing; the track
  round ends only when **all players have left OR the round timer expires**.
- **Sticky random targeting (2026-07-19):** any entity that targets "the player" (drones, boulders, etc.),
  when several players are in the track, picks **one random player and keeps that target for its whole
  lifespan** (retarget/despawn only if the target leaves — see Phase 5).
- **Netcode must use EXTRAPOLATION** for remote cars. Cars routinely exceed **600 mph**, where snapshot
  interpolation alone visibly jitters and rubber-bands; remote cars have to look smooth to every player.

### DECISIONS MADE (2026-07-19, confirmed with the user — the 5 open questions are settled)
1. **Stack:** Netcode for GameObjects (NGO 2.x) + Unity Transport + Unity Gaming Services
   (Authentication anonymous; Lobby + Relay via the unified `com.unity.services.multiplayer`
   **Sessions API** — the standalone Lobby/Relay packages are deprecated). Packages are INSTALLED
   (see Phase 0 status).
2. **Topology:** **listen server** — one player hosts, traffic over Relay (no port forwarding, no dedicated
   server). Host quitting ends the session; **host migration is explicitly out of scope for v1**.
3. **Movement authority:** **owner-authoritative cars** (each client simulates its own `CarController`
   physics) synced by a **custom extrapolating transform sync**; the **server owns all game state**
   (round phase/timers, seeds, SD awards, drone wins, endings, AI). Cheating accepted for v1.
4. **World:** **one shared world** — both teams race the SAME track instance. Implemented as hub + track
   loaded **additively in one session** with the generated track at a large world offset (see Phase 2).
5. **Drones/obstacles are server-simulated** NetworkObjects (forced by the scoring rules).

### MULTIPLAYER ROADMAP (phases in order; Phase 0 = DONE)

**Phase 0 — Foundation — ✅ DONE 2026-07-19**
1. ✅ Compile check of session 3's uncompiled code: `dotnet build Assembly-CSharp.csproj` → **0 errors**
   (32 warnings, all pre-existing deprecations like `enableWordWrapping`/`FindObjectOfType` — harmless).
2. ✅ Confirmed the WheelCollider sample kit was dead (no scene/prefab/asset referenced its GUIDs; only its
   own editor tool referenced its classes) and **DELETED `Assets/vehicle/` (all 5 scripts) +
   `Assets/Editor/vehicleManager.cs`** (+ metas + now-empty `Assets/Editor/`). Re-verified: 0 errors.
   The `FindWithTag("Player")` cleanup list is now **9 call sites across 7 files** (see below).
3. ✅ Added to `Packages/manifest.json` (versions = registry latest, editor is 6000.4.11f1):
   `com.unity.netcode.gameobjects` 2.13.0, `com.unity.services.authentication` 3.7.3,
   `com.unity.services.multiplayer` 2.2.4, `com.unity.multiplayer.playmode` 2.0.2,
   `com.unity.multiplayer.tools` 2.2.9 (`com.unity.transport` arrives as an NGO dependency).
   **NOTE (2026-07-19):** the standalone `com.unity.services.lobby` / `com.unity.services.relay` packages
   are DEPRECATED — Lobby + Relay now ship inside the unified **Multiplayer Services SDK**
   (`com.unity.services.multiplayer`) and its **Sessions API**; the manifest was corrected to use it.
   Do NOT re-add the standalone packages.
   **NOT yet done (needs the editor/dashboard, do first next session):** open the editor so Package Manager
   resolves these, then **link the project to a UGS project ID** (Project Settings → Services) and enable
   Authentication/Lobby/Relay in the Unity Dashboard.

**Phase 1 — Lobby & session plumbing — ✅ CODE-COMPLETE 2026-07-20 (compiles 0 errors; needs
editor verification + the UGS project link before it can actually run)**
Two new scripts in `Assets/Prefabs/Scripts/Multiplayer/` (hand-written `.meta`s, fixed GUIDs):
- **`NetworkSessionManager.cs`** — persistent singleton (`EnsureExists()`, created on demand by the lobby
  UI; no scene setup) wrapping the **Sessions API**. Anonymous UGS auth (`EnsureServicesAsync`; local name
  derived from the player id, "PLAYER-XXXX"); creates the persistent `NetworkManager`+`UnityTransport` in
  code with **`ConnectionApproval = true` and `EnableSceneManagement = false`** (Phase 2 uses additive
  areas, never NGO scene sync); `HostSessionAsync` / `JoinByCodeAsync` / `JoinByIdAsync` /
  `QueryPublicSessionsAsync`. **The Sessions API starts NGO itself** (`WithRelayNetwork()` → Relay
  allocation → its `GameObjectsNetcodeNetworkHandler` calls `StartHost`/`StartClient` — verified in the
  package source), so by the room screen every member is transport-connected. **Team size is a session
  property** (`teamSize`, public + `PropertyIndex.Number1` so the browser shows it; `MaxPlayers = 2×`,
  clamp 1–4, default 3, never hard-coded). Player metadata = member-visible player properties
  (`name`/`team`/`car`/`ready`) via `SetLocalPlayerPropertyAsync` → `SaveCurrentPlayerDataAsync`.
  NGO **connection approval** re-caps at `Session.MaxPlayers` and refuses joins once `started` is set.
  Teardown: **host leave = `AsHost().DeleteAsync()`** (clients get a clean "HOST CLOSED THE LOBBY"),
  client leave = `LeaveAsync()`; session `RemovedFromSession`/`Deleted`/`Disconnected` (and the NGO
  local-client disconnect) all funnel into one idempotent `EndLocally(reason)` → `NetworkManager.Shutdown`
  + `SessionEnded(reason)` event (deliberate leaves suppressed via a `leaving` flag).
  `StartGameAsync` (host): locks the session + sets `started="1"` — **Phase 2's hook**; Phase 1 stops there.
  Helpers the UI/Phase 4 reuse: `TeamOf`/`IsReady`/`PropertyOf`, `CountTeam`, `TrySwitchTeamAsync`,
  `AutoAssignTeamAsync` (joiners land on the smaller team), `ReadyToStart(out reason)`.
- **`MultiplayerLobbyUI.cs`** — the code-built lobby flow on the MainMenu canvas (same style/colours,
  reuses `SettingsUI` widgets + `MenuNavigation`). Screens: ROOT (HOST LOBBY / JOIN WITH CODE / LOBBY
  BROWSER / BACK) → HOST (PLAYERS-PER-TEAM cycler + PUBLIC/PRIVATE cycler → CREATE) → ROOM, plus
  JOIN-BY-CODE (code-built `TMP_InputField` — **keyboard entry; the browser is the gamepad path**) and
  BROWSER (query + join-by-id rows "NAME 3/6 (3 PER TEAM)"). ROOM: lobby name, **join code**, two team
  rosters (host marker, local ▸, car, ready ✓, "- OPEN SLOT -" fillers), SWITCH TEAM (full-team denial
  buzz), CAR cycler (writes `SelectedCarStore` + the `car` player property), READY toggle, host-only
  START GAME (interactable only when `ReadyToStart`), LEAVE. Async ops gate input on a `busy` flag and
  report through a shared status line (incl. a "IS THE PROJECT LINKED?" hint on init failure). B/Esc
  backs out per screen; in ROOM it leaves the lobby.
- **`MainMenuController` wiring:** new `multiplayerCars` Inspector field (**fill with the same name+prefab
  entries as the CarSelection scene's list** — the lobby can't read another scene's Inspector); builds
  `MultiplayerLobbyUI` in `BuildUI`; ONLINE MULTIPLAYER hides the main column and opens it; menu
  `Update`/`LateUpdate` defer entirely to the lobby while `lobbyUI.IsOpen`; exit restores + refocuses.
- **Still to do to RUN it (needs the editor / dashboard):** (1) link the project to a UGS project ID
  (Project Settings → Services) + enable Authentication/Lobby/Relay in the dashboard; (2) fill
  `multiplayerCars` on the MainMenu scene's controller; (3) verify in-editor and then two-instance test
  via Multiplayer Play Mode (host in one, join by code in the other). Session-side caveat: NGO
  approval-denial UX for a full/locked lobby is minimal (client just fails to connect) — acceptable
  because lobby slots already gate joins service-side.

**Phase 2 — Shared world & synced randomness — ✅ CODE-COMPLETE 2026-07-20 (compiles 0 errors; needs
the same runtime checklist as Phase 1 plus a two-instance world test). Single-player is UNTOUCHED —
every change is gated on `MultiplayerWorld.IsMultiplayerGame`.**
- **`Multiplayer/MultiplayerWorld.cs` (new, the Phase 2 core)** — persistent controller launched by
  `NetworkSessionManager.Update()` when Phase 1's `started` flag lands (host AND joiners; that was the
  Phase 1→2 hook). The multiplayer world = **HubWorld loaded single + TrackScene loaded ADDITIVELY each
  round at `TrackAreaOffset` (0,0,−35 km)** — inside the float envelope single-player tracks already
  occupy (~30 km ⇒ mm-scale). **Deliberately prefab-free** (NGO custom named messages `GNRC_*`, not
  NetworkObjects — no editor setup, no GlobalObjectIdHash headaches; Phase 4 migrates game state to
  NetworkVariables once Phase 3's player prefab exists).
  - **Host round loop** (single source of rounds AND randomness): waits for every member's READY (hub
    loaded, 25s cap) → countdown (GameLoopManager's own min/max fields) → rolls the round seed →
    ROUND_START{round,seed} → round runs `roundDuration`, **ending early once ≥1 player entered the
    track and every entrant has left** (the user's "all players leave" rule; disconnects are removed
    from the in-track set so they can't hold a round open) → ROUND_END → `postRoundDelay` → repeat.
  - **Every client keeps a real `GameLoopManager` in REMOTE-DRIVEN (puppet) mode** (`RemoteDriven`
    static; set before creation, whose Awake doubles as the menu→hub transition):
    `RemoteBeginRound`/`RemoteEndRound` fire the SAME events the local loop would, so
    **HubSceneController's portal spawn/despawn, RoundObstacleSelector, RoundDirectionalLightToggle and
    TrackGenerator's seed pull all work unmodified**. Local transitions/scoring suppressed
    (`Update` ticks `RoundTimeRemaining` for display only; `NotifyEnteredTrack`/`NotifyReturnedToHub`
    no-op; `GetNextTrackSeed` returns the server's `RemoteTrackSeed`).
  - **Additive track load handling:** before the scene's Starts run — roots shoved by `TrackAreaOffset`,
    the scene's OWN cameras/AudioListeners disabled (the hub rig follows the car everywhere), the
    authored test car destroyed, the scene's Speedometer HUD area-gated. After its Starts — directional
    lights recorded AS TOGGLED (so restoring them preserves blackout rounds).
  - **Teleports** (`EnterTrackLocally` via the hub portal / `ReturnToHubLocally` for every way out):
    zero velocities → `SetPositionAndRotation` → the generator's own two-FixedUpdate spawn-boost dance;
    return pose = the hub car pose captured at hub load. **Per-area presentation is local**:
    `SetActiveScene` follows the local player (RenderSettings skybox/fog + `AudioManager`'s scene-keyed
    music), per-area directional-light switching, camera rigs snapped so they don't swoosh 35 km.
  - **Teardown** (`TeardownToMenu`): session death mid-game (host quit, kicked, connection lost — wired
    from `NetworkSessionManager.EndLocally`) or deliberate quit → stop loop, unregister handlers, clear
    puppet statics, `GameLoopManager.EndRun()`, inventory reset, single-load MainMenu (which also dumps
    the additive track).
- **The ONE seed drives everything** (`MultiplayerWorld.DeriveRandom(stream)` — FNV-1a stream name ⊕
  round seed, so streams are independent but identical on every client): track geometry
  (`Random.InitState(GetNextTrackSeed())`, unchanged code path), **road hue** (was an UNSEEDED
  `System.Random` — now `DeriveRandom("roadhue")` in multiplayer), **skybox hues**
  (`DeriveRandom("skybox")`, recolors on `activeSceneChanged` so area teleports restore the same sky),
  **blackout roll** (`DeriveRandom("blackout")`), **obstacle-spawner subset**
  (`DeriveRandom("obstacles")` Fisher–Yates; spawner *timing* stays local until Phase 5).
- **Edited for multiplayer branches** (all no-ops in single-player): `TrackGenerator` (static `Current`,
  `CarSpawnPosition/Rotation` + `ApplySpawnBoostTo` exposed, generates at the area offset, end portal
  parented into the track scene, **auto car placement skipped** — the portal teleport does it),
  `PortalTrigger` (teleport + stays usable for re-entry), `ReturnPortalTrigger` (awards locally for now
  → teleport; no special-ending routing), `KillFloor` (inventory wipe → teleport; **trigger re-arms** for
  the next round), `LraAbortController`, `MainMenuReturnTrigger` + `StartMenuController.OnQuit` (leave
  session — host leave deletes it for everyone — then world teardown), `PlayerCarSwapper` + `AudioManager`
  (ignore ADDITIVE loads — the swapper would have grabbed the LIVE hub car via `FindWithTag` and
  destroyed it).
- **Post-playtest fixes (2026-07-20, first two-instance run):** (1) **LRA dead in the track** —
  `LraAbortController.IsInTrack()` gated on `CurrentPhase == InTrack`, which never happens in
  multiplayer (per-player presence isn't a global phase); now asks `MultiplayerWorld.InTrackLocally`
  (new public accessor). (2) **Meteors bleeding into the hub** — the authored BoulderSpawnPlane spans
  the whole track corridor, and shifted −35 km its far edge reaches back over the hub; boulders also
  kept spawning while the local player was hub-side. Fixes: spawners only run while the LOCAL player is
  in the track (`BoulderSpawner` + `LightningSpawner` gates, cadence kept fresh for re-entry), boulder
  spawns inside a **4 km hub-exclusion radius** around the world origin are skipped, and
  `ReturnToHubLocally` destroys this client's live `BoulderObstacle`s so nothing follows you home
  (safe: boulders are per-client until Phase 5).
- **Known Phase 2 boundaries (by design):** no round SCORING in multiplayer yet (Phase 4 — rounds cycle
  but nothing counts wins/drone-wins/endings); end-portal credits/SD awards still land in each player's
  LOCAL inventory (Phase 4 makes them server-validated + team-aggregated); remote players are INVISIBLE
  (Phase 3 networked cars); obstacle spawn timing diverges between clients (Phase 5). Editor checks
  worth doing on the first two-instance run: teleport feel + camera snap, per-area light/sky/music
  swapping, the hub portal surviving a full round cycle, kill-floor → re-enter same round.

**Phase 3 — Networked cars at 600 mph — ✅ CODE-COMPLETE 2026-07-20 (compiles 0 errors).
DELIBERATE DEVIATION from the original plan: NO NetworkObject player prefab.** Each client simulates
only its OWN plain local car (the one the game already spawns), and remote players are **local
PUPPETS** — stripped visual clones driven by an extrapolated state stream over the existing custom-
message layer. Why: every selectable car is its own prefab (NetworkObject + NetworkPrefabs registration
on each = editor asset work), `PlayerCarSwapper` destroys/respawns cars per scene (ownership juggling),
and hand-authoring a NetworkObject prefab risks GlobalObjectIdHash breakage. The puppet approach needs
ZERO editor setup and structurally satisfies the "gate on IsOwner" goal: **CarController, cameras,
swivel/`ManualYawInput`, SD input and engine audio exist ONLY on the owner's machine — there is no
remote instance of any of them to gate** (the handoff's local-only-camera warning is thereby resolved).
Cost: no NetworkVariables on players — Phase 4 stays on the message layer (fine at 6 players) or
introduces its own object then.
- **`Multiplayer/PlayerRegistry.cs` (new)** — the single "where is the player?" answer. `LocalCar`
  (cached; explicit `SetLocalCar` from `PlayerCarSwapper` at spawn; old `FindWithTag("Player")` as the
  resolution fallback so single-player is behaviour-identical), `Remotes` (clientId/name/car/team/
  puppet — **Phase 5's targeting pool**), and the **car catalog** (name → prefab, fed from
  `MainMenuController.multiplayerCars` in `BuildUI`; asset refs outlive the menu scene). All 9
  `FindWithTag` call sites replaced (`DroneCar`×2, `SDAbilityController`×2, `HubSpawnBoost`,
  `SpeedCheck`, `BoulderObstacle`, `TrackGenerator`, + `MultiplayerWorld`'s 4 internal uses);
  `PlayerCarSwapper` keeps its find-the-PLACED-car lookup (different semantics — it's hunting the
  scene's authored car to replace) and now registers what it spawns.
- **`Multiplayer/RemoteCarManager.cs` (new, added to the MultiplayerWorld object at session begin)** —
  roster + state stream + puppet lifecycle. HELLO (client→host once the local car exists: name/car/
  team) → host broadcasts the full ROSTER on every hello AND on disconnects; receivers diff it
  (create/destroy puppets). CAR state @ **30 Hz, `NetworkDelivery.Unreliable`**, `{clientId, ushort
  seq, pos, rot, linVel, angVel}` (~62 B), host applies + relays to the other clients. **Puppets:**
  instantiated from the catalog **under an INACTIVE staging root** (component Awakes never run), then
  stripped via `DestroyImmediate` — all MonoBehaviours, AudioListeners/Sources, Cameras, Colliders,
  Rigidbodies; trails muted, particles stopped + `playOnAwake` off — then released as a pure visual.
  **Not tagged "Player", no colliders**: can't be grabbed by tag lookups, can't fire portals/kill
  floors, can't physically shove anyone (deliberate: 600 mph contact through 100 ms latency isn't a v1
  fight). Parked at (0,−10000,0) until their first state lands. Empty catalog ⇒ placeholder cube +
  warning.
- **`Multiplayer/RemoteCarPuppet.cs` (new — THE extrapolating sync, the phase's hard requirement)** —
  dead reckoning, never snapshot interpolation: each frame projects the last state forward by its age
  (pos + v·t; angular velocity integrated into the rotation) and eases the visible pose toward the
  projection with an exponential blend (`τ_pos` 0.12 s, `τ_rot` 0.10 s). Three load-bearing details:
  (1) the projection **leads by the blend's own τ** — a pure exponential chase sits τ·v behind a moving
  target (~32 m at 600 mph), leading cancels that to first order; (2) **extrapolation age is capped**
  (0.5 s) so a stalled sender doesn't sail a ghost kilometres off the track; (3) **100 m snap
  threshold** for teleport-sized discontinuities (portal = 35 km between two packets) + ushort
  **serial-number sequence** dropping stale packets from the unreliable stream.
- **Post-playtest fix (2026-07-20):** puppets showed EVERY authored-active conditional accessory
  (Jet flames, SD ability effects) because the scripts that normally hide them at Awake/OnEnable are
  exactly what the strip removes (and their Awakes deliberately never run). `StripPuppet` now calls
  `HideConditionalVisuals` FIRST — while component data is still readable — replicating each script's
  at-rest state: JetFlames' flame list (assigned array, else its direct children) SetActive(false),
  and each `SDAbilityVFX.effects` particle-system GameObject deactivated (stopping the system alone
  isn't enough — the owner-side `Hide()` deactivates the OBJECT). Any future conditional car accessory
  needs a line there too.
- **Remote effect replication (2026-07-21):** puppets now MIRROR the owner's turbo tire trails, jet-jump
  flame flare, and per-SD activation burst (previously each player saw only their own). New
  `Multiplayer/RemoteCarEffects.cs` on every player puppet drives all three off **one extra byte on the
  existing 30 Hz CAR stream** — bit 0 turbo-trail-emitting, bit 1 flame-flaring, bits 2-3 a 2-bit SD
  index over a canonical `{none, Fire, Wind, Lightning}` order (`RemoteCarEffects.Encode`). LEVEL-triggered
  (owner's *current* state, not edge events), so a dropped Unreliable packet self-heals on the next of
  ~30/s. Payload is now 63 B (was 62) — far under the 1264 Unreliable cap; `FastBufferWriter(80)` unchanged.
  Owner side: `RemoteCarManager.ComputeEffectFlags` reads `CarController.TurboTrailsActive` (new — set in
  `UpdateTurboTrails`, true while a rear tire actually lays a mark), `JetFlames.IsFlaring` (new), and
  `SDAbilityController.Instance.ActiveSD`, caching the component lookups until the local car instance
  changes. Puppet side: `RemoteCarManager.CapturePlayerVisuals` records the flame GameObjects + each SD's
  particle system from the INSTANCE **before** the strip (same resolution as `HideConditionalVisuals`),
  and `Configure` rebuilds the two rear-tire `TrailRenderer`s the owner's CarController makes at runtime
  (they aren't in the prefab) using tuning + rear-wheel offsets read off the prefab's CarController.
  `ApplyState` gained a `byte effectFlags` param → `RemoteCarEffects.ApplyFlags`; **trails are Cleared on
  every snap** (teleport/area-change) AND on the flame rising edge (a jump — matches the owner breaking
  its trail when airborne) so no ribbon streaks across the gap. Flame visuals are Cone MESHES so
  SetActive alone shows them (mirrors `JetFlames.SetFlames`); SD systems get SetActive+`Play(true)`
  (mirrors `SDAbilityVFX`). NPC puppets pass `effectFlags: 0` (no `RemoteCarEffects` component). Any new
  replicated car effect: add a bit in `RemoteCarEffects` + a source read in `ComputeEffectFlags`.
- **Phase 3 boundaries:** puppet wheels don't spin / no engine audio / no nameplates (Phase 6 gives
  remotes a speed-driven audio+visual pass); drones still chase the LOCAL player per client (Phase 5
  moves AI server-side with the sticky random targeting from the registry pool); no car-to-car
  collision (deliberate, above). **Two-instance checks:** both players visible in hub + track, a
  600 mph fly-by staying smooth under Network Simulator latency (100–200 ms), portal teleport = clean
  snap (no cross-map streak), leaver's puppet despawning mid-round.

**Phase 4 — Server-authoritative game loop & team scoring — ✅ CODE-COMPLETE 2026-07-20 (compiles 0
errors). Implemented on the custom-message layer per the Phase 3 deviation (no NetworkBehaviour/
NetworkVariables — same authority model, zero editor setup).**
- **`Multiplayer/MultiplayerScoring.cs` (new, on the MultiplayerWorld object)** — the server's scoring
  brain. The single-player rule logic survives with EXACTLY the two planned changes:
  `playerFirstPlaceThisRound` → **which team claimed the round** (the FIRST player to reach the end
  portal with a first-place verdict claims it for their team), and `CountPlayerSDs()` →
  **distinct SD names across the claiming team's members** (`TeamDistinctSds`).
- **Per-player SD ownership:** `PlayerInventory` stays each player's full LOCAL inventory
  (credits/turbos local — completion credits still awarded client-side); only SD ownership is
  server-truth. Every client reports its **distinct held SD set** (same "name ends ' SD'" rule) via
  `GNRC_SDS` on `PlayerInventory.OnChanged` (debounced, only when the set actually changed — so a
  kill-floor wipe arrives as an empty report and shrinks the team aggregate). The server ALSO books
  its own awards instantly so the round-end win check never races the client echo.
- **First-place + SD award flow:** `ReturnPortalTrigger`'s MP branch → `AwardCompletionCredits(
  grantSdLocally: false)` (refactored to return the first-place verdict; the LOCAL SD grant is
  multiplayer-disabled) → `GNRC_FINISH{firstPlace}` → server: first valid claim wins the round for
  that team → server picks a random SD **the team doesn't collectively hold** (duplicates across teams
  fine; team holds all three ⇒ no award) → `GNRC_SD_AWARD` to the finisher → lands in their local
  inventory. *Interim semantics until Phase 5:* "before any AI" is the finisher's OWN sim's
  `AnyRacerFinishedAhead` (drones are still per-client), and claims are client-trusted (cheating
  accepted per the movement-authority decision).
- **First-place BONUS now server-authoritative too (2026-07-21):** previously the first-place credit
  bonus was self-awarded by every client in `AwardCompletionCredits` on its LOCAL `!AnyRacerFinishedAhead`
  (beat the drones) — so several players who each beat the drones each banked it. Now exactly ONE player
  per round wins the first-place bonus + SD: the single claimant (`claimedByClient` — first finish the
  server processes with `serverFirstPlace` while `claimedTeam == 0`, i.e. before the drones AND every
  other player). `AwardCompletionCredits` gates the local first-place bonus + `NotifyPlayerFirstPlace` on
  `!MultiplayerWorld.IsMultiplayerGame` (every finisher still banks the flat COMPLETION credit locally),
  and `HandleFinish` sends the claimant `GNRC_FIRST_BONUS{credits}` (server's `firstPlaceBonusCredits`,
  so all machines agree) right when it claims — unconditional for the claimant, independent of the SD
  award (which can be skipped when the team already holds all three). Non-winning finishers get
  completion + any rival bonus only. Single-player path unchanged. The host-instant-vs-client-latency
  claim ordering is unchanged (accepted) — it now governs the bonus and the SD identically.
- **HUB round clock (2026-07-21):** `GameLoopScripts/HubRoundClock.cs` — a hub-world digital screen that
  shows the live round's TIME REMAINING as MM:SS:SSS (minutes : seconds : milliseconds), off
  `GameLoopManager.RoundTimeRemaining` (holds at full `roundDuration` during the pre-round load, ticks
  down at GO; SP + MP, and in MP it's the display-only server-driven puppet timer, so it's purely a
  visual). Attach to the Digital Clock prefab (`Assets/Prefabs/Objects/HubWorld/Digital Clock.blend`);
  assign a positioned TextMeshPro child to `display`, or leave empty to auto-create one aligned via the
  `screen*` fields. Tunables: `digitColor` (red LED default), `separator` (":" default; "/" for
  MM/SS/SSS), `idleText` ("00:00:000"). NOT NetworkObject-driven — every machine reads its own
  GameLoopManager, no sync needed.
- **HUB spectator TVs (2026-07-21):** `Multiplayer/HubSpectatorTV.cs` — a hub TV bound to a TEAM that
  cycles every `cycleSeconds` (5) through that team's players CURRENTLY in the track, showing a chase-cam
  view of each. A remote's literal camera can't cross the wire (video streaming), so it stands up a LOCAL
  `Camera`+`RenderTexture` and points a **reused `CameraFollow`** (swivel off — so its Position/Pitch/Roll
  /Rotation Smooth Times all apply; FOV-kick/swivel/barrier-audio are inert on a script-less puppet with a
  kinematic RB) at the racer's puppet. The RT is drawn onto the assigned `screenRenderer`'s
  `screenMaterialIndex` slot via a runtime URP/Unlit material; STANDBY (single-player, or no teammate
  racing) restores the authored screen material, so SP/idle looks exactly as built. Cycling is tracked by
  clientId (list reordering doesn't jump); a racer returning to the hub drops out immediately; 1 racer →
  shown solo until a teammate joins. **"In track" signal:** a new **bit 4** on the car-state byte
  (`RemoteCarManager.AreaInTrackFlag`, set from `MultiplayerWorld.InTrackLocally`) → stored on
  `PlayerRegistry.RemotePlayer.InTrack` — reliable regardless of track length (position alone isn't). Setup:
  add to each TV, set `team` (1/2) + drag the screen face Renderer in. Costs 2 extra camera renders (tune
  `renderWidth/Height`). Markers billboard to the hub main camera, so they read off-axis in the TV view.
- **Spectator TV orientation + framing (2026-07-21):** first look showed the feed sampled 90° off (car
  hugging an edge, top-down look) because the screen mesh's UVs are rotated. Texture scale/offset can't
  rotate (no U/V swap), so added `Multiplayer/Resources/HubScreen.shader` (URP unlit; `_UVRotation` about
  centre, keeps `_BaseMap_ST` so the flips still work) — in a **Resources** folder so `Shader.Find`
  survives build stripping. New `uvQuarterTurns` (0-3, live) rotates in 90° steps; combine with
  `flipVertical/Horizontal` for any of the 8 orientations. `flipVertical/Horizontal/uvQuarterTurns` all
  re-apply live via `ApplyScreenTransform`. Also added `matchPlayerCamera` (default on): at rig build it
  copies `Camera.main`'s `CameraFollow` (offset, 4 smooth times, look-ahead, roll-blend, baseFOV) onto the
  spectator follow so the TV frames the car like its driver's view. Set `renderWidth:Height` to the screen
  quad's aspect to avoid stretch.
- **Spectator TV fit controls (2026-07-21):** driver's view read more zoomed-in than the TV because the
  driver's FOV widens with speed/loops while the spectator was pinned to a static FOV, and the screen mesh
  UVs sub-sample the render. Added: `fieldOfView` is now the **live zoom knob** (no longer copied by
  `matchPlayerCamera`; applied each frame via `follow.baseFOV`) — raise it to show more actual scene;
  `uvScale` (Vector2) + `uvOffset` (Vector2) **scale/pan the projected image** on the glass (new
  `_UVScale`/`_UVOffset` in HubScreen.shader, applied after the rotation) to fit the render to the visible
  screen. All live-tunable via `ApplyScreenTransform`. Rule of thumb: UV-scale-out reveals more only if the
  mesh crops the render; if it just shows borders the render is too tight → widen `fieldOfView` instead.
  Also `nearClip`/`farClip` (0.3 / 20000 defaults, live-applied) — big far plane so distant track/loops
  aren't culled from the feed.
- **Spectator TV bounds auto-framing (2026-07-22):** a car whose model differs from the tuned-against one
  framed wrong (esp. off-centre pivots → "not framed at all"). NOT a host/client difference — the rig is
  identical and the offset isn't per-car; it was the CAR being filmed. Fix: `HubSpectatorTV.FrameCar`
  aims at each car's CENTRE OF GRAVITY and sets the follow distance from its bounds, so every model frames
  the same. **CoM source (2026-07-22 revision):** the visual-bounds centre wasn't effective — cars define
  their CoM as a child object named **"CenterOfMass"** (CarController feeds it to `Rigidbody.centerOfMass`)
  which survives the puppet strip; `FindCenterOfMass` targets it directly (fallback: `rb.centerOfMass`,
  then bounds centre). `ComputeLocalBounds` now also covers SkinnedMeshRenderers. Mechanism: new
  `CameraFollow.focusLocalOffset` (target-local point the rig frames around; **zero for the player
  camera — unchanged**) is set to the car's CoM; `follow.offset` = the tuned
  angle (`offset` direction) × a distance computed from the car's bounding-sphere radius so it fills
  `boundsFillFraction` (0.7) of the vertical FOV. Bounds via `ComputeLocalBounds` — rotation-independent
  (mesh corners → root-local), skips inactive (hidden flames/SD), TMP label, trails/particles; cached per
  car. `matchPlayerCamera` no longer copies offset (bounds-driven now); target stays the actual puppet so
  cycle cuts still auto-realign and the FOV path (kinematic RB) still works. Also fixed a latent bug:
  `PlayerCarSwapper` re-pointed EVERY `CameraFollow` (incl. spectator cams) at the local car on swap — now
  skips any under a `HubSpectatorTV`.
- **Boulder spawn-flash fix (2026-07-22):** clients saw LavaBoulders flash half-buried inside track scenery
  for a frame before "disappearing" (launching). Cause: `NpcReplicator.HandleSpawn` built the boulder
  puppet at its raw spawn position — the launch origin, a half-buried point on the ground (the reliable
  SPAWN arrives before the boulder has velocity, and the puppet has no velocity until its first STATE, so
  it sat frozen at the buried spot; the host never shows this because its real rigidbody launches out via
  physics immediately). Fix: (1) `HandleSpawn` now DEFERS boulders (only) to their first STATE, which
  carries the launch velocity; drones/projectiles still build on spawn. (2) `RemoteCarPuppet` snaps
  `projectGravity` puppets to the **projected** flight position (`pos + v·τ + ½τ²g`, matching the first
  Update's lead) instead of the raw point, so a boulder pops in already airborne.
- **Puppet collider/visual decoupling fix (2026-07-22):** after the above, clients reported the boulder
  MESH moved but the COLLIDER stayed behind ("invisible objects you still bump into" at the old spot). Root
  cause: the LavaBoulder rigidbody has **Interpolate** on, and `StripPuppet` set kinematic/gravity/collision
  mode but NOT interpolation — so physics interpolation kept managing the transform for rendering while
  `RemoteCarPuppet` drove it by `transform.position`, decoupling the rendered mesh from the physics collider.
  Fix: `StripPuppet`'s keepColliders branch now sets `rb.interpolation = None` (puppets do their own
  extrapolation/smoothing; the collider must track the transform exactly). Applies to ALL solid puppets
  (players/drones/boulders), so it also tightens player-vs-player contact accuracy. **Gotcha for future
  puppet work:** any transform-driven kinematic puppet must have rigidbody interpolation OFF.
- **Player-car judder — RESOLVED, and NOT with interpolation (settled 2026-07-23). Read this before
  touching Rigidbody interpolation again.** The host's car appeared to vibrate up/down more than the
  client's on turns/drifts. It was tried as an interpolation problem (`CarController.Start` setting
  `rb.interpolation = Interpolate`) — **that change has been REVERTED and should not be reintroduced.**
  Actual cause: **the size of the Editor game window in Focus Mode**; maximized/fullscreen heavily
  reduces the vibration. Setting cars to Interpolate also caused a WORSE, separate regression —
  **DroneCar movement jittered as if on bad ping** — plus it broke the hub↔track portal teleport (see
  below). Player cars therefore stay on interpolation **None**. Puppets are transform-driven and also
  **None**. If the *physics* bob itself ever feels like too much, that's suspension tuning — raise
  `springDamper` (16 → ~23 = critically damped) — not interpolation.
- **Teleport hardening (2026-07-22, from the reverted interpolation experiment):** while cars were
  briefly set to Interpolate, the hub↔track portal broke — players "kept falling" while the
  skybox/music had already switched, because teleporting an **Interpolate** rigidbody via a plain
  `transform.SetPositionAndRotation` leaves interpolation's pose HISTORY at the pre-teleport spot and
  it render-smears across the ~35 km jump. `MultiplayerWorld.TeleportCar` was added and **kept**: it
  sets the pose, calls `Physics.SyncTransforms()` (so THIS frame's suspension raycasts/camera read the
  destination), then toggles `rb.interpolation` off→on to clear any history. With cars back on `None`
  that toggle simply no-ops, so the helper is now just correct-by-construction teleporting. BOTH
  `EnterTrackLocally` and `ReturnToHubLocally` (portal out / kill floor / LRA abort / return portal)
  route through it. **Gotcha:** any future transform-teleport of an interpolated body needs that
  history-clear.
- **Round lifecycle** (server loop in MultiplayerWorld, extended): round start clears the claim →
  round runs (portal for all, individual entry/exit, per-player deaths/finishes as before) → round end
  broadcast (everyone teleports home FIRST) → **`EvaluateRoundServer()` runs ONCE**: claimed team ⇒
  win check at `sdItemsToWin` (3); nobody ⇒ shared `DroneWins++` (replicated via `GNRC_SCORE` →
  `RemoteSetDroneWins` for HUD reads) ⇒ **drone ending for EVERYONE** at `droneWinsToGameOver` (2).
  Game over stops the round loop; `ApplyRoundStart` also guards on `GameEnded`.
- **Endings on all machines at once** (`GNRC_ENDING` → `MultiplayerWorld.ApplyEnding` → new
  GameLoopManager puppet triggers `RemoteTriggerDroneEnding`/`RemoteTriggerTeamVictory` fire the SAME
  events as single-player, so **HubSceneController's existing presentations play unmodified**): drone
  ending = the swarm + music swap (the hub portal it spawns is inert in MP — `CanEnterTrack` is false,
  so the ClipperEnding secret escape is **deferred**); team victory = the banner presentation with
  **per-team text/colour set beforehand** ("YOUR TEAM WINS" green for the winners, "TEAM N WINS" red
  for the losers) — the deferred single-player victory presentation folded into the team win as
  planned. After an ending players sit in the hub (swarmed or victorious) and leave via the Start
  Menu / hub exit, same as single-player.
- **Supporting edits:** `RemoteCarManager.TeamOfServer(clientId)` (scoring reads teams off the HELLO
  roster); `GameLoopManager` gained the three `Remote*` ending/score puppets.
- **Two-instance checks:** finish first on one instance → SD lands in that inventory only, both
  instances log the claim; let a round time out with nobody finishing → both show the drone-win log,
  second timeout → BOTH machines get the swarm; win 3 rounds on one team (with kill-floor wipes in
  between to verify the team aggregate shrinks) → both machines get the banner, winner green / loser
  red.
- **Post-playtest fix (2026-07-20): DronePissBall game-over exit left the MP world alive.**
  `DroneProjectile.ReturnToMainMenu()` (a drone-ending hit = back to the menu) did only the
  single-player teardown — the surviving `MultiplayerWorld` made the menu's background TrackGenerator
  generate out at the −35 km track offset (invisible menu backdrop) and the stale started session
  blocked re-hosting; quitting a fresh SP run "fixed" it because StartMenu's quit branch found the
  stale world and tore it down. Now has the SAME multiplayer branch as MainMenuReturnTrigger /
  StartMenu quit (leave session → `TeardownToMenu`). **RULE: every exit that loads MainMenu during
  gameplay needs that branch** — audited all `LoadScene` sites; the only unbranched one left is the
  legacy `ReturnToHub.cs`, which is referenced by nothing (dead).

**Phase 5 — Server-simulated AI & obstacles — ✅ CODE-COMPLETE 2026-07-20 (compiles 0 errors; on the
message layer like everything else — no NetworkObjects). PLUS the player-collision request (below).**
- **`Multiplayer/NpcReplicator.cs` (new, on the MultiplayerWorld object)** — host-simulated entities
  streamed to clients. The HOST runs the one true sim (the existing DroneCar/Boulder/projectile
  scripts, untouched physics); every spawn registers via `NpcReplicator.Track(go, kind, prefab,
  scale)` (no-op in SP/on clients, so spawners call it unconditionally) and streams typed state:
  **drones/challengers 15 Hz, projectiles 20 Hz, boulders 8 Hz — with GRAVITY-aware extrapolation**
  (`RemoteCarPuppet.projectGravity`: ballistic arcs curve between updates). Client puppets are built
  by PREFAB NAME from `RegisterPrefab` calls each spawner makes on every machine (registering a drone
  prefab auto-registers its projectile prefab — clients have no other path to it); spawns that beat
  the track-scene load park and retry when states arrive. Despawns replicate on destroy (kill floor,
  lifetime, scene unload); `ClearRoundPuppets` sweeps at round end. **Bandwidth note:** worst case
  (~30 boulders + ~20 drones + projectiles) ≈ 100–200 KB/s host upstream at 5 remote clients — fine
  on a decent connection, revisit rates if a weak host stutters.
- **Spawner gating** (`MultiplayerWorld.IsClientOnly` + `AnyPlayerInTrackServer`): DroneCarSpawner
  (+ChallengerCarSpawner — same script), BoulderSpawner, LightningSpawner idle on clients; host-side
  boulder/lightning also idle while nobody is racing. **DroneCarSpawner also needed a phase fix: it
  gated on `Phase.InTrack`, which NEVER occurs in the multiplayer puppet loop — track drones had
  silently never spawned in MP.** Lightning is EVENT-replicated (host rolls point+column height →
  `SpawnStrikeAt(point, height)` runs identically on every machine — same hazard, per-client contact
  damage stays local and consistent). Fans stay locally spawned everywhere but all their rolls now
  derive from `DeriveRandom("fans")` (they'd silently diverged per client before).
- **PATROLLING DRONE PLANES (user feature, 2026-07-23 — ✅ compiles 0 errors; NEEDS SCENE WIRING, see
  below).** `DroneAI/DronePlane.cs` + `DroneAI/DronePlaneSpawner.cs`, prefab `Prefabs/Planes/DronePlane`.
  Airborne hunters that own a patch of sky over the track. **Three states:** PATROL — flies a horizontal
  circle around its spawn point at a moderate cruise, holding its spawn altitude; CHASE — on spotting a
  player in its vision cone it locks on and pursues in FULL 3D (so it follows a car up hills, through
  loops and off jumps), holding `standoffDistance` (90) and sitting `chaseHeightOffset` (60) above the
  car so it **strafes rather than rams**, firing the DroneCar's burst cycle + `DroneProjectile` the whole
  time; RAGDOLL — ANY solid collision (scenery, a car, **another plane**) instantly kills the AI, flips
  `useGravity` on and lets it tumble `ragdollDuration` (1 s) before despawning.
  **Bounty:** crashing *while hunting someone* pays THAT player `killReward` (50) — local inventory, or
  `NpcReplicator.SendBounty` for a remote player; crashing on patrol with no target pays nobody.
  **Target loss:** `ValidateStickyTarget(target, anyArea:false)` is exactly the "did my target leave the
  track?" test (LRA / kill floor / return portal / disconnect) → the plane drops back to PATROL around
  wherever it now is, rather than flying home.
  **NOT A RACER:** it never calls `NotifyRacerFinished`, so a plane can't cost the player first place or
  score a round for the drones. Flying through the Return Portal is doubly safe — the portal is a
  TRIGGER (so no `OnCollisionEnter` → no ragdoll) and it only reacts to the `Player` tag.
  **Spawning:** FanSpawner-style scatter over `RoadEdge` centreline samples with lateral scatter, but the
  vertical offset is a **positive band** (`minVerticalOffset` 120 .. `maxVerticalOffset` 600) rolled per
  plane — always ABOVE the track, and the spread is what gives "mixed" altitudes. Patrol radius/speed are
  also per-plane ranges. Host-only sim like DroneCarSpawner (`IsClientOnly` early-out, `RoundLoadedLocally`
  gate, `TrackFrozen` respected in FixedUpdate); streamed as `NpcKind.Drone` (15 Hz, solid puppet).
  `NpcReplicator.RegisterPrefab` extended to auto-register a **DronePlane's** projectile prefab too.
  **Self-fire guard:** each plane `IgnoreCollision`s its own projectiles against all its colliders — without
  it a plane ragdolls on its own first shot.
  **Predictive aim (2026-07-23):** the plane leads its shots — `PredictAimPoint` aims where the car WILL
  be from its current trajectory (`leadTarget` on, `leadTime` 1 s), not where it is. **Velocity source
  matters:** remote players are KINEMATIC puppets whose rigidbody velocity is meaningless, so theirs comes
  off `RemoteCarPuppet.CurrentVelocity` (the replicated value); the local car uses its own rigidbody; a
  sampled position-delta is the last-resort fallback. Sight/cone checks still use the car's REAL position —
  only the aim point is led. Optional `useProjectileFlightTime` swaps the fixed lead for a 2-pass intercept
  solve (distance ÷ 402 m/s); **worth knowing: a fixed 1 s over-leads badly up close** — a shot crosses 50 m
  in ~0.12 s — so flip that on if planes miss at knife range. `maxLeadTime` (2 s) caps a wild reading.
  **Gizmos (2026-07-23):** `showVisionGizmo` / `showPatrolGizmo` / `showChaseGizmo` toggles (chase also
  draws the YELLOW predicted aim point + the lead offset from the car, which is how you tune `leadTime`).
- **PROJECTILE I-FRAMES / anti-stunlock (2026-07-23):** a landed DronePissBall grants
  `hitInvulnerabilitySeconds` (2 s) of immunity to **every** drone projectile — from any plane or drone
  car, not just the one that hit — so a pack can't chain-pop the player. State is a **static** on
  `DroneProjectile` (`invulnerableUntil` / `PlayerInvulnerable`) because the window belongs to the
  PLAYER, not to any one projectile. **Not replicated, deliberately:** each machine owns its own car's
  window, so BOTH hit paths test it — local contact in `OnCollisionEnter`, and host-reported hits in
  `ApplyRemoteHitToLocalPlayer` (the host can't know a client's window, so it reports every contact and
  the victim decides; a dropped hit costs one wasted message). An absorbed shot still despawns but
  plays the ENVIRONMENT impact, not the player one. Instances publish their inspector value into
  `lastKnownInvulnSeconds` so the static remote path uses the tuned duration instead of a second
  hardcoded number. `ResetStatics` (`RuntimeInitializeOnLoadMethod`) clears the window on load —
  without it, with domain reload disabled, a stale future timestamp + `Time.time` restarting at 0 would
  leave the player permanently immune. Vision cone uses
  the SAME expanding-ring style as DroneCar (green searching → red locked); patrol draws the horizontal
  circle + centre marker + the point on the ring it's flying to; chase draws the line to the car and the
  orange standoff hold-sphere. All work **before pressing play** — outside play mode the patrol centre falls
  back to `transform.position`, since `patrolCenter` is only assigned in Awake (it would otherwise draw at
  the world origin while authoring).
  **⚠️ SCENE WIRING STILL REQUIRED (not doable from script):** (1) create the **`DronePlane` layer** in
  Project Settings → Tags and Layers and set its collision matrix; (2) put a `DronePlaneSpawner` in the
  TrackScene with `trackGenerator` + `dronePlanePrefab` assigned; (3) give the DronePlane prefab a
  **Rigidbody + collider** and assign `projectilePrefab`; (4) set the plane's `visionMask` to the
  **Player + RemotePlayer + DronePlane** layers only (never terrain, or the cone gets blocked).
- **Sticky random targeting per the spec** (`MultiplayerWorld.PickStickyTarget(anyArea)` /
  `ValidateStickyTarget`): pool = players **currently in the track** (server `inTrackNow` +
  local car/remote puppets), `anyArea: true` for the hub ending swarm. Chase drones stick until the
  target CEASES TO EXIST (disconnect destroys the puppet) → then retarget randomly, idle if none.
  Boulders stick for their whole flight; if their player leaves the track mid-arc the homing just cuts
  out (ballistic — deliberately no retarget). **The "swarm splitting between players" the user saw in
  testing is now the DESIGNED behaviour, properly:** before, each machine ran its own private swarm
  chasing its own local player (the split was an artifact of two overlapping local sims); now ONE
  host swarm exists, every machine sees the SAME drones, and each drone deliberately hunts one
  randomly chosen player.
- **Server-truth first place (closes Phase 4's interim semantics):** AI racers exist only on the host,
  so its `AnyRacerFinishedAhead` is THE verdict — broadcast on rising edge (`GNRC_RACER_FIN`) so every
  client's local flag (credits bonus + finish reports) mirrors it, and `MultiplayerScoring.HandleFinish`
  now judges by the SERVER flag (client verdict logged if it disagrees).
- **Host-authoritative per-player effects:** projectiles collide with remote players' solid puppets
  (tag `RemotePlayer`, added to TagManager) → `GNRC_NPC_HIT` to the victim → `DroneProjectile.
  ApplyRemoteHitToLocalPlayer()` (pop-up + momentum halt, or the multiplayer-safe game-over exit in
  the hub drone ending). Knockoff bounties are ATTRIBUTED: `DroneCar` records who last shoved it
  (local tag vs RemotePlayer → clientId via `MultiplayerWorld.TryGetCarOwner`) and the kill floor
  payout goes to that player (`GNRC_BOUNTY` for remotes) — so ANY player can knock drones off, not
  just the host.
- **PLAYER-vs-PLAYER COLLISION (user request, 2026-07-20):** remote player puppets are now SOLID —
  `StripPuppet(go, keepColliders: true)` keeps colliders and turns every Rigidbody KINEMATIC
  (ContinuousSpeculative) instead of destroying them, and puppets carry the new **`RemotePlayer` tag**
  (never "Player": portals/kill floors/tag lookups still can't grab them). Cars bump instead of
  phasing; a kinematic puppet is infinitely massy, so each machine's car bounces off the other's
  puppet (standard casual-racer soft collision — with ~100 ms latency both sides feel their own
  bounce). Drone/boulder puppets are solid the same way (they shove the local car like the real sim);
  projectile puppets stay collider-less (hits are host-authoritative). **Consequence handled:** all
  players spawned on the SAME authored pose — now a formation offset (3 abreast, rows behind, keyed by
  sorted-clientId index) applies at hub capture, portal entry and hub return so nobody materialises
  inside a teammate. The old ReturnToHubLocally per-client boulder cleanup was REMOVED (boulders are
  shared now — a returning player must not delete them).
- **Two-instance checks:** track drones spawn (~60 s into a round) and BOTH machines see the same
  groups; drone projectiles hit either player (pop-up on the victim's screen); a client ramming a
  drone knocks it off and gets the bounty; boulders/lightning identical on both screens; fans
  identical; drone-ending swarm = same drones on both machines, attention split between players;
  players physically bump; spawn-in is a staggered formation, no overlap launch.

**STAGED ENTRY & MID-GAME JOINING (user flow change, 2026-07-20 — replaces the original
"everyone auto-launches on START GAME"):**
- **Host presses START GAME → only the HOST auto-enters the hub.** The lobby is NOT locked at start
  anymore (`StartGameAsync` no longer sets `IsLocked`; NGO approval no longer rejects started games).
  Every other player's READY button becomes **ENTER GAME** (`NetworkSessionManager.EnterStartedGame`)
  — they enter the hub individually, on their own accord, which also ends the everyone-materialises-
  at-once spawn-point choke (on top of the Phase 5 formation offsets).
- **The game loop does NOT begin until every seat the lobby allows is in the hub**: the server round
  loop waits (no timeout) until `readyClients.Count >= Session.MaxPlayers` (2 × team size — two teams
  of one waits for the 2nd player; two teams of two waits for all four). Then it **LOCKS the lobby**
  and starts the rounds.
- **Mid-game joining:** a player leaving mid-game (server `OnClientDisconnected`) **UNLOCKS** the
  lobby; a replacement joins through the normal lobby flow, presses ENTER GAME, and on hub arrival
  `MarkReady` (a) **syncs them into the round in progress** — a targeted ROUND_START now carries the
  TRUE remaining time (the message gained a `remaining` float; joiners load the same seed/track, get
  the portal, and their timer matches) — and (b) **re-locks** once the room is full again. Rounds keep
  running throughout; the loop never re-waits after it has begun.
- **Two-instance checks:** host starts alone → hub idle, no countdown/portal; second player ENTER GAME
  → loop begins + lobby shows locked; kill the client mid-game → lobby unlocks; rejoin → synced into
  the live round with the matching timer → lobby re-locks.
- **UI cleanup (2026-07-20, pre-real-machine testing):** (1) Main Menu AUDIO voice rows: smaller value
  text (22/24) + selector boxes widened to 450 + ELLIPSIS so long microphone names never bleed
  (Start Menu cyclers got the ellipsis + a small font drop too; `BuildOptionRow` gained optional
  width/font params). (2) Lobby BROWSER stays LEFT-anchored like every other screen (a brief
  centring experiment was reverted by user request); lobby rows extend rightward to 980
  (`BrowserRowWidth`; `MakeButton` gained `widthOverride`) and are **TWO-SECTION buttons**
  (`MakeBrowserRow`): the lobby NAME on the left (MidlineLeft, ELLIPSIZED when long, can never
  bleed) + a dedicated always-visible right column (`BrowserCountWidth` 170) showing "current/max"
  players — no per-team note (there are always two teams). Text matches REFRESH/BACK (uniform size +
  auto-caps via SettingsUI.NewText).
  (2b) **Car-cycler default fix:** the room's CAR cycler now applies its DISPLAYED initial value as
  the REAL selection at build (`OnCarChanged(carCycler.Index)` right after creation) — previously
  `SelectedCarStore` was only written by the change callback, so a player who never touched the
  cycler saw the first car's name but spawned the scene's default prefab (the cycle-away-and-back
  workaround). Pattern note: any code-built cycler/slider whose initial value implies state must
  apply that state explicitly — construction does not fire onChanged (by design, see Gotchas).
  (3) **READY mechanic REMOVED** — `ReadyToStart` now only checks team validity (host starts whenever);
  the room's ready button is gone, replaced by a hidden **ENTER GAME** button that appears (and grabs
  focus + rewires nav) for CLIENTS once the game starts, disappearing after use; player rows no longer
  show ready markers. The `ready` player property/`IsReady` helper remain in NetworkSessionManager but
  are vestigial.

**Phase 6 — Multiplayer UX & polish — ✅ CODE-COMPLETE 2026-07-20 for the USER-SCOPED subset only:
teammate markers, remote engine audio, and the RIVAL system. Explicitly OUT by user decision: team SD
tally HUD / drone-wins counter / scoreboard ("players keep track of the wins on their own").**
- **`Multiplayer/RemoteCarMarker.cs` (new)** — floating label over remote cars: code-built 3D
  TextMeshPro, billboarded to the camera each LateUpdate. `RemoteCarManager.RefreshMarkers()` labels
  per-VIEWER: **"TEAMMATE"** (cyan) over your own team's cars, **"RIVAL"** (red-orange) over the one
  opposing player assigned to YOU — rival markers are inherently private because each machine only
  labels its own rival. Refreshes on roster changes and rival-map updates.
- **`Multiplayer/RemoteCarAudio.cs` (new)** — engine audio for puppets, driven off replicated speed
  (the all-3D audio groundwork's payoff). The prefab's own `CarEngineAudio` TUNING (unique clip,
  pitch/volume curves, 3D settings) is copied off the prefab ASSET at puppet build (the instance's
  component is stripped), and the identical speed→pitch/volume mapping runs from
  `RemoteCarPuppet.CurrentVelocity` (horizontal only, eased, global-SFX-scaled).
- **RIVAL system (in `MultiplayerScoring`):** when the game loop begins (full room), the server gives
  every player a random rival FROM THE OPPOSING TEAM — each direction is a random permutation, so
  assignments are injective (nobody is the rival of two players on a side) but not necessarily
  mutual. **Reward:** reaching the end portal while your rival hasn't finished that round ⇒
  **+100 credits** (`rivalBonusCredits`; `GNRC_RIVAL_BONUS` to the finisher, reward stinger; a
  per-round `finishedThisRound` ledger on the server decides — if the rival never finishes at all,
  you still beat them). **Replacement:** a mid-game leaver creates a VACANCY (their orphaned
  opponents lose their rival — marker disappears — and their old rival is remembered); the next
  joiner inherits it exactly per the spec: joiner's rival = the leaver's old rival (validated,
  random-opposing fallback), and everyone whose rival was the leaver gets the joiner. Full map
  broadcast via `GNRC_RIVALS`; clients read their own entry (`TryGetMyRival` — a try-pattern because
  clientId 0 is the HOST, not a sentinel). Assignment waits briefly for hello-roster stragglers.
- **Two-instance checks (1v1 = mutual rivals by construction):** RIVAL marker over each other's cars
  + engine audio audible/revving on approach; finish first → +100 on top of completion credits, and
  the rival finishing second gets nothing; leave with a 3rd instance joining → the newcomer inherits
  the rival slot and markers update on both sides.

**VOICE CHAT — now on UNITY VIVOX (migrated 2026-07-23 from the DIY system; ✅ compiles 0 errors).**
Package `com.unity.services.vivox` 16.11.0 (modern `Unity.Services.Vivox`/`VivoxService.Instance` API).
Vivox owns capture, encode, echo-cancel/noise-suppression, transport (its OWN voice servers — NOT the
NGO host relay) and 3-D spatialization, so the whole class of DIY problems is gone.
- **PREREQUISITE (external, one-time):** Vivox must be ENABLED for the project in the Unity Cloud
  Dashboard (Services → Vivox); it auto-provisions credentials tied to the existing UGS project. Without
  it, `InitializeAsync`/`LoginAsync` throw — every call in `VoiceService` is guarded so it degrades to
  "no voice" (with a clear warning) rather than crashing.
- **`Multiplayer/VoiceService.cs` (new, PERSISTENT self-bootstrapped singleton — like AudioManager, via
  `[RuntimeInitializeOnLoadMethod]`).** NOT match-scoped, because the AUDIO-panel mic picker needs
  Vivox's device list and Vivox can only enumerate devices AFTER `InitializeAsync`. So: Vivox inits
  EARLY at boot (UGS `InitializeAsync` + anonymous sign-in — same auth `NetworkSessionManager` uses —
  then `VivoxService.Instance.InitializeAsync()`); LOGIN + channel joins happen only when a match begins.
- **Two channels (design preserved):** PROXIMITY = a Vivox POSITIONAL channel `prox_<sessionId>` joined
  by everyone; each client reports only ITS OWN car position ~10 Hz via `Set3DPosition(LocalCar, chan)`
  and Vivox mixes the falloff (`Channel3DProperties` audible/MAX **400** / conversational/MIN **200**,
  `LinearByDistance`). No per-puppet AudioSources anymore. TEAM = a 2-D GROUP channel
  `team_<sessionId>_<team>` joined only by that team. You stay JOINED to both (always HEAR proximity +
  teammates); **LB** (`Gamepad.leftShoulder`, menu-move tick, suppressed while menus open) only flips
  which you TRANSMIT into via `SetChannelTransmissionModeAsync(Single, prox|team)`. Bonus: the 35 km
  hub↔track gap means the positional model naturally stops cross-area proximity while team stays 2-D.
- **Team-speaker list:** unchanged UI (code-built overlay canvas, sortingOrder 140, below the SD HUD,
  ~0.4 s linger) — now driven by `VivoxParticipant.SpeechDetected` over `ActiveChannels[teamChannel]`
  (self shown via `IsSelf`, others via `DisplayName` set at login). Only team-transmitters show, exactly
  as before.
- **Settings (BOTH menus' AUDIO panels, still persisted in `GameSettings`, polled live):** MICROPHONE
  cycler now sources `VoiceService.InputDeviceNames` (Vivox devices; "Default" = leave Vivox's default —
  list may be empty until Vivox finishes booting), MUTE MY MIC → `MuteInputDevice`, MUTE PLAYERS →
  `MuteOutputDevice`. **VOICE volume** is its OWN slider (3rd in the AUDIO panel, next to MUSIC/SFX;
  `GameSettings.VoiceVolume`, **default 0.5**) that raises/lowers ONLY **other players' incoming voices**
  (for quiet remote mics) — it maps onto Vivox **per-CHANNEL volume** (`SetChannelVolumeAsync` over every
  joined channel, applied again on each fresh join), −50..+50, ≈0.5 = neutral, >0.5 boosts, <0.5 quietens.
  **Deliberately NOT `SetOutputDeviceVolume`/`SetInputDeviceVolume`:** per-channel volume is local
  playback of REMOTE participants only, so it can never touch your own mic/capture (the earlier
  output-device version at max default made the mic feel hypersensitive / fed back). The global output
  device is pinned to neutral (0) at init. `VoiceService.ApplyVoiceVolumeLive()` applies it mid-drag.
  **Proximity Min/Max distance** are the `ConversationalDistance`/`AudibleDistance`
  consts at the top of `VoiceService.cs` (currently **200 / 400**) — dev-tuned, applied at channel join
  (change + rejoin a match to take effect).
- **Channel lifecycle:** `MultiplayerWorld.BeginGame` calls `VoiceService.BeginMatch()` (was
  `AddComponent<VoiceChat>()`); `TeardownToMenu` calls `VoiceService.EndMatch()` (LeaveAllChannels +
  Logout). Team channel join is deferred until `LocalTeam()` resolves (may lag match start), throttled
  to retry ≤ every 2 s.
- **csproj:** `VoiceService.cs` compiled in place of `VoiceChat.cs`; added `<Reference
  Unity.Services.Vivox>` (asmdef is autoReferenced, so Unity re-adds it on its own regen). The old
  `VoiceChat.cs` (+meta) was deleted — git preserves it (incl. the 16 kHz capture-rate resampling fix
  that's now moot since Vivox handles capture/rate).
- **Testing caveats:** two instances on ONE machine still share the mic (feedback — use a headset or
  MUTE MY MIC on one). Vivox needs internet + the dashboard step above; if not provisioned you'll see
  the `[VoiceService] Vivox init failed` warning and no voice. Keyboard players still have no team
  toggle (LB is gamepad-only). Watch the boot log for `Vivox initialized. Input devices: …`.

**ROUND PRELOAD/GO SPLIT + "ROADING" SCREEN (user perf request, 2026-07-20 — ✅ compiles 0 errors).**
The track used to load AT portal-spawn time — a big hitch right as the hub portal/boost gate appeared.
The round now has two server messages instead of one:
- **PRELOAD** (`GNRC_ROUND_START`, now `{round, seed, live, remaining}`) fires at the TOP of the round
  cycle (right after the post-round delay): every machine async-loads the TrackScene behind a
  code-built **"ROADING" screen** (full overlay, sortingOrder 500, orange progress bar — async load
  maps to 0–85%, the synchronous generation hitch rides behind the 90–100% settle frames), generates,
  and lets spawners do their heavy work — **but FROZEN**: new `MultiplayerWorld.TrackFrozen` halts
  `DroneCar.FixedUpdate` (no movement/path progress/burst timers), and
  `GameLoopManager.RemotePrepareRound` sets round state while keeping phase `HubCountdown` so the
  round timer holds at full (spawner "elapsed" reads 0 — delayed groups wait naturally, zero-delay
  drone groups spawn immediately during the freeze). `DroneCarSpawner`'s MP gate is now
  `MultiplayerWorld.RoundLoadedLocally` (was phase-based). Boulder/lightning idle anyway
  (`AnyPlayerInTrackServer` false while the portal is down).
- **GO** (`GNRC_ROUND_GO {remaining}`) fires when the hub countdown ends: portal/boost gate spawn
  (ZERO load hitch — the track already exists), `TrackFrozen` clears, `RemoteBeginRound` starts the
  round timer. A GO landing on a still-loading client is parked (`pendingGoRemaining`) and applied the
  moment the load settles. Mid-game joiner sync reuses PRELOAD with `live: true` + true remaining
  (load → unfreeze immediately); a joiner during the countdown gets a plain preload.
- Round end / teardown clear all of it (`roundLoaded`, `TrackFrozen`, pending GO, the screen);
  `OnDestroy` clears the static so a freeze can never leak into single-player.
- **Checks:** portal/gate spawn with no hitch (the hitch moved behind the ROADING screen at round
  top); drones visible standing frozen on the track if you look; timers/Drone-Target-Finish pacing
  measured from PORTAL spawn, not scene load; mid-join both during countdown and mid-round.

**Phase 7 — Testing (throughout, not at the end)**
- Multiplayer Play Mode virtual players from Phase 3 onward; Unity Transport network simulator for
  100–200 ms latency + loss. Critical scenarios: two cars passing at combined 1200 mph, portal teleports
  mid-sync, disconnect mid-round, late join, all-leave-early round end.

**Sizing note:** Phases 0–3 are the make-or-break half; once two cars drive smoothly in the shared world,
Phase 4 is comparatively small because `GameLoopManager` is already shaped right. The riskiest items are
the extrapolation component and the additive-world restructure — **prototype those before polishing the
lobby**.

### What already lines up (do NOT rebuild these)
- **`GameLoopManager` already encodes both win rules**, and the numbers already match the multiplayer spec:
  `sdItemsToWin = 3` and `droneWinsToGameOver = 2`. `EvaluateRoundOutcome()` (runs once per round) is the
  single decision point: first place + enough SDs ⇒ win; anything else ⇒ `DroneWins++` ⇒ drone ending at 2.
  **The multiplayer change is narrow in shape** — make `CountPlayerSDs()` a *team* aggregate rather than a
  read of the one local `PlayerInventory`, make `playerFirstPlaceThisRound` team-scoped, and make the whole
  manager **server-authoritative** so one machine scores the round. The rule logic itself survives intact.
- **Audio is already multiplayer-shaped by design** — every world/gameplay sound is 3D specifically so remote
  players hear each other, and local-only effects (the speed-barrier muffle) are per-**AudioListener** rather
  than on a mixer. See "Design rules" below; don't undo either.

### What's missing / what will fight you
- ~~No netcode stack at all~~ — **RESOLVED in Phase 0**: NGO + UGS packages are in `manifest.json` (see the
  roadmap's Phase 0 status; the editor still needs to resolve them once, and the UGS project link is pending).
- **`PlayerInventory` is a single DontDestroyOnLoad singleton holding *the* player's items**, and SD ownership
  is read globally off it (`EquippedSD`, `Order`). Team aggregation needs per-player inventory state with an
  owner id, which is the deepest structural change in this list.
- **9 `FindWithTag("Player")` call sites across 7 files** — `DroneCar` (×2), `SDAbilityController` (×2),
  `BoulderObstacle`, `SpeedCheck`, `HubSpawnBoost`, `TrackGenerator`, `PlayerCarSwapper`. Every one assumes
  **exactly one** player car and silently grabs whichever it finds first → replace with the Phase 3
  `PlayerRegistry`. (The old `Assets/vehicle/*` sample scripts were confirmed dead and **deleted** in Phase 0.)
- **Singletons are only a problem for *shared* state.** Statics are per-process, so each client having its own
  `MenuState.AnyOpen`, HUDs, `StartMenuController`, camera rig and `SDAbilityController` input is *correct* —
  those are local-player concerns. What must move to server authority is **game state**: round scoring, SD
  ownership, drone wins, round start/end.
- **`CarController` is custom raycast-hover physics in `FixedUpdate`**, not a WheelCollider car. Unity PhysX is
  not cross-platform deterministic, so **lockstep is out** — expect server-authoritative movement with client
  prediction + reconciliation, or client-authoritative with server validation.
- **The track is procedurally generated** (`TrackGenerator`, plus a random road hue and `SkyboxHueRandomizer`,
  and `RoundDirectionalLightToggle`'s 33% blackout roll). **Every one of these needs a synced seed** or players
  will be driving different tracks under different lighting.
- **The grounded camera swivel and air rotation are local-only.** Both read `CarController.ManualYawInput` /
  `ManualPitchInput`, which are that machine's stick poll — on a remote car they'd read 0. Gate the camera rig
  and input reads on **local ownership**, not on "found the Player tag".

### ~~Open questions~~ — ALL FIVE SETTLED, see "DECISIONS MADE (2026-07-19)" above.

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
Three sessions have layered up here, so read the labels rather than assuming "this session":
1. **Audio foundation** — `AudioManager` + `AudioLibrary` ScriptableObject (`Assets/Resources/AudioLibrary.asset`),
   `Spatial3DSettings`, `PortalAudio`, `CarEngineAudio`, `BoulderAudio`, per-scene music, endings.
2. **Audio extensions + visual FX + gameplay systems**, and two editor-freeze fixes — the "New scripts" /
   "Modified systems" / "Bugs hit & fixed" sections below are mostly this one.
3. **Most recent session** — the full settings/rebinding stack in both menus, the manual air-ability rework
   (manual self-level, 3-axis manual rotation, the air-drift runaway fix), per-SD activation VFX, and the
   grounded camera swivel. All of it is documented in the bulleted sections near the top.

> **Compile status: VERIFIED CLEAN (2026-07-19, Phase 0).** `dotnet build Assembly-CSharp.csproj` → 0 errors
> including all of session 3's previously-uncompiled work (SD VFX, camera swivel, `CarController` additions),
> and again after deleting the `Assets/vehicle` sample kit. 32 warnings remain, all pre-existing deprecations
> (`enableWordWrapping`, `FindObjectOfType`-family). Note the new netcode packages haven't been resolved by
> an editor yet — first editor open will import them.

### AudioLibrary slots STILL EMPTY (`{fileID: 0}`) — assign clips (OGG/WAV)
As of the current `AudioLibrary.asset`: `playerVictoryMusic`, `menuClose`, `carLanding`,
`lightningWarning`, `sdActiveLoop`, `portalSpawn`, `portalDespawn`, `victoryBanner`.
**PLUS the 14 slots added this session, all unassigned** — Shield: `shieldCraftLoop`, `shieldCrafted`,
`shieldActivate`, `shieldActiveLoop`, `shieldDeactivate` (+ the `shieldAudio3D` tuning block); Grappling
hook: `grappleCraftLoop`, `grappleCrafted`, `grappleFire`, `grappleAttach`, `grappleRelease` (+ the
`grappleAudio3D` block); Support Ship: `supportShipActivate`, `supportShipLoop`, `supportShipDeactivate`,
`supportShipDestroyed`, `supportShipLaserFire`, `supportShipLaserHit` (+ the `supportShipAudio3D`
block — note the laser IMPACT is tuned by the round's own block on the laser prefab instead, since it
can land hundreds of metres downrange). **Each 3D block is the single tuning point for
its feature's sounds.** Two range gotchas: `grappleAttach` plays AT THE HIT POINT up to the hook's full
range away, so give `grappleAudio3D` a generous max distance or long successful shots land silently; and
`supportShipLoop` rides the SHIP, which the pilot can slide to the edge of its offset box, so a tight max
distance will make an escort fade out exactly when it's being flown.
(The user has been assigning clips live; several slots intentionally reuse a **placeholder** clip and may
want distinct ones: `turboCraftLoop`==`jetCraftLoop`; `projectileHitEnvironment`==`projectileHitPlayer`;
`loopBoost`/`boostGateBoost` reuse `turboBoost`; `menuOpen` reuses `menuSelect`; `windowsEnter`==
`windowsExit`; `speedBarrierBreak`==`speedBarrierLeave`.)

---

## What sessions 1–2 added / changed (session 3's work is in the bulleted sections near the top)

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
- `Car Scripts/SDAbilityVFX.cs` — on the **PlayerCar root**, next to the SD particle systems. A serializable
  `Entry[] effects` maps each SD's exact inventory name → its `ParticleSystem`; `Show(sdName)` plays that one
  and stops every other, `Hide()` stops all. `Awake() => Hide()` so nothing emits even if a system has
  Play-On-Awake ticked, and `SetPlaying` is idempotent (checks `activeSelf`/`isPlaying`) so repeated calls
  never restart a running system. Driven by `SDAbilityController`; optional and null-safe.
- `UI/GameSettings.cs`, `UI/OptionSelector.cs`, `UI/InputRebinding.cs`, `UI/SettingsUI.cs`,
  `UI/RebindController.cs` — the settings/rebinding stack; all documented in detail in the two Settings
  sections above.
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
  which also avoids the kill-floor audio pop; (6) the **grounded right-stick camera swivel** (both axes,
  diagonals, aim-easing latch) and (7) the **self-healing target cache** — both detailed in their own bullets
  in the settings/gameplay section above.
- `CarController.cs` — turbo **tire trails** (rear `TrailRenderer`s; emit on real turbo **or**
  `TriggerTurboTrail()` from BoostGate **or** `IsLoopGravityCut`; `turboTrail*` fields incl.
  `turboTrailHeightOffset`); `AirborneTime` property; `loopBoost` one-shot on the loop-flag rising edge;
  `OnJumped` event (for JetFlames); the **manual air-ability rework** (manual self-level, 3-axis manual
  rotation, fixed air drift — see the bullets above); and `ManualYawInput` / `ManualPitchInput` properties
  exposing the right-stick poll so the cameras can share it.
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

## Bugs hit & fixed in sessions 1–2 (the "failed attempts")
> Session 3's big one — the **air-drift runaway-speed bug** — is written up in full in its own bullet near
> the top, including why the old auto-leveler masked it.
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

> **0. THE ACTUAL NEXT FEATURE IS THE SUPPORT SHIP'S FIRING CONTROLS — see "NEXT TASK" at the top of
> this file. The ship itself is BUILT (2026-08-16); only the gun was deferred.** Everything numbered
> below is background/cleanup.

### ⚠️ OUTSTANDING EDITOR WIRING (accumulated this session — code is done, Unity setup is NOT)
Each of these makes a finished feature silently do nothing until it's wired:
- **Shield → EVERY car prefab.** Only `Melody.prefab` nests `CarAccessories/Shield.prefab`. The other five
  (`Car`, `Deora II Test Car`, `GeorgeCar`, `S-Sen7[Black_White]`, `S-Sen7[Red]`) have no `Shield` child, so
  L3 does nothing in them. Child must be named exactly **"Shield"**, on the **Shield** layer, with a
  collider, left **inactive**. (`ShieldAbility` now warns once per car naming the offender.)
- **`Shield` layer collision matrix** — ON vs lightning/fans/boulders/other cars, **OFF vs track + hub floor**.
- **`DronePlane` layer** + a `DronePlaneSpawner` in the TrackScene (trackGenerator + prefab assigned), and
  the DronePlane prefab needs a **Rigidbody + collider** and its `projectilePrefab`; set its `visionMask`
  to **Player + RemotePlayer + DronePlane only** (terrain in the mask blocks the cone).
- **Store rows:** `Grappling Gun` (suggest `maxOwned = 1`), `Wire`, `Plasma`, and the LRA row renamed to
  exactly **`LRA Premium`** — as of last check the saved scene still read `LRA`, which silently drops
  every abort to the inventory-wiping tier.
- **Vivox must be enabled in the Unity Cloud Dashboard** or voice throws at login and degrades to silence.
- **Skybox material** must be named with the **`SimpleSkybox` prefix** to receive the per-scene hue
  randomisation; add `Skybox/ProceduralSkyClouds` to Always Included Shaders if it renders in-editor only.
- **14 unassigned AudioLibrary slots** (shield ×5, grapple ×5, support ship ×4) — see the empty-slots section.

**Support Ship (2026-08-16) — all of this is required before it does anything:**
- **A `SupportShip` layer** must be added in Tags and Layers. As of writing, `TagManager.asset` has
  `DronePlane`/`Shield`/`Portal` but **no `SupportShip`**, and `SupportShipAbility` only logs a warning —
  without it the ship keeps the prefab's layer and the collision matrix below is not in effect.
- **`SupportShip` layer collision matrix.** This is the entire "what can down my ship" design, and the
  user's explicit choice was to make it a Project Settings decision rather than code. Suggested start:
  **OFF vs Track and the hub floor** — the ship trails the car with camera-style lag, so loops, tunnels
  and low ceilings would otherwise shred it constantly — and **ON vs Projectile, Boulder, Drone,
  DronePlane, Fan, Lightning and Player**. Also **OFF vs `SupportShip`** unless you want two escorts to
  collide. The owner's own car is already excluded in code, so leaving Player ON only lets *enemies*
  hit it, which is what the user asked for.
- **`SupportShip` child on EVERY car prefab.** Only `Melody.prefab` has one (at `(0, 2.5, -7)`, scale
  0.5). Same situation as the Shield. It must be named exactly **"SupportShip"** and needs a **collider**
  (any shape — the code turns it into a trigger). It can be left ACTIVE in the prefab: both
  `SupportShipAbility` and `RemoteCarManager.HideConditionalVisuals` now switch it off, since it is only
  ever a template. `SupportShipAbility` warns once per car naming the offender.
- **A `PilotControlCenter` prefab in the HubWorld scene** with the `PilotControlCenter` component and a
  **trigger** box collider (adding the component sets `isTrigger` via `Reset()`). The `.blend` asset
  exists but is not yet placed or scripted.
- **A `SupportShip` store row.** ⚠️ The name must match `SupportShipAbility.shipItem` **character for
  character**. `Norm()` only TRIMS — it does not fold case or internal spaces — so `SupportShip` and
  `Support Ship` are two unrelated items. This has now bitten twice (`'Plasma '`, then this), and the
  symptom is always the same and always misleading: the player buys the item and the ability silently
  does nothing. Settled on **`SupportShip`** (one word), matching the store row in `HubWorld.unity`.
  `Summon()` now logs a warning naming the string it looked for.
- **The BoxCollider is currently on the Melody INSTANCE, not the `SupportShip.prefab` asset.** The asset
  itself has the script and a Rigidbody but no collider, so any other car that nests the prefab gets a
  ship that can never be downed (nothing to trigger with). Move it onto the asset before rolling the
  ship out to the other five cars.
- **Portal on the ship's `crashIgnoreMask`** (or off in the matrix), or driving the racer through the
  TrackScene's return portal will cost them their ship.
- **`trackSkybox` on the PilotControlCenter** must point at `Assets/Prefabs/Skyboxes/SimpleSkybox.mat`
  (the TrackScene's sky). Unassigned, a hub-bound pilot flies over the track under the HUB's sky, which
  is Unity's plain built-in default.
- **The guns (2026-08-16):** assign **`laserPrefab`** on the `SupportShip.prefab` ASSET (not just the
  Melody instance) — `TuningTemplateFor` reads the car prefab's nested ship to arm remote copies, so an
  instance-only assignment leaves every teammate's ship firing blanks. The
  `SupportShipLasers` prefab needs a **Collider** (it currently has meshes only — without one nothing
  can ever stop a round) and should sit on the **Projectile** layer; `FireLaser` re-applies the layer
  regardless. A Rigidbody is added automatically by `[RequireComponent]` if absent. Check the
  **Projectile** layer's matrix row allows the targets you want hit — it already collides with Player
  for drone fire, which is why ignoring the owner's car is done in code instead.

1. **Phases 0–5 are DONE in code** (full detail per phase in the roadmap section; everything compiles
   0-errors via `dotnet build Assembly-CSharp.csproj`; still ZERO editor asset setup beyond filling
   `multiplayerCars` — note the **`RemotePlayer` tag was added to ProjectSettings/TagManager.asset**,
   which the editor picks up automatically). Two-instance runtime checklist: the cumulative Phase 2–4
   checks plus Phase 5's (same drones/boulders/lightning/fans on both screens, projectile hits and
   drone-knockoff bounties working for BOTH players, one shared ending swarm splitting its attention,
   players bumping instead of phasing, staggered formation spawns).
2. **Phase 6 is DONE for its user-scoped subset** (teammate/rival markers, remote engine audio, the
   rival system — see its roadmap entry). Explicitly NOT wanted: score HUDs (players track wins
   themselves). Still-deferred oddments if ever requested: puppet wheel spin, flames/SD VFX from
   replicated events, nameplates with player names, MP drone-ending secret escape (ClipperEnding),
   remote knockback on the SHOVED player's own screen, WindowsAudio possibly reacting to a puppet.
3. **Phase 7 (testing)** is really "keep doing the two-instance runs with Network Simulator latency" —
   the critical scenarios list lives in the Phase 7 roadmap entry, plus each phase's own checks.
2. **Assign the empty AudioLibrary slots** (8 long-standing + 14 added recently — see the empty-slots
   section) and, if desired, give distinct clips to the placeholder-shared pairs listed above.
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
  presentation has no design yet. **Multiplayer will need a team-win presentation anyway**, so these two are
  worth designing together rather than separately.
- **Retrofit `MainMenuController` onto the shared `SettingsUI` + `RebindController`.** It still carries its own
  inline `CreateSlider` / `CreateOptionSelector` / rebind flow; the Start Menu already uses the shared
  versions. Pure dedupe — behaviour is already matched, so this is cleanup, not a fix.
- ~~Input rebinding UI~~ — **DONE** (both menus, with conflict detection and live re-apply).

**GRAPPLING HOOK (user feature, 2026-07-23 — ✅ compiles 0 errors).** `Car Scripts/GrappleHook.cs`,
`Car Scripts/GrappleRope.cs`, `Multiplayer/GrappleReplicator.cs`.
- **GUN + AMMO ECONOMY (2026-08-13).** Firing needs BOTH:
  • `requiredItem` = **`Grappling Gun`** — the TOOL. Owned, **never consumed**; one purchase enables the
    hook for good. Deliberately NOT the ramp recipe's `capacityItem`; it gates the ability, not capacity.
  • `ammoItem` = **`Grappling Hook`** — SPENT one per shot, **at launch, hit or miss**, so a wasted shot
    costs you and range/aim matter. Crafted from Wire at the ramp (rotary slot).
  Either blank = that check is skipped (handy for testing). **RELEASING is never gated** — losing the gun
  or the last hook mid-swing must not strand the player on a tether they can't cut, so only FIRING
  checks. Both failure paths log rather than failing mutely.
- **AUDIO (2026-08-13) — 5 new AudioLibrary slots.** Ability (3D): `grappleFire` (at the muzzle),
  `grappleAttach` (**at the HIT POINT**, so a long catch is audible out where it landed and doubles as a
  positional cue), `grappleRelease` (at the car; covers RB-release, shield deflection AND a missed
  recall, since all funnel through `Release()`). All three tuned by one block, **`AudioLibrary.
  grappleAudio3D`** — note the attach can play up to the hook's full range away (200 m+), so its max
  distance wants to be generous or long successful shots land silently. Ramp: `grappleCraftLoop` +
  `grappleCrafted`. **The loop needed a new flag:** the rotary craft never sets `active`, so
  `SyncCraftLoop` keys off `rotaryActive` (true while the stick is turning on a craftable recipe);
  the loop is cut on each completion so continuous rotation restarts per hook, like Turbo/Shield.
- **HUD (2026-08-13):** `TurboJetHUD` gained a 4th label `Grapple: n` at x=1020, right of Shield — and it
  is **hidden until a `Grappling Gun` is owned**, since its presence is what tells the player the ability
  exists at all (a count for an ability you can't use would mislead). `Refresh` runs on every inventory
  change, so buying the gun reveals it instantly.
- **Controls:** **RB** fires from the car's nose (RB was completely unused — no conflict); **RB again**
  releases, whether still flying or attached. **RT + Y** reels. Range 200 m, recalled after a 2 s flight
  with no hit. Blocked layers (`blockedLayerNames`, inspector): **Portal / Projectile / Lightning**; the
  car's OWN colliders are always skipped by hierarchy test, not by mask — a remote player's car shares
  the `Player` layer, so a mask could never separate "me" from "them".
- **Flight sweeps along the travel SEGMENT** (not a point test at the new position): the hook covers
  hundreds of metres per second and would otherwise tunnel. **NOTE the hook has NO rigidbody/collider —
  there is nothing to set "Continuous Dynamic" on; a swept cast IS the CCD technique**, so speed alone
  can never make it miss. Velocity is inherited from the car so it still outruns you at 600 mph.
- **TWO-TIER detection (2026-07-23), and WHY:** a thin **`RaycastAll` is the PRIMARY** test; the
  `SphereCastAll` is a **secondary** pass only, widening the catch onto edges. Reason: `hookRadius` is a
  sweep RADIUS, not a catch range. Raised to 10, the sphere **engulfed the road the car sits on**, so
  the track was a distance-0 START-OVERLAP every step → discarded by the degenerate-hit rule → the hook
  flew straight through the world, ignoring the track entirely. A ray from inside the car body is in
  open air and reports the road correctly, so detection no longer depends on the radius being sane.
  Keep `hookRadius` ≈0.5–2; 0 disables the secondary test.
- **Tether = ONE inextensible distance constraint** (chosen over a jointed chain, which stretches and
  destabilises at this game's speeds). It only removes the velocity component moving AWAY from the
  anchor once taut — the tangential component is left alone, and **that IS the swing**, for free.
  Slack rope does nothing at all. `GrappleRope` is a many-segment VERLET rope for the visual only.
- **Reeling is decided by MASS:** anchor lighter than the car ⇒ the object is dragged in; heavier or
  static (the track) ⇒ the car is pulled instead and the rope shortens. Note a remote player's puppet is
  KINEMATIC but still carries the real car's mass from the prefab, so it's excluded from the
  `isKinematic` test that would otherwise disqualify every remote car.
- **Facing is a TORQUE, applied only while `CarController.IsAirborne`.** Torque (not a hard rotation)
  means it does its best and simply falls short when geometry won't allow it — exactly the user's case
  of grappling a car that's over a ledge while you're on the track. Grounded steering is never
  overridden, so a mid-race grapple doesn't make the car undrivable.
- **Multiplayer — replicate the ATTACHMENT, not the hook POSITION (rewritten 2026-08-13).** The first
  version streamed the hook's world position at 15 Hz and applied it RAW, which made the rope end lag
  and teleport whenever it was hooked to another player. Two compounding causes, both now gone:
  (1) raw 15 Hz application stepped visibly while the rope's OTHER end rode a smoothed puppet;
  (2) more fundamentally, a world point sampled on the OWNER's machine can never match where the VIEWER
  sees that car — each extrapolates it separately — and at 268 m/s the 66 ms gap alone is **~18 m**.
  **Now `GNRC_GRAPPLE` carries {senderId, state, KIND, anchorClientId, a, b, f}** and each viewer
  derives the endpoint itself (`TryResolveHookPosition`):
  • **Static** — a fixed world point, identical everywhere, sent once. Nothing to smooth.
  • **PlayerCar** — client id + LOCAL offset; the viewer reads **their own copy** of that car (already
    interpolated by `RemoteCarPuppet`) and does `TransformPoint(offset)`. Rigidly glued, zero lag,
    nothing to snap. Resolves the LOCAL car when the hooked player is you.
  • **Dynamic** (drone/boulder — no replicated identity to key off yet) — still streamed, but now EASED
    (`DynamicSmoothTau` 0.08) so it glides instead of stepping.
  • **Firing** — the flight is SIMULATED locally from {origin, velocity, elapsed}; a straight line at
    constant velocity needs no updates at all. Each message re-bases the local clock so drift can't
    accumulate, and a timeout guard stops a rope flying forever if the attach/release message is lost.
  **Rates collapsed accordingly:** only a Dynamic anchor uses `StreamRate` 15 Hz; everything else is a
  2 Hz `HeartbeatRate` (self-heals a dropped packet / late joiner). State CHANGES — fire, attach,
  release — carry the whole story now, so they go **Reliable**. Net result is smoother AND cheaper.
- **BREAK FREE — L3 (2026-08-13).** A grappled player shrugs the tether off with **L3**. Key point: the
  tether lives entirely on the GRAPPLER's machine and the victim holds no record of being hooked, so
  rather than replicate "who is hooked", the victim just **broadcasts `GNRC_GRAPPLE_BREAK` {victimId}**
  and whoever is attached to that car releases (`ReleaseHooksAttachedTo`). Reliable, one message per
  press, no new persistent state. Polled in `GrappleHook`, NOT called from `ShieldAbility`, so the two
  stay independent: the same press summons a shield **if one is held** and breaks the tether either
  way — **breaking free never requires a shield.** (A shield raised by that press then also deflects
  the next hook, per below.) Host fans the message out, since the grappler can be any machine.
- **SHIELDS DEFLECT HOOKS (2026-08-13).** A hook reaching a player's Shield layer **fails outright** —
  `ResolveHit` recalls it instead of latching on, so a raised shield is a real counter to being
  grappled (matching the shield already eating drone fire). Your own shield is never a candidate: it's
  a child of your car, which the hook always skips.
- **Pulling a remote player** can NOT push their puppet — movement is owner-authoritative and the next
  state update would overwrite it — so `GNRC_GRAPPLE_PULL` is relayed via the host to the OWNER's
  machine, which applies the acceleration to their real car. `PushAnchor` routes automatically.
- **Bootstrapping:** `GrappleHook` on the PlayerSystems object (like ShieldAbility); `GrappleReplicator`
  added to the MultiplayerWorld object at session begin. No scene wiring, no prefab.
- **"Grapple grabs the CreditsHUD canvas" — the REAL cause was NOT the UI (2026-07-23). Worth reading
  before trusting any `hit.point` from a sweep.** The rope appeared to latch onto a corner of the
  CreditsHUDCanvas rect from a huge distance. It never touched the UI. Chain:
  1. Going **uphill**, the muzzle (car position + forward × 2.5) buries itself in the rising track mesh.
  2. `Physics.SphereCastAll` starting **already overlapping** a collider reports that hit with
     `distance == 0` and — the killer — **`point == Vector3.zero`**. Documented Unity behaviour: there
     is no real contact point to report, so you get the origin.
  3. The hook honoured it and anchored to **world origin (0,0,0)**.
  4. The track area sits at `TrackAreaOffset` = **(0,0,-100000)**, so the rope stretched 100 km back to
     the origin — hence "grabbed from very far away".
  5. A **ScreenSpaceOverlay canvas's rect spans (0,0) → (screenW, screenH) in world units, so its
     bottom-left CORNER sits exactly on the world origin.** The rope ended at the origin; the canvas
     corner was simply the nearest thing drawn there. Pure coincidence — the canvas has no collider.
  **Fixed twice over — symptom AND cause:** (1) `TickFlight` skips any hit with `hit.distance <= 0f`;
  (2) the sweep now starts from **`SweepOrigin()` = car centre** (muzzle HEIGHT, no forward offset)
  instead of the muzzle. The `forward × 2.5` projection was the thing burying the origin in the hill —
  the height offset never was. The rope is still DRAWN from `MuzzlePosition()` (the nose), so it looks
  unchanged; the hook clears the car body within one step, and the body's own colliders are skipped by
  both the distance-0 rule and the `IsOwnCar` test.
  **Gotcha for any future sweep/raycast in this project: a cast that starts inside a collider returns
  point (0,0,0) — always reject `distance <= 0` before using the point.** With the track 100 km from the
  origin, that failure mode doesn't look like a local bug; it looks like a wild hit across the map.
- **UI hygiene (kept anyway, 2026-07-23).** Independent of the above: **every canvas here is code-built
  with `new GameObject(...)`, which lands on the DEFAULT layer**, so excluding the UI layer from a
  physics query achieves nothing on its own. TWO measures, belt-and-braces:
  (1) new `UI/UiLayer.cs` — `UiLayer.Apply(canvasRoot)` sets a canvas and all descendants to the **UI**
  layer; called at the END of each build (after children exist) in CreditsHUD, TurboJetHUD, SDCardHUD,
  LraAbortController, Speedometer and VoiceService — the canvases alive during track gameplay.
  (2) `GrappleHook.IsUserInterface` skips ANY hit whose collider has a **Canvas parent**, regardless of
  layer. That's the actual guarantee: a Canvas is never a legitimate grapple target, and it covers UI
  added in future without anyone remembering to call `UiLayer.Apply` or set a layer.
  **When adding new code-built UI:** call `UiLayer.Apply` at the end of its build for consistency; the
  Canvas check means forgetting can't reintroduce the grapple bug.
- **CRAFTING — 4th ramp slot, ROTARY (2026-07-23):** `Wire` → **`Grappling Hook`**, made by sweeping the
  **RIGHT STICK one full CLOCKWISE revolution**; keep spinning and it keeps crafting while Wire lasts
  (the surplus past 360° carries over). Progress is the **angle SWEPT**, accumulated frame to frame —
  not the stick's absolute position — so the player may start anywhere on the circle and REVERSING
  unwinds progress instead of granting it. Guards: `stickDeadzone` (0.5) resets the revolution when the
  stick is released, and `maxStickStepDegrees` (90) rejects per-frame jumps that are a flick across the
  centre rather than a swept turn (otherwise you could flick your way to a free craft). Only runs while
  no hold-bar is charging, so the ramp still does one craft at a time.
  **UI:** a RADIAL gauge, not a bar — dark-silver fill sweeping clockwise from 12 o'clock
  (`Image.Type.Filled` + `Radial360` + `Origin360.Top`). Unity's radial fill **requires a sprite** (a
  plain Image with none cannot be filled), so `RingSprite()` generates a feathered white annulus texture
  in code, keeping the ramp UI asset-free; it's white so each Image's own colour tints it.
  **Layout reflowed (2026-08-13):** the slots no longer sit at hard-coded offsets — each builder RETURNS
  where the next one starts and the panel sizes itself to the total. Two bugs that fixed: a **blank label
  still reserved 36 px**, which is why Turbo/Jet (blank labels in the prefab) had their counts floating
  far from their bars; and the fixed offsets had let the rotary slot overlap the hint line. Rhythm is four
  constants — `GapBarToText` **6**, `LabelHeight` 34, `GapLabelToCounts` 2, `GapSlotToSlot` **62** — so a
  bar sits tight to its OWN text while slots stay clearly separated. Adding a 5th slot can no longer
  silently overlap anything.
  **Note:** the recipe has NO capacity item — see the open question about "Grappling Gun" below.
- **Tuning:** `ropeStiffness` (1 = perfectly inextensible), `ropeSpring`, `reelForce`/`reelSpeed`,
  `faceTorque`/`faceDamping`, `hookRadius` (thickness makes edges much easier to catch), and
  `GrappleRope`'s `slack`/`segments`/`solverIterations` for how string-like the rope looks.

**SUPPORT SHIP — traps and tuning (user feature, 2026-08-16).**
The design summary is at the TOP of this file. These are the things that cost time or would look like
bugs to someone reading the code cold:
- **A kinematic body moved by transform writes raises NO collision events against static scenery.** The
  first instinct — kinematic follower + `OnCollisionEnter` — silently sails through the entire track.
  Triggers fire in that situation, AND still honour the collision matrix, which is why `SupportShip`
  detects death with `OnTriggerEnter`. Consequence: the ship's colliders are triggers while alive, so a
  `DroneProjectile`'s own `OnCollisionEnter` never runs against it — the ship destroys the projectile
  itself so the shot doesn't sail on into the racer. Solidity is restored on ragdoll so the wreck tumbles.
- **`Physics.IgnoreCollision` ERRORS on a collider whose GameObject is inactive**, and a car is full of
  those (Shield, jet flames, SD VFX, the ship template itself). `Attach` filters to `activeInHierarchy`;
  trigger overlaps with the car are caught by the `BelongsToCar` hierarchy walk instead.
- **The car's own colliders must be excluded in CODE, not the matrix.** Own car and enemy cars are both
  on `Player`, and the user wants enemies to be able to down the ship — so the matrix can't separate them.
- **Cloning is from the template's WORLD pose**, not `localPosition`. The ship may be nested inside an
  accessories group on some car prefabs, in which case a local-space read parks it in the wrong place.
- **Who may declare a crash is not "whoever sees one".** Every viewer runs a copy of every ship, but a
  third party's copy is glued to an *interpolated puppet* and will invent contacts. `detectCrashes` is
  true only on the owner's machine and the host; everyone else waits for `GNRC_SHIP_DOWN`. Note the
  division of labour that falls out of this: a client can only be downed by scenery/obstacles it
  simulates locally, while **projectile kills can only ever come from the host** — on clients,
  projectiles are puppets with their colliders stripped, so there is nothing to trigger against.
  Both paths report; `BroadcastDownVerdict` de-dupes, and `Crash()` is idempotent.
- **The host must record its OWN ship in the shared table.** `ResolvePilotRequest` runs on the host and
  checks `active` before granting; without `BroadcastLocalShipState` writing `ships[LocalClientId]`, the
  host's own ship is invisible to its own arbitration and nobody can ever fly it.
- **`MenuState.AnyOpen` does NOT stop a car driving.** It only gates the BUTTONS (turbo / jump / brake /
  self-level) — throttle and steering are read unconditionally in `CarController.Update`. Taking the car
  away properly needed a new **`CarController.InputSuppressed`** static, which zeroes every axis and
  skips every button. Use that, not a Rigidbody freeze, whenever a system needs the sticks: the car
  stays a normal dynamic body, so the world can still push it.
- **⚠️ "Pilot gets booted out of the cockpit after ~1 second" — a protocol bug, 2026-08-16.** Symptom:
  take control of a teammate's ship, view swaps correctly, then snaps back to your own car within about
  half a second; re-entering the pad repeats it forever. Cause: `ReleaseClaimsInvolving(clientId)` freed
  claims **held by** a client as well as claims **on** that client's ship — and every client heartbeats
  its own ship state at 2 Hz, so a hub pilot who owns no ship continuously announces
  `{theirId, active:false}`. The host read that as "release everything involving this player" and
  revoked the claim they had just been granted. **Fix: split the two meanings.**
  `ReleaseClaimsOnShip(ownerId)` for "this ship is gone" (dismissal, destruction);
  `ReleaseClaimsHeldBy(clientId)` for "this client is gone" (disconnect only). Keep them separate —
  the general lesson is that a level-triggered heartbeat saying "false" is NOT an event saying
  "something just ended", and treating it as one makes routine traffic destructive.
- **Losing the controls is debounced, deliberately.** A stray `OnTriggerExit` (physics re-filtering, a
  collider toggling, a re-parent) must not eject a pilot, and a remote ship is legitimately null for a
  frame or two whenever its owner's puppet is rebuilt. So `TickPiloting` requires the ship to stay
  missing for 1 s, and the car to stay off the pad for `padExitGrace` (0.5 s) — where "off the pad" is
  the AND of the trigger's own bookkeeping and an explicit `bounds.Contains` test, since the event is
  edge-triggered and can glitch while the bounds test cannot.
- **SELECT releases, not B.** B stays a pure menu button so it can't eject a pilot by reflex. Releasing
  drops them back into the ship LIST (still parked on the pad) rather than closing the station.
- **THE GUNS (2026-08-16).** `SupportShipLaser.cs` + `SupportShip.FireLaser()` + `GNRC_SHIP_FIRE`, fired
  with **A** from the pad. Star Fox 64 semi-auto: a press arms a fresh burst and fires its first round on
  the SAME frame (so a tap is instant and gives exactly one), holding walks the remaining `burstRounds`
  (3) at `burstInterval` (0.12 s) then stops dead until A is released and pressed again. Four things
  worth knowing:
  - **Rounds NO LONGER ignore the owner's car (changed 2026-08-16 — see the hit table below).** They
    originally did — the ship's resting offset IS the chase camera's, so it flies BEHIND its racer and
    "fire straight ahead" points at that car's back bumper. The user's call is that watching for that is
    the PILOT's job, so only the firing ship is excluded now (a round would otherwise die inside its own
    muzzle). This is what makes shooting your own racer for a boost possible.
  - **Firing is HOST-spawned, unlike aiming.** The offset is pure presentation so the pilot owns it
    outright; a round that can knock a drone down is game state, so `RequestFire` routes the trigger
    pull to the host, which spawns the round and streams it via `NpcReplicator.Track(NpcKind.Projectile)`
    (clients get collider-less visuals — contact resolves once). The pilot therefore sees their own shot
    a round trip late; nil when the pilot is the host. The host also checks `PilotOf(ownerId) == sender`
    so only whoever holds the controls can fire that ship.
  - **`ContinuousDynamic` collision detection is mandatory** at ~700 m/s — a round covers ~12 m per
    physics step, so discrete detection tunnels it clean through the track. Set in `Awake`, not left to
    the prefab.
  - **The laser prefab must be `NpcReplicator.RegisterPrefab`'d on every machine** or clients cannot
    build puppets for the rounds. It is only ever referenced from a car prefab's `SupportShip`
    component, so nothing else would find it — registered in `SupportShipAbility.BuildShip` (local) and
    `ResolveRemoteShip` (remote, after `CopyTuningFrom` supplies the reference).
- **WHAT A ROUND DOES ON HIT (2026-08-16).** All of it resolves on the HOST, the only machine lasers
  exist on, so each judgement is made once.

  | Target | Effect | Credits |
  |---|---|---|
  | Player car (**including the gunner's own racer**) | Popped up at `popUpForce` 40 — half a lightning strike's 80 — with **momentum kept**, unlike a DronePissBall which halts the car. Then 2 s immune to further rounds. | — |
  | DronePlane | Down in ONE hit, straight into its normal ragdoll. | 50 → **gunner** |
  | DroneCar / Challenger | `droneHitsToDown` (3) rounds, **no window between them**, then the same downed state a player ram causes. | its own `creditReward` (100/200) at the kill floor → **last toucher** |
  | LavaBoulder | Destroyed outright. | 25 → **gunner** |

  - **Rounds no longer ignore the owner's car.** They did, because the ship flies behind its racer and
    every shot would otherwise hit them; the user's call is that watching for that is the pilot's job.
    Only the firing SHIP is still excluded, or a round would die inside its own muzzle.
  - **The player i-frame window is SEPARATE from DroneProjectile's**, kept as its own static on
    `SupportShipLaser`. Being lasered must not grant immunity to drone fire or vice versa. Same
    domain-reload reset (`RuntimeInitializeOnLoadMethod`) the drone one needed, for the same reason.
  - **Credit attribution reuses the drones' existing "last toucher wins" fields** (`lastHitByRemote` /
    `lastHitClientId`) rather than adding a parallel system — which is what makes the user's rule fall
    out for free: a driver who shoulder-checks a softened drone overwrites the gunner in
    `RegisterPlayerContact` and takes the credits, and vice versa. Nothing new was needed at the kill
    floor.
  - **⚠️ `ChallengerCar.cs` is an EMPTY STUB — a Challenger is a `DroneCar` prefab** with
    `creditReward` 200 instead of 100 (confirmed: the prefab references the DroneCar script). So the
    DroneCar path covers both, and anything targeting "drone cars" must NOT look for a ChallengerCar
    component.
  - **⚠️ Unity does not define which object's `OnCollisionEnter` runs first, and that decided who got
    paid.** `DronePlane.OnCollisionEnter` unconditionally called `Crash()`, which awards the racer the
    plane was HUNTING. If it won the coin flip against the laser's own handler, the gunner lost the
    kill they had just made. Fixed by having the PLANE recognise a laser (`GetComponentInParent<
    SupportShipLaser>()`) and route to `DownedByPilot` itself; both orderings now reach the same place
    and the loser no-ops on the ragdoll state check. Worth remembering for any future projectile that
    hits something which also reacts to being hit.
  - A hit on a REMOTE player's car cannot be applied to their kinematic puppet (it would be erased by
    their next state update), so `GNRC_SHIP_LHIT` routes it to the machine that owns that car, which
    also judges its own invulnerability window — the same shape as `GNRC_NPC_HIT` and
    `GNRC_GRAPPLE_PULL`.
- **⚠️ "A teammate's ship stays rigid when I fly it" — TWO bugs in one symptom, 2026-08-16.** Flying your
  OWN ship worked perfectly while flying someone else's did nothing, which is the tell: the local path
  and the replicated path had diverged. `SyncRemoteShips` (now `SyncShips`) was:
  1. **Overwriting the pilot's own input.** It assigned `ship.PilotOffset = smoothedOffset` for every
     remote ship unconditionally, every frame — including one the local player was flying. A machine
     never sends itself its own aim (the `MsgAim` handler early-returns on `LocalPilotOf == ownerId`),
     so that stored offset stayed frozen at its initial value and dragged the ship straight back within
     the same frame the stick had moved it. **The rule now has one exception: if WE are the pilot, our
     writes are the truth** and the entry is updated FROM the ship instead (which also makes letting go
     seamless — the stored value is already current, so nothing snaps).
  2. **Skipping the owner's own ship entirely** (`if (ownerId == LocalClientId) continue;`). That was
     right for LIFETIME — `SupportShipAbility` owns that object — but wrong for STEERING, because the
     ability knows nothing about a teammate flying it. So even with (1) fixed, the racer would never
     have seen their own ship move. The loop now covers every ship including our own; only the
     build/destroy half is skipped for the local one.
- **Remote ships must be re-tuned from the prefab ASSET.** A remote ship is cloned from the template on
  that player's PUPPET, and `StripPuppet` destroys every MonoBehaviour — so the clone gets a freshly
  `AddComponent`ed `SupportShip` carrying nothing but code defaults. Anything tuned in the Inspector
  (movement box, speed, tilt, ragdoll, crash mask) would apply to your own ship and NOT to anyone
  else's. `SupportShip.CopyTuningFrom` + `TuningTemplateFor(carName)` reads the untouched asset via
  `PlayerRegistry.CarPrefabFor` — the same trick RemoteCarAudio/RemoteCarEffects already use. Any new
  tuning field on `SupportShip` must be added to `CopyTuningFrom` or it will silently be local-only.
- **Exactly one AudioListener may be enabled.** The pilot's listener is switched on only as the hub
  camera's is switched off, and `RestoreCamera` is null-guarded because a scene load can destroy the hub
  camera while someone is flying.
- **`SupportShip` runs at `[DefaultExecutionOrder(-50)]`** so it moves before the (unordered) cameras;
  otherwise the pilot's chase camera frames the ship's *previous* frame and reads as a permanent jitter.
- **A summoned ship PERSISTS across areas and scenes** — user requirement, so a teammate can fly it all
  session without ever racing. Two things had to be handled for that (2026-08-16):
  - **A scene load leaves NO local car for a frame or two** (old car destroyed, `PlayerCarSwapper` hasn't
    spawned the replacement). Reacting to `PlayerRegistry.LocalCar == null` by tearing the ship down
    killed it on every single transition. A missing car is now tolerated for `carLostGrace` (10 s); only
    a car that stays gone — quitting to the menu, teardown — ends the ship.
  - **A REPLACED car leaves the ship escorting a destroyed transform**, where it just freezes. It's
    re-`Attach`ed and its `defaultOffset` recomputed from the new car's template. Note multiplayer
    *teleports* the same car between areas rather than replacing it, so this is mainly a single-player
    path — but both are covered.
- **Hub↔track travel is a ~100 km TELEPORT, not movement.** Read as travel it banks the ship instantly to
  its limit and unwinds the rotation lag over the next second. A single frame of car movement over
  `teleportDistance` (500) re-snaps instead.
- **⚠️ `MultiplayerWorld.TrackFrozen` must NOT gate the Support Ship (fixed 2026-08-16).** Symptom: at
  round start the ship hung motionless in the air while its car drove away, then snapped back to it the
  moment the portal opened. Cause: the freeze guard was copied from `DronePlane`/`DroneCar` without
  re-checking that it applied. It doesn't — the flag means *"the preloaded TrackScene's AI must hold
  still until the hub portal spawns"*, and it is only ever correct for things that **live in the track**.
  The ship escorts a **player car**, and during preload every player is still in the hub driving around
  normally. General rule for anything new: `TrackFrozen` asks "am I a TrackScene entity?", not "is the
  round starting?" — a player-attached object should ignore it.
- **Kinematic bodies only support Discrete and ContinuousSpeculative** collision detection. The prefab
  was authored ContinuousDynamic, which makes Unity complain every time the ship is summoned, so `Awake`
  forces **ContinuousSpeculative** — also the right choice, being the one that still catches a fast pass
  through a trigger.
- **`MenuState.AnyOpen` blocks the L3+Y chord**, so a player parked inside the STORE trigger (having just
  bought a ship) cannot summon it until they drive out. Correct, but confusing the first time.
- **⚠️ FLIGHT CONTROLS REWORKED (2026-08-16) — the travel-derived nose tilt is GONE.** The old model
  banked the ship from how fast it was actually moving through the car's frame. That is now replaced by
  **stick-driven AIM ANGLES**, and the difference is the whole point: the guns fire along
  `transform.forward`, so the angles are what widen the arc of fire.
  - Sideways push → **local Y** (yaw); vertical push → **local X** (pitch), **negative climbing,
    positive descending**. Both scale with how far the stick is pushed, so the pilot picks the angle.
  - Movement speed is **uniform and independent of the angles** — turning never speeds the ship up or
    slows it down.
  - **The angles come from the STICK, not from travel**, which is what makes the headline behaviour
    work: a pilot pinned against the wall of the movement box keeps AIMING the way they're pushing
    while no longer MOVING that way. Nothing derived from the ship's motion could reproduce that —
    which is also why the angles had to start being **replicated** (`GNRC_SHIP_AIM` grew from a Vector2
    offset to `{Vector3 offset, Vector3 look}`); viewers cannot infer them.
  - Releasing the controls eases back to a level `(0,0,0)` over `lookSmoothTime`.
  - The movement box became a **3D box**: `maxForwardOffset` on top of horizontal/vertical, driven by
    **B (forward) / X (back)**. Set all three equal for a true cube. The selected-gizmo draws the real
    box now.
  - **ROLL on the bumpers (2026-08-16):** RB banks right, LB banks left, to `maxRollAngle` (80°) on
    local Z. Holding BOTH gives 0, which falls out of the `+1 / -1` sum for free and is exactly the
    "they cancel" rule — the target drops to level and the ship smooths out of whatever roll it was
    in, using the same `lookSmoothTime` as the other two axes. Roll is a pure AIM angle: it never
    moves the ship. Both bumpers were already free while piloting because `MenuState.AnyOpen` gates
    the grapple (RB) and the voice-channel flip (LB). `PilotLook` is a **Vector3** (x yaw, y pitch,
    z roll) and `GNRC_SHIP_AIM` carries `{Vector3 offset, Vector3 look}`.
- **The pilot camera gets the TRACK's sky, lighting and post-processing (2026-08-16).** All three come
  from the same root problem: the pilot is standing in the HUB but looking at the TRACK, and Unity's
  environment settings are per-ACTIVE-scene, not per-camera.
  - **Skybox.** The two scenes genuinely differ — `HubWorld` is assigned Unity's built-in Default-Skybox
    and `TrackScene` the procedural `SimpleSkybox` — so a hub-bound pilot was flying over the track
    under a plain grey sky while the racers saw the real one. Fixed with a per-camera **`Skybox`
    component**, which overrides `RenderSettings.skybox` for that camera alone and leaves every other
    view untouched. ⚠️ **`trackSkybox` must be assigned** on the PilotControlCenter (point it at
    `Assets/Prefabs/Skyboxes/SimpleSkybox.mat`) or the override has nothing to show and falls back to
    the hub's sky.
  - **Matching the round's HUES needed a new path.** `SkyboxHueRandomizer` only recolours on
    active-scene change, so a teammate who spends the whole session in the hub never recolours the
    track's sky and `CurrentSky` is null for them. `BuildRecoloured` was therefore split out of
    `Recolor` and exposed (with `CurrentSky` / `BuildRoundSky`) so the pad can build the track's sky
    itself — and because the hues derive from the shared round seed, it lands on exactly the colours
    everyone else got.
  - **Post-processing is ON** (`UniversalAdditionalCameraData.renderPostProcessing`, exposed as
    `enablePostProcessing`). This is the right default and not merely a preference: URP blends volumes
    at the **camera's** position, not the player's car — and this camera sits ~100 km away in the track
    area, so it picks up the TRACK's volumes and the pilot gets the same grade a racer's camera would
    get standing there. (An earlier note here claimed hub volumes would leak in because the pilot's car
    is parked in the hub; that was wrong — the car's position is irrelevant to volume blending.)
  - **The directional light needed nothing.** Lights are global once their scene is loaded, and both
    scenes are additively loaded for everyone, so the track's light (including whatever
    `RoundDirectionalLightToggle` has done to it that round) already lit the pilot's view correctly.
    `renderShadows` is left ON so it still casts.
- **The pilot camera IGNORES the ship's aim (2026-08-16).** It follows the ship's POSITION with a small
  lag on its LOCAL offset only (`cameraFollowLag`, 0.08 s — see below) and takes its ORIENTATION from
  `SupportShip.FollowFrame` — the lagged
  car-following rotation, without the pilot's aim angles. If the camera swung with the yaw, angling the
  ship to shoot left would simply drag the whole view left and nothing would look aimed; the widened
  arc of fire would be invisible. Held apart, the camera keeps the car's heading and the ship visibly
  angles inside the shot — the Star Fox arrangement.
  **This is why `SupportShipCamAnchor` exists as its own component** rather than a method on
  `PilotControlCenter`: CameraFollow derives both its placement and its aim from `target.rotation`, so
  the target has to BE an aim-free transform. ⚠️ **Its `[DefaultExecutionOrder(-25)]` is load-bearing** —
  it must run after the ship has moved (`SupportShip` is -50) and before the camera reads it
  (`CameraFollow` is unordered, 0). Driving it from `PilotControlCenter` does NOT work: that runs at
  +1000, after both, so the camera would frame a one-frame-stale anchor.
- **⚠️ The camera smooths the ship's LOCAL offset, never its world position (2026-08-16).** The ship's
  world position is "wherever the car is" **plus** "where the pilot has put it", and only the second
  half is the pilot's doing. Lagging the world position made the camera trail by an amount proportional
  to the car's SPEED — glued at a standstill, dragged along at 600 mph — which reads as the camera being
  yanked about rather than as the ship being flown. `SupportShipCamAnchor` now eases
  `SupportShip.LocalOffset` and rebuilds from the car's CURRENT position, so all of the car's travel is
  carried rigidly and `followLag` shows up exactly where it was asked for: the pilot sliding the ship.
- **The pilot camera's own frame smoothing is FORCED TO ZERO in `EnsureRig`, and that is not an
  oversight.** `CameraFollow`'s `positionSmoothTime`/`pitchSmoothTime`/`rollSmoothTime` ease its frame
  toward its target's rotation — but the target already carries the ship's `FollowFrame`, which the ship
  has ALREADY smoothed against the car. Leaving them set stacked a second lag on a first, and being
  ANGULAR it only bit while the car was TURNING: rock-steady at a standstill, ship sliding across the
  screen through a fast corner. One layer of laziness (the ship's) is the design — **tune it on the
  SHIP**, not the camera. The now-meaningless `matchPlayerCamera` / smooth-time fields were removed from
  `PilotControlCenter` rather than left as inspector traps (Unity silently drops dead serialized fields,
  so nothing had to be re-wired).
- **Tuning:** `defaultOffset` (taken from the authored child, so move the prefab child to re-home the
  ship), `positionSmoothTime`/`pitchSmoothTime`/`rollSmoothTime` (the ship’s CameraFollow-style lag
  against the car — **this is the ONLY frame smoothing in the chase now**, and these are
  duplicated from `CameraFollow` and must be kept in sync by hand), `maxHorizontalOffset`/
  `maxVerticalOffset`/`maxForwardOffset` (the pilot's box; drawn as a gizmo when the ship is selected),
  `offsetMoveSpeed`, `invertPilotVertical` (**ON by default** — stick up dives, stick down climbs,
  flight-stick style rather than point-where-you-want; the aim angles follow the resulting MOVEMENT
  direction, so they stay correct either way),
  `maxYawAngle`/`maxPitchAngle`/`maxRollAngle`/`lookSmoothTime` (aim rotation), and `ragdollDuration`.
  On `PilotControlCenter`: `cameraOffset`/`fieldOfView`/`cameraFollowLag` for the gunner’s framing,
  `trackSkybox` (must be assigned — see above), `burstRounds`/
  `burstInterval` for the guns, and `teamOnly`.

**PROCEDURAL SKYBOX with animated clouds + stars (user feature, 2026-07-23).**
`Prefabs/Skyboxes/ProceduralSkyClouds.shader` — a URP skybox in the spirit of Unity's built-in
Skybox/Procedural (tinted zenith→horizon gradient, flat ground colour, sun disk + glow off the scene's
directional light) plus drifting CLOUDS and fixed STARS. Fully procedural — no textures.
- **Draw order is sky → stars → sun → clouds**, so clouds correctly occlude both stars and the sun.
- **Clouds:** 5-octave fbm value-noise on a DOME projection (`dir.xz / (dir.y + _CloudDomeBias)` — the
  bias stops the horizon smearing to infinity), drifting along `_CloudDirection` at `_CloudSpeed`, with
  a time-warped domain (`_CloudTurbulence`) so shapes CHURN instead of sliding rigidly. `_CloudCoverage`
  / `_CloudSoftness` shape them; `_CloudHorizonFade` keeps them off the horizon line.
- **Stars:** hash-per-cell points from the quantised view direction. Because a skybox's direction never
  depends on camera POSITION, they're pinned to the sky and never move — as specified.
- **Sun placement (2026-07-23):** `_SunDirectionSource` picks between **Manual Angles** (default) and
  **Scene Directional Light**. Manual uses `_SunElevation` (−10..90; **90 = straight overhead / noon**)
  + `_SunAzimuth` (0..360), converted to a direction in the shader — so the visual sun is authored
  independently of scene lighting. The light mode reads `_MainLightPosition` (declared in URP's
  `Input.hlsl`, pulled in by `Core.hlsl` — do NOT redeclare it), guarded against a zero vector so a
  scene with no directional light can't NaN. **Gotcha:** in Manual mode the sun disk and the actual
  lighting direction are DECOUPLED — match them by hand, or switch to the light mode.
- **Sun is masked by `aboveHorizon`** so the ground OCCLUDES it. Without that a low sun drew as a bright
  disk sitting in the flat ground area (the symptom that prompted the manual-angle controls).
- **NIGHT MODE (2026-07-23)** — the sky goes dark on its own when the directional light is gone, like
  the built-in skybox losing its light. `_NightSource`: **Auto** (default) derives the night factor from
  **`_MainLightColor` luminance — URP sets that global to BLACK when there is no main directional light
  (or it's disabled)**, which is the reliable "no sun" signal; **Manual** drives `_NightBlend` yourself
  (0..1), which is the hook for a scripted day/night fade. `_NightLightThreshold` sets how dim counts as
  night. At night: colours are MULTIPLIED down by `_NightDarkness` × `_NightTint` (multiplied, NOT
  replaced with authored night colours — that **preserves the per-scene randomised HUE**, so the sky
  still reads as its own colour, just deep and cool); `_Exposure` lerps to `_NightExposure`; the SUN
  fades out entirely; stars go to full `_StarBrightness` while daylight scales them by
  `_DayStarVisibility` (0.15). **Ordering matters and is deliberate:** night is applied to the sky/ground
  BEFORE the stars are added (so stars stay bright against a dark sky), and clouds are night-darkened but
  composited LAST so they still occlude stars after dark.
- **Hue-randomizer hook:** it exposes `_SkyTint` + `_GroundColor` under the built-in names, so naming a
  material with the **`SimpleSkybox` prefix** makes `SkyboxHueRandomizer` hue-shift it per scene / per
  round seed exactly like the existing sky. Name it anything else to opt OUT of the randomisation.
- **Static GROUND TEXTURE (2026-07-23):** the same fbm cloud noise projected onto the ground plane with
  **no time term** — fixed mottling that breaks up the flat ground colour (`_GroundCloud*` properties).
  It's inside an `if (up < 0.05)` branch: sky and ground pixels are screen-coherent, so whole waves take
  one side and genuinely skip the other's fbm rather than paying for both.
- **Hue randomisation, extended (2026-07-23):** `SkyboxHueRandomizer` re-hues `_HorizonColor`,
  `_CloudColor` and `_GroundCloudColor` too, each behind a `HasProperty` guard so the built-in
  Skybox/Procedural material is untouched. **Only THREE hues are rolled (sky / ground / lit cloud), and
  they're drawn UP FRONT before the guards** — drawing them lazily inside the optional branches would
  advance the seeded stream a different number of steps on a machine whose skybox lacked those
  properties, desyncing multiplayer skies. **Horizon and ground-texture hues are DERIVED, not rolled**
  (`hSky + horizonHueOffset`, `hGround + groundTextureHueOffset`, both `Mathf.Repeat`-wrapped), so the
  sky gradient and the ground each read as one coherent surface instead of occasionally rolling a
  clashing pair. **`_NightTint` is rolled too** (4th independent hue), so the night sky gets its own cast
  per scene.
  `ShiftHue` takes optional saturation bounds, used for OPPOSITE reasons — both default to no clamping,
  so the original colours behave exactly as before:
  • `cloudMinSaturation` (0.2) raises the FLOOR for the lit cloud colour — **hue does nothing to a
  pure-white (S=0) colour**, so the shader's default white clouds would otherwise never visibly tint.
  • `nightTintMaxSaturation` (0.5) caps the CEILING for the night tint — that colour is **MULTIPLIED**
  into the already-hue-shifted sky, and a vivid clashing pair (red tint over a green sky) cancels the
  channels and collapses the night to near-black. Set it to 1 to allow those very dark nights.
- **⚠️ Shaders are NOT covered by `dotnet build`** — that only compiles C#. This one is unverified until
  Unity imports it; check the Console/material inspector on first import.
- **Perf note:** clouds are the expensive part (one 5-octave fbm + a 2-tap warp per pixel, full-screen).
  The warp deliberately uses single-octave `ValueNoise`, not `Fbm`, to keep it to ONE fbm per pixel. If a
  weak GPU struggles, drop the octave count in `Fbm` or set `_CloudTurbulence` to 0.
- **Build tip:** if the sky renders in the Editor but not in a build, add the shader to Project Settings
  → Graphics → Always Included Shaders (the usual skybox-stripping gotcha).

**LRA IS NOW A DEFAULT CAR ABILITY, with a PREMIUM upgrade item (user change, 2026-07-23 — ✅ compiles
0 errors).** Previously the L+R+A abort required an "LRA" item and always kept the inventory. Now:
- **DEFAULT tier (RED bar) — always available, no item, can't run out.** It's an innate ability of every
  car, so nothing shows in the inventory view. Cost: the inventory is **wiped to the starting defaults**
  (`ResetToStarting`), exactly like the kill floor / timeout. It only saves the player the fall.
- **PREMIUM tier (GREEN bar)** — whenever the player holds an **"LRA Premium"** (store item), the abort
  consumes one and keeps the inventory: the ORIGINAL LRA behaviour. Still one-use.
- `LraAbortController`: the `HasLra()` gate is GONE from `canAbort` (the combo now always works while
  in-track), `premiumItemName` replaces `lraItemName`, and `CompleteAbort` does consume-or-wipe in one
  place (`bool premium = inv.Consume(premiumItemName, 1); if (!premium) inv.ResetToStarting();`) so the
  two outcomes can never both fire. The bar's fill colour is recoloured **live in `ShowBar`** off
  `HasPremium()`, so mid-hold the player can see which abort they're committing to.
- Leaving the track now ranks: End Portal (keep + rewards) > LRA Premium (keep, no rewards) > LRA default
  (wiped, but on the player's terms) > kill floor / timeout (wiped).
- **Note:** `LraAbortController` is CODE-BOOTSTRAPPED (`AddComponent` in `PlayerInventory.Bootstrap`), so
  it has no serialized scene/prefab data — changed field defaults take effect immediately, unlike the
  UpgradeRamp/Store prefabs where the YAML wins.
- **⚠️ The store row must be renamed to exactly `LRA Premium`** — as of this change the saved HubWorld
  scene still read `LRA`, and a mismatch silently drops EVERY abort to the inventory-wiping tier (same
  failure shape as the `'Plasma '` bug above).

**SHIELD ABILITY (user feature, 2026-07-23 — ✅ compiles 0 errors; NEEDS SCENE/PREFAB WIRING, below).**
Plasma → Shield at the ramp; L3 summons an ellipsoid shield that eats drone fire for 2 s.
- **Crafting:** third bar on the Upgrade Ramp — **Y** (X=Turbo, A=Jet, B=Close were taken), `Plasma` →
  `Shield`, `craftTime` 1 s (same as Turbo). Capped at **4 held** via a NEW `CraftRecipe.maxProduct`
  flat cap (0 = none) — distinct from the existing container cap (`capacityItem` × `capacityPerContainer`),
  because Shield has no container item. `HasCapacity` enforces BOTH; the counts line renders `n/4`.
  Ramp panel grew 520 → 710 for the third bar. **"Plasma" itself is a STORE row configured in the scene's
  StoreController inspector** (the code-side default list is untouched).
- **`Inventory/ShieldAbility.cs` (new, bootstrapped on PlayerSystems):** **L3** (`Gamepad.leftStickButton`,
  polled directly like VoiceChat's LB — not a rebindable action) consumes one `Shield` and shows the car's
  shield child for `shieldDuration` (2 s), then hides it. One activation = one shield; a press while
  shielded or with none held is ignored. Finds the shield by CHILD NAME ("Shield", case-insensitive,
  inactive-inclusive) under the local car, and re-finds it on a fresh car (scene load / car swap).
- **Blocking projectiles — the subtle part:** `DroneProjectile.OnCollisionEnter` walks UP the hierarchy for
  the `Player` tag, and the shield is a CHILD of the tagged car root — so a shield hit would have popped
  the player anyway. It now checks the **Shield LAYER first** and, on a match, despawns with the
  environment SFX and no pop-up/momentum loss. Layer (not component) because puppets have their scripts
  stripped — the layer survives, so the same check works for local cars and remote puppets alike.
- **Multiplayer:** shield state rides **bit 5 (`RemoteCarEffects.FlagShield`, 0x20)** of the existing car
  stream byte — bits 0-3 are effects, **bit 4 is `AreaInTrackFlag`**, so 5 was the next free one.
  Level-triggered like the other flags (a dropped Unreliable packet self-heals next tick). This does
  DOUBLE DUTY: remote players see each other's shields, AND since projectile hits are **host-authoritative**,
  the host's copy of a remote player's shield collider is what actually blocks their incoming fire.
  `RemoteCarEffects` finds the puppet's shield by the same child name and toggles it.
- **HUD:** `TurboJetHUD` gained a third label, `Shield: n`, to the RIGHT of Jet (x=690).
- **Shield audio (2026-07-23) — 5 new AudioLibrary slots.** Crafting mirrors Turbo exactly:
  `shieldCraftLoop` (loops with the Y progress bar, cut on each completion so a consecutive craft
  restarts fresh) + `shieldCrafted` (one-shot, via `AudioManager.PlayShieldCrafted`). Ability sounds are
  3D at the car: `shieldActivate` / `shieldActiveLoop` / `shieldDeactivate`, played by
  `AudioManager.PlayShieldActivate`/`PlayShieldDeactivate` and (for the loop) a `ShieldLoopAudio` source
  `ShieldAbility` owns and repositions onto the car each frame — same shape as SDAbilityController's
  `sdLoopSource`. **3D TUNING LIVES IN ONE PLACE: `AudioLibrary.shieldAudio3D`** (a `Spatial3DSettings`
  block on `Resources/AudioLibrary.asset`) — blend / volume / min+max distance / rolloff / doppler,
  shared by all three shield sounds and applied via `ApplyTo(src, SfxVolume)`. NOTE this is unlike the SD
  loop, whose 3D values are still HARDCODED in SDAbilityController (min 5 / max 60).
- **Item names are now TRIMMED — `PlayerInventory.Norm` (2026-07-23). Read this before debugging any
  "the recipe/store can't see my item" report.** First Shield playtest: the ramp wouldn't register Plasma
  or craft. Cause was DATA, not code — the HubWorld Store override had `items.Array.data[4].itemName` =
  **`'Plasma '` with a TRAILING SPACE** (Unity quotes a string when it has one), while the ramp recipe's
  `shield.materialB` was `Plasma`. Two distinct dictionary keys ⇒ `GetCount` 0 ⇒ `HasMaterials` false ⇒
  the bar never charges, and the counts line shows 0. Item names are hand-typed into the Inspector in
  four unrelated places (store rows, craft recipes, HUD fields, SD tables), so this is a permanent
  footgun: `GetCount`/`Add`/`Consume`/`SetEquippedSD` now `Trim()` their key, making every lookup agree
  regardless of stray whitespace. `Order` (and so the inventory view) stores trimmed names too.
- **⚠️ WIRING STILL REQUIRED (not doable from script):** (1) create the **`Shield` layer** in Project
  Settings → Tags and Layers; (2) set its **collision matrix** — ON vs lightning / fans / boulders /
  other players+cars, **OFF vs the track and the hub floor** (this is the "passes through the world"
  behaviour, deliberately left as project settings, not code); (3) add the Shield ellipsoid as a child of
  **EVERY player-car prefab**, named exactly **"Shield"**, on the Shield layer, with a collider, left
  **INACTIVE**; (4) confirm the **"Plasma"** store row exists (and is priced) on the scene StoreController.
- **⚠️ The Shield is PER-CAR wiring, and that's the easy thing to miss (2026-08-13).** Symptom: "shields
  don't come up at all" — cause: the car being driven simply has no `Shield` child. As of this date only
  `Car Models/Melody.prefab` nests `CarAccessories/Shield.prefab`; the other five (`Car`, `Deora II Test
  Car`, `GeorgeCar`, `S-Sen7[Black_White]`, `S-Sen7[Red]`) do NOT, so L3 summons nothing in them.
  `ShieldAbility` now logs a **one-time warning naming the car** when the child is missing — it used to
  fail silently, which is indistinguishable from a broken ability. Same applies to any car added later.

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
  `IsTurboActive`, `IsLoopGravityCut`, `IsAirborne`, `AirborneTime`, `OnJumped`, `ManualYawInput` /
  `ManualPitchInput`). Optional per-car components: `PortalExitAudio`, `JetFlames`, `SDAbilityVFX`.
- **Cameras**: two always-running `CameraFollow` rigs (main + Rear View) with `CameraSwitcher` toggling which
  one *renders* on R3 — both keep following every frame, so the switch is instant. `CameraFollow` owns the
  lazy-Susan per-axis rotation lag, the speed/turbo/loop/barrier FOV kicks, the barrier low-pass muffle, and
  the grounded right-stick swivel. It re-resolves its `Rigidbody`/`CarController` whenever `target` changes.
  **All of it is local-player-only** — relevant when multiplayer lands.
