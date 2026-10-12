---
type: Game Feature
title: Drive a running game from scripts through a file-drop command bridge
description: Poll a folder for JSON command files from the frame postfix so a shell script, a test runner or an agent can pause, step, load saves and script gameplay in a live game, with the bridge kept out of player installs
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
demonstrated_by:
  - mods/ksagolf/features/file-command-bridge
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksarmory/features/file-command-bridge-and-mcp
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/unattended-scenario-runner
  - mods/ksagolf/features/putting-session
  - mods/ksarmory/features/build-deploy-package-release
  - mods/ksarmory/features/own-log-file-and-developer-switch
  - mods/kittenextensions/features/patch-debug-stepper
  - mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf
lifecycle:
  - startup:all-mods-loaded
  - frame:after-on-frame
  - frame:input
  - frame:draw-ui
  - frame:viewports
  - startup:asset-load
game_types:
  - KSA.Universe
  - KSA.SimSpeed
  - KSA.SimStep
  - KSA.GameSaves
  - KSA.Vehicle
  - KSA.Program
  - KSA.Constants
  - KSA.ModManifest
  - KSA.ModEntry
  - KSA.KittenEva
  - KSA.FileReference
  - KSA.ShaderReference
  - KSA.ModLibrary
tags: [testing, bridge, json, file-ipc, developer-only, simulation-speed, save-load]
---

KSA is a Windows process with a Vulkan renderer, so a mod cannot be exercised by a plain unit test once it touches the live
simulation. The workaround four of the analyzed game mods converged on is a developer-only command bridge: the mod polls an
`in/` folder from its frame hook, runs one JSON command at a time and answers into `out/`. A shell script, a test runner or an
AI agent then pauses the world, steps an exact number of simulated seconds, loads a save, spawns things, sets any setting by name,
takes screenshots and reads state back. There is no port, no listener and no firewall prompt, and it works across the WSL to
Windows boundary because both sides only need the same folder.

The same section of every article answers the second half of the problem: a bridge (and any other developer-only code) must not
exist on a player's install. Three gates are shown, a marker file beside the DLL, an extra key in the player's `manifest.toml`
and a compile flag.

## Engine background

The bridge needs five engine facts, all checked against the decompiled build.

| Fact | Where | Why the bridge cares |
|---|---|---|
| `Program.OnFrame` runs one frame; after the render it calls `PostRender` and increments `FrameNumber` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2331-L2332) | StarMap's `StarMapAfterOnFrame` postfix runs after this, on every frame that is not cut short by an early return, including menus and frames with the HUD hidden. See [the frame order in the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) |
| The UI hooks sit inside `if (DrawUI)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269) | `StarMapAfterGui` stops firing while a screenshot hides the UI, so a bridge hooked there would die the moment it asked for a picture |
| `Universe.SetSimulationSpeed(SimSpeed)` assigns the speed field directly; `IsPaused()` tests it against exactly zero | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802) | Pause is speed 0, step is "unpause, count, pause", and there is no floor except in the UI's speed buttons |
| `Universe.GetLastSimStep()` returns the step `ApplyVehicleSolvers` last applied | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1948) | Its `DeltaTime` is simulated seconds actually consumed, 0 while paused or outside flight; summing it is how `step` counts exactly |
| `GameSaves.LoadSaveGame(string)` is the console `load` action and only logs an error when the name is unknown | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283-L302) | The bridge cannot trust a return value; it waits for a flown craft with a timeout instead |

The user folder comes from `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283)), which the mods
mirror in their own log-folder search so the same folder can hold `Logs/bridge/`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: in/ and out/ folders, `.part` rename, closures](drive-a-running-game-through-a-file-bridge-1.md) | ksagolf ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs)), ksacars ([src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs)) | `starmap-lifecycle` | `frame:after-on-frame` | The reference protocol with `step`, `load` and the developer marker; copy this one first |
| [2: generic get/set by reflection, composed commands](drive-a-running-game-through-a-file-bridge-2.md) | ksagolf ([src/KSAGolf/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/BridgeCommand.cs)) | `file-ipc` | `frame:after-on-frame` | Every public config field is scriptable for free; no per-setting code, but type coverage is limited |
| [3: lean variant with shader reload and a held camera](drive-a-running-game-through-a-file-bridge-3.md) | ksastructures ([src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs)), ksarmory ([src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs)) | `file-ipc` | `frame:after-on-frame` | Hot reload and visual commands; relies on internals through reflection |
| [4: a custom key in manifest.toml](drive-a-running-game-through-a-file-bridge-4.md) | kittenextensions ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs)) | `toml-config` | `startup:asset-load` | Player-controlled switch with no extra file; the game drops unknown keys when it saves the manifest |
| [5: DEBUG compile flag plus a runtime switch](drive-a-running-game-through-a-file-bridge-5.md) | ksa-deltavmap ([DeltaVMap/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/DebugConfig.cs)) | `msbuild` | `frame:draw-ui` | Dev code is physically absent from release builds, but a release build cannot be diagnosed |

Articles 1 to 3 are the transport; articles 1, 4 and 5 are the three alternative gates. Pick one gate and apply it to every
developer-only feature, not only the bridge.

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Take clean screenshots from code](../take-screenshots-from-code/overview.md), [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md), [Fly scripted scenarios unattended](../run-unattended-scenarios-and-report-a-verdict/overview.md)
- [Reload a mod shader in a running game](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md), [Spawn a vehicle at runtime](../../../vehicles-and-parts/vehicles/spawn-a-vehicle-at-runtime/overview.md), [Set simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md)
- [Package and publish a mod release](../../../getting-started/build/package-and-publish-a-mod-release/overview.md), [mod.toml reference](../../../getting-started/loader/mod-toml-reference/overview.md), [How StarMap launches KSA](../../../getting-started/loader/how-starmap-launches-ksa/overview.md), [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
- Related topics not yet written: letting AI agents control the game over MCP, steering a walking kitten, marshalling commands to the game thread, making an HTTP request without blocking the frame
- Mod features: [KSAGolf file command bridge](../../../../mods/ksagolf/features/file-command-bridge.md), [KSACars file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md), [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md), [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md)
- [Let AI agents drive the game through an MCP server](../../../external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md) - related topic
- [Simulate mod-owned bodies in sub-steps outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md) - related topic
- [Make a walking kitten move and face along the look direction, and drive it with the mouse](../../../kittens/eva/steer-a-walking-kitten/overview.md) - related topic
- [Debug XML patches with the step debugger and the session log](../../../assets/validation/debug-xml-patches/overview.md) - related topic
- [Marshal commands from background threads to the game thread](../../../external-io/control/marshal-commands-to-the-game-thread/overview.md) - related topic
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) - related topic
- [Make an HTTP request from a mod and add an in-game bug report form](../../../external-io/network/make-an-http-request-from-a-mod-without-blocking-the-frame/overview.md) - related topic
- [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md) - related topic

