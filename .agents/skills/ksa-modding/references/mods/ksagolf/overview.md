---
type: Mod
title: KSAGolf
description: A golf mod foundation for Kitten Space Agency with a working putting game and a minigolf course builder on top of extensive frame-timing, camera, kitten-posing, terrain and developer tooling
mod: ksagolf
tags: [golf, putting, minigolf, kittens, camera, terrain, bepu, harmony, starmap, tooling]
repo: KSAGolf
license: MIT
author: LaurensDeV
loader: starmap
mod_id: KSAGolf
---

KSAGolf is LaurensDeV's golf mod for Kitten Space Agency: "clubs a kitten holds, a ball that flies
through whatever air the planet has, and later golf carts and minigolf courses". The README at this
ref still says there is nothing to play, but the code (and CLAUDE.md) has moved past that: the flown
kitten carries a putter, drops a ball, steps up to it with G, swings with the mouse, optionally in
first person with V, and a top-down builder (B) lays out minigolf holes from ten piece types that
the ball banks off and drops into and that a kitten can stand on.[^golf-readme][^golf-claude-md]
CLAUDE.md is explicit that only a subset has been seen working in game (the mod loading with its
props XML, the bridge's `frame`, `capture` and `kitten_prop`, and the putter and ball drawn on a
kitten); the rest is implemented and unit-tested but marked unverified in game.

Most of the repository is foundation another modder can reuse: a frame-timing discipline built on
KSA's applied simulation step, a camera controller swapped into the main viewport, kitten posing
through the engine's animation processors, terrain and hull contact, a file-based command bridge
with an MCP server for agent-driven visual testing, and a full update, build and release toolchain.

## Implemented, planned and absent

| Area | State at this ref |
|---|---|
| Mod loads, panel, menu-bar entry, developer tools, log | Implemented |
| Props, sounds and course asset XML (ball, putter, 12 course glTFs for 10 piece types plus a mirrored twin and a plinth, 3 ghost tints, 4 sounds) | Implemented |
| Kitten carries the putter; pose at address; first-person view | Implemented (putter on the kitten seen in game) |
| Putting: drop, address, mouse swing, strike sound, rolling ball on terrain, launch pads and courses | Implemented, unverified in game per CLAUDE.md |
| Ball flight with quadratic drag | Implemented in the ball model (`GolfBall` calls `Medium.Drag` with the air density at the ball); putting only launches along the ground. `Medium.Buoyancy` and `Medium.Coasting` exist but nothing calls them, and the water-density branch is not reached in play because the ball's ground is clamped to the waterline |
| Minigolf course builder, save and load, colliders a kitten stands on | Implemented, one hole per file; covered by headless tests, unverified in game per CLAUDE.md |
| Other clubs, aimed long shots, shot preview, scoring (a saved course carries a `Par` field nothing reads), multiple holes per course | Planned, absent |
| Chase camera that follows the ball (`ChaseView`, `ViewClaim`, `SmoothedStep`) | Written in `Sim/` with tests, no caller in game code yet |
| Ball against craft hulls (`ContactSweep`, `HullTest`), ground test for bodies in flight (`GroundTest`, `CoarseGroundTest`), ridge mask (`TerrainMask.Blocked`, `KsaWorld.IsOccluded`), slope reading (`GroundSlope`), local map frame (`TerrainMap`), buoyancy (`Medium.Buoyancy`) | Written and tested, not wired into the ball or any game path yet. `TerrainMask.TryBandBelow` is live: the cursor's terrain ray uses it |
| Golf carts and course pieces as parts | Planned, absent (no `<Part>` assets ship) |
| Two-bone IK, pose blending, held items described as data | Recorded in `docs/KITTEN-ARMS.md` as possible, not built[^golf-kitten-arms] |

## Install footprint

`mod.toml` is both the KSA content manifest and the StarMap manifest:[^golf-modtoml]

```toml
# src/KSAGolf/mod.toml L3, L8, L10-L11
name = "KSAGolf"
# ...
assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]
# ...
[StarMap]
EntryAssembly = "KSAGolf"
```

