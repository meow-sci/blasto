---
type: Mod Feature
title: Frame lifecycle hooks (BeforeGui, AfterGui, AfterOnFrame)
description: StarMap's per-frame hooks are a prefix on Program.OnDrawUiFrame, a postfix on Program.OnDrawUiViewports and a postfix on Program.OnFrame; this maps each to its position in the KSA frame and the state that is valid there
mod: starmap
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.Program.OnDrawUiFrame
  - harmony-postfix KSA.Program.OnDrawUiViewports
  - harmony-postfix KSA.Program.OnFrame
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.App, KSA.Universe]
tags: [starmap, lifecycle, frame, imgui, harmony, timing, hud]
---

Most StarMap mods do their per-frame work in `[StarMapBeforeGui]` or `[StarMapAfterGui]`, and a
few use `[StarMapAfterOnFrame]`. The names suggest "before the UI" and "after the UI", but the
attach points are two specific private methods of `KSA.Program`, and the game draws a large part
of its UI after `StarMapAfterGui` returns. This page places each hook in the decompiled frame.

## How it works

`ProgramPatcher` is a `[HarmonyPatch(typeof(Program))]` class applied by StarMap's `PatchAll`.
The attribute classes only validate the signatures listed here.[^sm-programpatcher][^sm-ongui][^sm-onframe]

| Hook | Harmony patch | Arguments passed |
|---|---|---|
| `StarMapBeforeGui(double dt)` | Prefix on `Program.OnDrawUiFrame(double dt)` | `dt` |
| `StarMapAfterGui(double dt)` | Postfix on `Program.OnDrawUiViewports(double dt)` | `dt` |
| `StarMapAfterOnFrame(double currentPlayerTime, double dtPlayer)` | Postfix on `Program.OnFrame(double currentPlayerTime, double dtPlayer)` | both |

The frame, from `Program.OnFrame` in build 2026.10.10.5554, with the hooks placed:[^ksa-program]

| Order | Code | Hook / lifecycle |
|---|---|---|
| 1 | `PrepareFrame`: renderer rebuild if scheduled; wait for orbit, vehicle, cloth job systems; `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers` / `ApplyClothSolvers`; `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`; `Universe.ExecuteNextClothSolvers` / `ExecuteNextVehicleSolvers` / `ExecuteNextOrbitSolvers`; `Network.Tick` | `frame:prepare` |
| 2 | `GaugeCanvas.UpdateCanvasesOnFrame` or `BurnCanvas.OnFrame`, `GizmosRenderer.ResetInstances` | |
| 3 | `PrepareImGui` (`ImGuiBackend.NewFrame`, `ImGui.NewFrame`) | `frame:imgui-new-frame` |
| 4 | `OnFrameEditor`, `OnFrameViewports` (cameras for this frame) | `frame:viewports` |
| 5 | if `DrawUI`: **prefix: StarMapBeforeGui**, then `OnDrawUiFrame` (alerts, `Popup.DrawAll`, debug windows, celestial windows, settings, `ImGuiWindow.DrawAllStaticWindows`, renderer debug UIs, statistics) | `frame:draw-ui` |
| 6 | if `DrawUI`: `OnDrawUiViewports` (ImGui for secondary viewports), then **postfix: StarMapAfterGui** | `frame:draw-ui` |
| 7 | if `DrawUI`: `OnDrawUiThreadSafe` (menu bar, vehicle patch and resource windows, gauge canvases, Esc menu, `Universe.OnDrawUi`, controller, editor and launch UI) | `frame:draw-ui` |
| 8 | `DrawFps`, `OnDrawUiConsole` (always) | `frame:draw-ui` |
| 9 | `ImGui.Render` | `frame:imgui-render` |
| 10 | if `!IsLoaded` return; gauges, controller, lights, celestials, `OnPreRender`, `Render`, `PostRender` | `frame:gauges` ... `frame:post-render` |
| 11 | **postfix: StarMapAfterOnFrame** (also after the early returns in steps 1 and 10) | `frame:after-on-frame` |

