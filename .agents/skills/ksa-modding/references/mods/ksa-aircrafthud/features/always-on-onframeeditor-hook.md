---
type: Mod Feature
title: Always-on ImGui hook from a Program.OnFrameEditor postfix
description: Patches the private Program.OnFrameEditor with a Harmony postfix to get the earliest point of every flight frame's ImGui frame, one that still runs when the player hides the UI and that StarMap's own GUI hooks do not offer
mod: ksa-aircrafthud
category: patching
techniques: [harmony-postfix, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:immediate-load, frame:viewports]
game_types: [KSA.Program, KSA.CameraMode]
tags: [harmony, postfix, frame-order, imgui, drawui, hidden-ui, lifecycle, program]
---

The HUD has to be drawn on every flight frame, including frames where the player has hidden the game
UI with the `ToggleUi` key, and its window should sit behind every game window. None of StarMap's
GUI hooks fits: `[StarMapBeforeGui]` and `[StarMapAfterGui]` are skipped with the UI hidden, and
`[StarMapAfterOnFrame]` fires after `ImGui.Render`. AircraftHUD instead puts a Harmony postfix on the
private `Program.OnFrameEditor(double)`, which `Program.OnFrame` calls right after `ImGui.NewFrame`
and outside the `DrawUI` checks.[^ah-main] [^ksa-program]

## How it works

1. **Patch class.** `ProgramPatches` is a `[HarmonyPatch(typeof(Program))]` class whose postfix is
   bound to the method by name with `[HarmonyPatch("OnFrameEditor")]`, because the target is private
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341)).
   The postfix parameter is named `dtPlayer` like the target's parameter, so Harmony passes the
   frame's player delta time through (Harmony binds patch arguments by name; library behaviour).
2. **Registration.** `Init` (`[StarMapImmediateLoad]`) stores the instance in `HUD.Instance`, then
   calls `harmony.PatchAll(typeof(HUD).Assembly)` with the Harmony id `AircraftHUD`
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L669)).
   StarMap runs `Init` from its prefix on `Mod.PrepareSystems`
   ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)),
   inside the `Program` constructor and long before the first `OnFrame`.[^starmap-modpatches]
3. **Forwarding.** The postfix calls `HUD.Instance?.DrawHud(dtPlayer)`. `DrawHud` gates itself: it
   returns when the HUD is disabled, the camera is `Map` or `Free`, or there is no controlled vehicle
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L967)).
4. **What the target does.** In flight `OnFrameEditor` is close to empty: it creates a
   `VehicleEditor` only when `EditorFlag` is set and the editor does not exist yet (and sets
   `ControlledVehicle` to null when it does), then calls `Editor?.OnFrame`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578)).
   The postfix therefore behaves like a hook placed between `PrepareImGui` and `OnFrameViewports`.

## Lifecycle and timing

Position inside `Program.OnFrame` in build 2026.10.10.5554
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286)):

| Order | Call | Relation to the postfix |
|---|---|---|
| 1 | `PrepareFrame`: wait for and apply the solvers, poll input, dispatch the next physics step | Before. If it returns `Exit` (font rebuild, window close), `OnFrame` returns and the postfix does not run that frame |
| 2 | `GaugeCanvas.UpdateCanvasesOnFrame`, `GizmosRenderer.ResetInstances`, `PrepareImGui` (`ImGui.NewFrame`) | Before |
| 3 | `OnFrameEditor` and **the postfix** | `frame:viewports` |
| 4 | `OnFrameViewports` (controllers and cameras move) | After |
| 5 | `OnDrawUiFrame` (`StarMapBeforeGui` prefix), `OnDrawUiViewports` (`StarMapAfterGui` postfix), `OnDrawUiThreadSafe` (menu bar window, vehicle windows) | After; all three are skipped when `DrawUI` is false |
| 6 | `DrawFps`, `OnDrawUiConsole`, `ImGui.Render` | After |
| 7 | Gauges, lights, celestials, `OnPreRender`, `Render`, `PostRender`, then StarMap's `StarMapAfterOnFrame` postfix | After |

What that position means for code in the postfix:

- **ImGui calls are legal.** `ImGui.NewFrame` has run and `ImGui.Render` has not.
- **It runs with the UI hidden.** `OnFrameEditor` sits outside both `if (DrawUI)` blocks, unlike the
  StarMap GUI hooks ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L36)).[^starmap-programpatcher]
- **Vehicle state is readable, not writable.** The main-thread `Vehicle` holds the step applied in
  this frame's `PrepareFrame`, and it does not change until the next frame's `ApplyVehicleSolvers`.
  The next step was already dispatched to the worker threads before the postfix runs, like every
  StarMap frame hook, so writes to simulated state here race the workers and are overwritten at the
  next apply. AircraftHUD only reads.