| Item | Value |
|---|---|
| Manifest id | `KSAGolf` (`[[mods]] id = "KSAGolf"` in the user `manifest.toml`) |
| Folder | `<user dir>/mods/KSAGolf/`: `KSAGolf.dll`, `mod.toml`, three asset XML files, `Meshes/` (14 glb), `Textures/` (13 png), `Sounds/` (6 wav), `VERSION`, `README.md`, `LICENSE` |
| Runtime files written | `Logs/KSAGolf.log`, `Logs/KSAGolf.prev.log`, `Logs/bridge/in` and `out` (developer installs), saved courses as `<user dir>/KSAGolf/Courses/<name>.json` |
| Developer marker | An empty `developer` file beside the DLL, written by `tools/deploy.sh`, never packaged |
| Target | `net10.0`, KSA build `2026.9.22.5482` (from `ksa-assemblies.lock`)[^golf-lock] |

## Loader model and entry class

StarMap loads `KSAGolf.dll` and instantiates `KSAGolf.KSAGolfMod`, the `[StarMapMod]` class.[^golf-mod]

| Hook | Fires (verified against StarMap and the decomp) | KSAGolf uses it to |
|---|---|---|
| `[StarMapImmediateLoad] OnImmediateLoad(Mod)` | Prefix on `Mod.PrepareSystems`, per mod, before asset bundles load | Log mod id and built-for vs running KSA build (first log line rotates the log file) |
| `[StarMapAllModsLoaded] OnFullyLoaded()` | Postfix on `ModLibrary.LoadAll` | Install every Harmony patch, build the putting session and course builder, start the bridge on developer installs, build the panel |
| `[StarMapBeforeGui] OnBeforeGui(double)` | Prefix on `Program.OnDrawUiFrame`, only when `DrawUI`[^starmap-programpatcher][^ksa-program] | Open ImGui's own main menu bar with a `KSAGolf` menu; this is a separate bar, not KSA's "Menu Bar" window (see [Developer panel](features/developer-panel-and-menu-bar.md)) |
| `[StarMapAfterGui] OnAfterGui(double)` | Postfix on `Program.OnDrawUiViewports`, only when `DrawUI` | Step the simulation (preferred), draw the panel, read golf and builder keys, run the craft mover and its gizmos |
| `[StarMapAfterOnFrame] OnAfterFrame(double, double)` | Postfix on `Program.OnFrame`, every frame, after `PostRender`; also on the early-exit frames that skip UI and render | Fallback step, bridge commands, release the frame latch in `finally` |
| `[StarMapUnload] Unload()` | Process exit only: after `Program.Main` has returned and disposed `Program`, and after StarMap's id-less `Harmony.UnpatchAll()` has removed every patch in the process[^starmap-core] | Release everything, unpatch (already a no-op), restore KSA's camera controller (on a disposed game), flush the log; only the log flush matters at that point |

Harmony patches and reflection, all installed at `StarMapAllModsLoaded` with their own Harmony ids,
each degrading (logged once) if its target is missing (the pattern is its own feature,
[Degrading Harmony patches](features/degrading-harmony-patches.md)):