### State at each hook

**StarMapBeforeGui** (`frame:draw-ui`, before the game's own windows)

- ImGui frame is open (`PrepareImGui` ran); none of the game's `OnDrawUi*` methods has run yet
  this frame. Window stacking is decided by ImGui's focus order, not by which hook submits first.
- Input for this frame has been polled; camera state for this frame is final.
- Physics: this frame's solver results have been applied, and the next vehicle, orbit and cloth
  steps were already dispatched to worker threads in `PrepareFrame`. Reading vehicle state is
  reading the state just applied; writing simulated state races the running step until the next
  frame's `Apply*Solvers`.
- Good for: per-frame update logic, work that must finish before the game draws (unscience's
  parts-now defers texture purges here because game windows drawn later in the frame still
  reference them).[^partsnow-panelactions]

**StarMapAfterGui** (`frame:draw-ui`, after secondary-viewport UI)

- Same physics and input state as BeforeGui. The game's static windows and secondary-viewport UI
  are already submitted; the menu bar, vehicle windows, gauge canvases, Esc menu and
  `Universe.OnDrawUi` are not.
- Good for: drawing the mod's own ImGui windows and draw-list overlays. This is the most used frame
  hook in the reference mods.

**StarMapAfterOnFrame** (`frame:after-on-frame`)

- The frame has been rendered and presented, or the frame bailed out early: Harmony runs a postfix
  on every return, so the hook also fires when `PrepareFrame` returned `Exit` (window closing, font
  rebuild; no UI and no render that frame) and when `Render` returned `Exit` (failed acquire or
  pending rebuild; no present). Either way no ImGui frame is open (`ImGui.Render` already ran, or
  `NewFrame` never did), so ImGui calls are invalid until the next `NewFrame`.
- The next physics step is running on worker threads.
- Good for: telemetry sampling, bookkeeping, scheduling work for the next frame. Fires even when
  the HUD is hidden.

### Arguments

`App.Run` calls `OnFrame(totalSeconds, dtPlayer)` where `totalSeconds` is wall-clock seconds since
the frame loop started (a `Stopwatch`) and `dtPlayer` is the real frame time clamped to
`1 / GameSettings.Current.Simulation.MinTargetFrameRate`. `OnDrawUiFrame` and `OnDrawUiViewports`
receive that same `dtPlayer`. None of these values is simulation time or scaled by time warp.[^ksa-app]

## Lifecycle and timing

All three run on the main thread once per `Program.OnFrame`, starting with the first frame after
the `Program` constructor finished (`IsLoaded` is true by then), in the editor as well as in
flight. Mods run in StarMap initialization order. Each invocation is a `MethodInfo.Invoke` with
boxed `double` arguments, inside a Harmony patch, with no exception handling.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L7-L49) | All three frame patches |
| [StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L30-L75) | `StarMapBeforeGui`, `StarMapAfterGui` signatures |
| [StarMap.API/OnFrameAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnFrameAttributes.cs#L35-L45) | `StarMapAfterOnFrame` signature |

## Snippets

```csharp
// StarMap.Core/Patches/ProgramPatcher.cs L7-L36
[HarmonyPatch(typeof(Program))]
internal static class ProgramPatcher
{
    private const string OnBeforeDrawUiMethodName = "OnDrawUiFrame";
    private const string OnAfterDrawUiMethodName = "OnDrawUiViewports";
    private const string OnFrameMethodName = "OnFrame";

    [HarmonyPatch(OnBeforeDrawUiMethodName)]
    [HarmonyPrefix]
    public static void BeforeOnDrawUi(double dt)
    {
        var methods = StarMapCore.Instance?.Loader.ModRegistry.Get<StarMapBeforeGuiAttribute>() ?? [];

        foreach (var (_, @object, method) in methods)
        {
            method.Invoke(@object, [dt]);
        }
    }

    [HarmonyPatch(OnAfterDrawUiMethodName)]
    [HarmonyPostfix]
    public static void AfterOnDrawUi(double dt)
    {
        var methods = StarMapCore.Instance?.Loader.ModRegistry.Get<StarMapAfterGuiAttribute>() ?? [];

        foreach (var (_, @object, method) in methods)
        {
            method.Invoke(@object, [dt]);
        }
    }
```

```csharp
// current/decomp/KSA/Program.cs L2261-L2286
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
SequencePerformanceList sequencePerformanceList = Editor?.EditingSpace.Parts?.PerformanceSequences;
if (sequencePerformanceList != null && sequencePerformanceList.IsDirty && JobSystems.NearestOrbitAndPerformanceWorker.IsReady())
{
    _sequencePerformanceJob.Target = sequencePerformanceList;
    JobSystems.NearestOrbitAndPerformanceWorker.Queue(_sequencePerformanceJob, 1);
    JobSystems.NearestOrbitAndPerformanceWorkerProfiler.Prepare();
    JobSystems.NearestOrbitAndPerformanceWorker.ExecuteJobs();
}
Profiler.MainThread.Begin(Profiler.Tag.ImguiRender);
ImGui.Render();
```

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | Postfix target for `StarMapAfterOnFrame` |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) | Applies and re-dispatches physics before any frame hook |
| `Program.OnDrawUiFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3160) | Prefix target for `StarMapBeforeGui` |
| `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176) | Postfix target for `StarMapAfterGui` |
| `Program.OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3241) | Game UI drawn after `StarMapAfterGui` |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L539) | HUD toggle (`InputAction.ToggleUi`) that gates both GUI hooks |
| `App.Run` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | Source of `currentPlayerTime` and `dtPlayer` |

## Pitfalls and version notes

- **Hiding the HUD stops both GUI hooks.** `OnDrawUiFrame` and `OnDrawUiViewports` sit inside
  `if (DrawUI)`, so with the HUD hidden neither `StarMapBeforeGui` nor `StarMapAfterGui` fires and
  any update logic placed there freezes. `StarMapAfterOnFrame` still fires. unscience's
  `HiddenUiFrameHook` prefixes the unconditional `OnDrawUiConsole` and replays the two callbacks
  only while `DrawUI` is false.[^hiddenui]
- **"After GUI" is not after all GUI.** The menu bar, vehicle windows, gauge canvases and
  `Universe.OnDrawUi` are drawn after `StarMapAfterGui`. The API README describes the hooks as
  "just before/after `KSA.Program.OnDrawUi`", a method that no longer exists.[^sm-api-readme]
- **No ImGui in AfterOnFrame.** `ImGui.Render` has already run.
- **Exceptions end the game.** A throwing hook propagates out of the Harmony patch, out of
  `OnFrame` and out of `App.Run`.
- **Parameter names are part of the contract.** Harmony binds `dt`, `currentPlayerTime` and
  `dtPlayer` by name. KSA 2026.4.6.4036 split the former `OnDrawUi` into `OnDrawUiFrame` /
  `OnDrawUiViewports` and StarMap had to retarget the patches (commit 93cd07c, PR #71); a rename of
  these methods or their parameters in a future build breaks StarMap's `PatchAll` at startup.
- **Physics writes are racy here.** Use a vehicle-solver patch or apply changes in a
  `frame:prepare` patch if the mutation must be consistent with the running step.

## See also

- [Startup lifecycle hooks](startup-lifecycle-hooks.md)
- [Unload hook and shutdown order](unload-and-shutdown.md)
- [Discover lifecycle hooks by attribute type name](attribute-hook-discovery.md)
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature

[^sm-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^sm-ongui]: StarMap.API/OnGuiAttributes.cs
[^sm-onframe]: StarMap.API/OnFrameAttributes.cs
[^sm-api-readme]: StarMap.API README
[^hiddenui]: unscience ksa-abstractions.lib/HiddenUiFrameHook.cs
[^partsnow-panelactions]: unscience parts-now.lib/Ui/ModFolderPanelActions.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-app]: KSA.App (decompiled)