- **The camera is one frame behind.** Cameras move in `OnFrameViewports`, after the postfix. The mode
  from `Program.GetCameraMode()` (the frame viewport's `Mode`) is already current, because mode
  switches come from key input handled in `PrepareFrame` or from UI clicks in an earlier frame. A mod that projects world points to the screen would use last
  frame's camera here and should hook after `OnFrameViewports` instead.
- **It also runs outside flight.** The call precedes the `IsLoaded` check, so the postfix runs on the
  main menu and in the editor. AircraftHUD is safe there only because `ControlledVehicle` is null:
  when the editor opens, the editor branch of `OnFrameEditor` nulls it before the postfix runs.
- **Window order.** The HUD window is the first game window begun in the frame. It uses
  `NoBringToFrontOnFocus` and `NoInputs`; Dear ImGui places such a window at the back of the display
  order when it is created (library behaviour), so it stays behind the menu bar and vehicle windows
  and never takes the mouse.
- **Shutdown.** The mod has no `[StarMapUnload]` method. `StarMapCore.DeInit` calls the id-less
  `Harmony.UnpatchAll()`, which removes this postfix with every other patch in the process
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L34)).[^starmap-core]

Alternatives for UI that must survive a hidden HUD:

| Hook | Runs with UI hidden | Inside the ImGui frame | Order and camera |
|---|---|---|---|
| Postfix on `Program.OnFrameEditor` (this mod) | Yes | Yes | Before every game window; camera not yet moved |
| Postfix on `Program.OnFrameViewports` | Yes | Yes | Before every game window; camera moved for this frame |
| `[StarMapBeforeGui]` / `[StarMapAfterGui]` | No | Yes | Around `OnDrawUiFrame` / `OnDrawUiViewports`, before the menu bar and vehicle windows |
| Hook on `Program.OnDrawUiConsole` | Yes | Yes | After every game window |
| `[StarMapAfterOnFrame]` | Yes | No (after `ImGui.Render`) | After the render; ImGui calls land in the next frame or nowhere |

## Key code

| Path | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | Harmony postfix on `Program.OnFrameEditor` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L669) | `Init`: instance and `PatchAll` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L967) | Per-frame gate in `DrawHud` |

## Snippets

```csharp
// AircraftHUDMain.cs L1332-L1341
    [HarmonyPatch(typeof(Program))]
    internal static class ProgramPatches
    {
        [HarmonyPatch("OnFrameEditor")]
        [HarmonyPostfix]
        private static void AfterOnFrameViewports(double dtPlayer)
        {
            HUD.Instance?.DrawHud(dtPlayer);
        }
    }
```

```csharp
// current/decomp/KSA/Program.cs L2260-L2276
		GizmosRenderer.ResetInstances();
		PrepareImGui(dtPlayer);
		OnFrameEditor(dtPlayer);
		OnFrameViewports(dtPlayer);
		Profiler.MainThread.Begin(Profiler.Tag.DrawUi);
		if (DrawUI)
		{
			OnDrawUiFrame(dtPlayer);
			OnDrawUiViewports(dtPlayer);
		}
		if (DrawUI)
		{
			OnDrawUiThreadSafe(dtPlayer);
		}
		DrawFps();
		OnDrawUiConsole(dtPlayer);
		Profiler.MainThread.End();
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnFrameEditor(double dtPlayer)` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) | Postfix target |
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286) | Call order around the target |
| `Program.PrepareFrame` early `Exit` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2222-L2235) | Frames on which the postfix does not run |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L540) | The flag the postfix is not subject to |
| `Program.GetCameraMode()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L651-L654) | Frame viewport's camera mode |

## Pitfalls and version notes

- **Private, string-named target.** A rename or signature change of `OnFrameEditor` in a game update
  makes `PatchAll` fail. Harmony reports a missing target by throwing (library behaviour), and
  neither StarMap's `PrepareSystems` prefix nor `ModLibrary.PrepareAll` catches it, so the failure
  surfaces at startup rather than as a silently missing HUD.
- **Misleading name.** The postfix method is called `AfterOnFrameViewports`, but it targets
  `OnFrameEditor`, which runs before `OnFrameViewports`. Harmony only uses the attribute.
- **Skipped frames.** On a font-rebuild frame `PrepareFrame` returns `Exit` before the postfix, so
  the HUD is missing for that one frame and per-frame state the mod updates there (the `FrameTime`
  accumulator, the post-process push constants) is not updated.
- **Exceptions are fatal.** Code in the postfix runs directly on the game loop. `App.Run`
  ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L38-L72)),
  `Program.Main` and StarMap's entry-point call
  ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L55))
  have no `try`/`catch`, so an exception thrown here ends the game.[^ksa-app] [^starmap-surveyer] See the parent-body
  cast in [Surface attitude and air data from the controlled vehicle](flight-data-from-vehicle.md#pitfalls-and-version-notes).

## See also

- [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md)
- [Surface attitude and air data from the controlled vehicle](flight-data-from-vehicle.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)
- [HarmonyPatch attribute with a string method name, applied by PatchAll](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-1.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^starmap-programpatcher]: StarMap ProgramPatcher.cs
[^starmap-modpatches]: StarMap ModPatches.cs
[^starmap-core]: StarMap StarMapCore.cs
[^starmap-surveyer]: StarMap GameSurveyer.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