| Target | Kind | Feature |
|---|---|---|
| `Program.OnFrameCelestials` (private) | prefix | [Dual-hook frame stepping](features/dual-hook-frame-stepping.md) |
| `Program.OnGameLoaded` | postfix | [Save-load detection](features/save-load-detection.md) |
| `SuperMeshRenderSystem.ClearBuckets(IViewport)` | postfix | [Drawing a mod's static mesh into every viewport](features/mod-mesh-draw-hook.md) |
| `KittenLocomotion.StepGrounded`, `KittenEva.UpdateRenderData`, `AnimatedRenderable.Draw`, `StaticMeshRenderable.Draw` | prefix and postfix | [First-person kitten view](features/first-person-kitten-view.md) |
| `Universe.ExecuteNextVehicleSolvers` | prefix (two patches) | [Launch-site surface raycast](features/launch-site-surface-raycast.md), [Course colliders as clutter](features/course-colliders-as-clutter.md) |
| `ConstraintSim.DetectCollisions`, `Simulate`, `TryResetForPool` | prefix (worker threads) | [Course colliders as clutter](features/course-colliders-as-clutter.md) |
| `GameViewport` `FixedController` backing field | reflection write | [Level-horizon camera controller](features/level-horizon-camera-controller.md) |
| `KittenRenderable._characterAvatar`, `Program._screenshotCapture`, `StaticObject._models`, clutter `_statics` | reflection read | Kitten features, captures, launch sites, course colliders |

## Dependencies

| Dependency | How it is resolved |
|---|---|
| StarMap (`StarMap.API.dll`) | Compile-time reference, `Private="false"`; the player launches through StarMap |
| Harmony (`0Harmony.dll`) | Ships with StarMap; referenced `Private="false"`, nothing for the player to install |
| KSA, Brutal (Numerics, Common, Strings, ImGui, Vulkan, Glfw, Concurrency), `Planet.Render.Core`, BepuPhysics, BepuUtilities | Referenced from `$(KsaDllDir)` resolved by `Directory.Build.props`; never copied[^golf-csproj] |
| ModMenu | Optional; a copied `ModMenuEntryAttribute` is found by name when ModMenu is present |
| KittenExtensions, ShaderExtensions, native libraries | Not used |

## Architecture

- **`Sim/` vs `Ksa/`.** 44 files of KSA-free maths (ball, drag, sweeps, course model, camera
  maths, frame latch, step gate, bridge parsing) are linked wholesale into the xUnit project; `Ksa/`
  binds to the game, with most engine access funnelled through the 2000-line `Ksa/KsaWorld.cs`.[^golf-ksaworld]
- **One step per frame on simulated time.** `StepOnce` runs from the first of three hooks, consumes
  `Universe.GetLastSimStep()` once, and steps the putting session, the course builder and any held
  camera. Everything in the ecliptic frame; Ego only at draw time.[^golf-frames-epochs]
- **Threads.** Everything runs on the main thread except the `ConstraintSim` prefixes and the
  `KittenLocomotion.StepGrounded` patches (vehicle solver workers) and the log's flush timer
  (thread pool).
- **Keys.** G (address), V (first person) and B (builder) are read with ImGui from the GUI pass, so
  they do nothing while the HUD is hidden.
- **Data flow for putting.** Mouse dx (camera controller) -> `PuttingStroke` -> strike velocity ->
  `GolfBall` in the body-fixed frame -> ground from the height field, launch-site colliders and the
  course surface -> drawn by the `ClearBuckets` postfix into every viewport; the kitten is drawn at
  address by an animation processor.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Content-plus-code mod layout](features/content-and-code-mod-layout.md) | getting-started | startup:immediate-load, startup:asset-load, startup:all-mods-loaded |
| [Mod log file with rotation and a verbose toggle](features/mod-log-file-with-rotation.md) | getting-started | startup:immediate-load, thread:background |
| [Prop and course asset XML](features/prop-and-course-asset-xml.md) | assets | startup:asset-load |
| [GLB authoring pipeline and mesh checks](features/glb-authoring-and-mesh-checks.md) | assets | build time |
| [World-positioned sounds](features/world-positioned-sounds.md) | audio | the step (frame:draw-ui, frame:celestials or frame:after-on-frame) |
| [Developer panel and menu-bar entry](features/developer-panel-and-menu-bar.md) | ui | frame:draw-ui |
| [Top-down minigolf course builder](features/minigolf-course-builder.md) | ui | frame:draw-ui |
| [Cursor picking and the terrain ray](features/cursor-picking-and-terrain-ray.md) | ui | frame:draw-ui |
| [Dual-hook frame stepping on simulated time](features/dual-hook-frame-stepping.md) | patching | frame:draw-ui, frame:celestials, frame:after-on-frame |
| [Degrading Harmony patches with per-feature ids and signature pins](features/degrading-harmony-patches.md) | patching | startup:all-mods-loaded, frame:after-on-frame |
| [Save-load detection](features/save-load-detection.md) | persistence | event:save-loaded |
| [Course grid model and JSON save file](features/course-grid-and-save-file.md) | persistence | frame:draw-ui |
| [Frame and epoch draw anchor](features/frame-epoch-draw-anchor.md) | rendering | frame:draw-ui |
| [Drawing a mod's static mesh into every viewport](features/mod-mesh-draw-hook.md) | rendering | frame:render |
| [First-person kitten view that keeps the shadow](features/first-person-kitten-view.md) | camera | physics:vehicle-solver, frame:render |
| [Level-horizon camera controller swapped into the main viewport](features/level-horizon-camera-controller.md) | camera | frame:viewports |
| [Borrowing the main view and handing it back](features/borrowed-chase-camera.md) | camera | frame:viewports |
| [Posing kitten bones through the pose-processor list](features/kitten-pose-processor.md) | kittens | frame:render |
| [Mod meshes held by a kitten as cosmetic attachments](features/kitten-held-props.md) | kittens | frame:render |
| [Putting address pose drawn on the kitten skeleton](features/putting-address-pose.md) | kittens | frame:render |
| [Putting session with a mouse-swung club](features/putting-session.md) | kittens | the step, frame:input, frame:render |
| [Golf ball physics in the body-fixed frame](features/golf-ball-physics.md) | physics | the step (frame:draw-ui, frame:celestials or frame:after-on-frame) |
| [Swept contact against a craft's real hull](features/swept-hull-contact.md) | physics | none: not wired into a hook yet |
| [Launch-site surface by raycast into a copied collider](features/launch-site-surface-raycast.md) | physics | frame:dispatch-solvers |
| [Course colliders registered as ground clutter](features/course-colliders-as-clutter.md) | physics | frame:dispatch-solvers, physics:bepu |
| [Course surface from exact shapes](features/course-surface-exact-shapes.md) | physics | the step (frame:draw-ui, frame:celestials or frame:after-on-frame) |
| [Terrain height queries off the engine's height field](features/terrain-height-queries.md) | celestials | frame:draw-ui |
| [Craft mover](features/craft-mover.md) | vehicles-and-parts | frame:draw-ui, frame:input |
| [File command bridge and MCP server](features/file-command-bridge.md) | testing-and-tooling | frame:after-on-frame |
| [Visual testing captures](features/visual-testing-captures.md) | testing-and-tooling | frame:after-on-frame, frame:post-render |
| [Headless simulation tests behind a Sim/Ksa boundary](features/headless-sim-tests.md) | testing-and-tooling | build time |
| [API surface record and assembly lock](features/api-surface-and-assembly-lock.md) | testing-and-tooling | build time |
| [Packaging, deploy and release pipeline](features/packaging-and-release.md) | testing-and-tooling | build time |

## Build, test and tooling

| Command | Purpose |
|---|---|
| `./tools/doctor.sh` | Can this machine build, test and run it |
| `./tools/build.sh`, `./tools/test.sh` | .NET 10 build; headless tests over `Sim/` |
| `./tools/deploy.sh`, `./tools/run.sh` | Install with the developer marker and manifest entry; launch through StarMap from WSL |
| `./tools/check-all.sh` | Everything CI's tooling job runs (also the pre-push hook) |
| `./tools/validate-parts.py`, `./tools/model/checkmesh.py` | Asset XML references and mesh defects |
| `./tools/api-surface.sh`, `./tools/check-assemblies.sh`, `./tools/ksa-api-diff.sh` | KSA update workflow |
| `python3 tools/ksa-mcp/server.py cli <cmd>` | Drive a running game through the bridge |

The repo also ships two Claude Code skills: `ksa-blender` (authoring art over Blender MCP) and
`upgrade-ksa` (the KSA update procedure).[^golf-blender-skill][^golf-upgrade-skill]

## Compatibility notes

- Built against KSA `2026.9.22.5482`; this bundle checked every engine claim against
  `2026.10.10.5554` and diffed the two decompiled builds. Every type and member in the mod's
  `docs/KSA-API-SURFACE.md`, and every private member it reaches by name (`OnFrameCelestials`,
  `_characterAvatar`, `_screenshotCapture`, `StaticObject._models`, `BubbleClutterStatics._statics`
  and its two-argument value constructor, `AnimatedRenderable.MaterialIndices`), still exists in
  2026.10.10.5554, and the order of the `Program.OnFrame` phases the mod hooks is unchanged.
- API drift between the two builds that touches the mod:
  - `ShadowRenderable` gained a `Center` field (the computed `Position` property is gone), and
    `SuperMeshRenderSystem.AddPoolToShadowBucket` now culls each entry per cascade against
    `Center` and `Radius`; in 5482 every pool entry went into every cascade.
    `AnimatedRenderable.Draw` and `StaticMeshRenderable.Draw` fill `Center`, but the whole-kitten
    shadow entries `FirstPersonHook` adds leave it at zero (see
    [First-person kitten view](features/first-person-kitten-view.md)); the effect is unverified in game.
  - `FixedController.OnFrame` now skips `CharacterPortrait` viewports and localises its alert; the
    mod's subclass replaces `OnFrame` on the main viewport only, so nothing changes for it.
  - `Universe.IsWorldSun` was removed and `UpdateWorldSun`/`NearestStar` added; the mod uses none
    of them.
- Not differences between the builds, but errors in the mod's own docs at its own build: KSA's top
  bar is a window named "Menu Bar" using `BeginMenuBar` in 5482 as in 5554, so the mod's
  `BeginMainMenuBar` entry was never part of it (see
  [Developer panel](features/developer-panel-and-menu-bar.md)); and `Core/Sounds.xml` has no
  `<SoundBehavior>` element in either build (see [World-positioned sounds](features/world-positioned-sounds.md)).
  Every `file:line` citation in the mod's docs points at an older build.
- Breakage patterns the mod guards against: private members reached by name (`OnFrameCelestials`,
  `_characterAvatar`, `_screenshotCapture`, the controller backing field, clutter `_statics`), each
  degrading to a logged no-op. Public patch targets are pinned into `docs/KSA-API-SURFACE.md` by
  never-called methods that really call them (`PinTheSignature`, `PinTheStep`, `PinThePatches`,
  `PinTheWalk`, `PinTheRender`), so a signature change is a build error; `ClearBuckets` has no such
  pin and is missing from the record, and the private `OnFrameCelestials` cannot be pinned at all
  (see [Degrading Harmony patches](features/degrading-harmony-patches.md)).[^golf-api-surface-md]

## Secondary sources

| Document | Worth reading for |
|---|---|
| [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-MODDING-NOTES.md) | Loader contract, key types, reference frames, part gates, character attachments, sound, statics that collide, threading[^golf-modding-notes] |
| [docs/KSA-FRAME-ORDER.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-FRAME-ORDER.md) | The frame in order and what instant each sample belongs to[^golf-frame-order] |
| [docs/FRAMES-AND-EPOCHS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/FRAMES-AND-EPOCHS.md) | The 29.8 km/s carrier, epoch rules, how to diagnose by dividing out milliseconds |
| [docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-CAMERAS.md) | Camera modes, controllers, following, viewports, the render path[^golf-cameras] |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-TERRAIN.md) | Where the engine thinks the ground is, measured off the height field[^golf-terrain] |
| [docs/KITTEN-ARMS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KITTEN-ARMS.md) | Posing a kitten, hanging a mesh on it, rig measurements |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/BLOCKED-ON-KSA.md) | What a mod cannot build and why, with a recheck list[^golf-blocked] |
| [docs/FROM-KSP-MODDING.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/FROM-KSP-MODDING.md) | Concept map for KSP part modders[^golf-from-ksp] |
| [docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/VISUAL-TESTING.md) | Seeing the game from a terminal[^golf-visual-testing] |
| [CLAUDE.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/CLAUDE.md) | Layout table, design decisions, CI and release, upgrade order |

[^golf-readme]: README.md
[^golf-claude-md]: CLAUDE.md
[^golf-modtoml]: src/KSAGolf/mod.toml
[^golf-csproj]: src/KSAGolf/KSAGolf.csproj
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-lock]: ksa-assemblies.lock
[^golf-modding-notes]: docs/KSA-MODDING-NOTES.md
[^golf-frame-order]: docs/KSA-FRAME-ORDER.md
[^golf-frames-epochs]: docs/FRAMES-AND-EPOCHS.md
[^golf-cameras]: docs/KSA-CAMERAS.md
[^golf-terrain]: docs/KSA-TERRAIN.md
[^golf-kitten-arms]: docs/KITTEN-ARMS.md
[^golf-blocked]: docs/BLOCKED-ON-KSA.md
[^golf-from-ksp]: docs/FROM-KSP-MODDING.md
[^golf-visual-testing]: docs/VISUAL-TESTING.md
[^golf-api-surface-md]: docs/KSA-API-SURFACE.md
[^golf-blender-skill]: .claude/skills/ksa-blender/SKILL.md
[^golf-upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
