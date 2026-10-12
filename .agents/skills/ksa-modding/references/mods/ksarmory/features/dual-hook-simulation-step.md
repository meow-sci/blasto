---
type: Mod Feature
title: One simulation step per frame from whichever hook runs first
description: A frame latch hands the mod's step to the StarMap GUI postfix when the HUD is drawn, to a by-name Harmony prefix on Program.OnFrameCelestials when F2 or a screenshot hides it, and to the OnFrame postfix as a last resort
mod: ksarmory
category: patching
techniques: [starmap-lifecycle, harmony-prefix, reflection, frame-latch, graceful-degradation]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.Program.OnFrameCelestials
  - reflection KSA.Program.OnFrameCelestials
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Program, KSA.ScreenshotCapture, KSA.GizmosRenderer, KSA.GameViewport, KSA.Camera]
tags: [frame-order, starmap, harmony, prefix, drawui, f2, screenshot, latch, fallback]
---

KSArmory's simulation (rounds, fire control, cameras) must advance exactly once per frame and must
do it before the render, so that a round's subpart body and the overlay drawn against it share an
epoch with the craft KSA is about to draw. The natural home is the `[StarMapAfterGui]` hook, but
StarMap's GUI hooks sit inside KSA's `if (DrawUI)` guard, and the player hides the HUD with F2. So
the step runs through `StepOnce`, which three hooks may call and only the first one each frame
gets: a `FrameLatch` claimed by the caller and released unconditionally by the OnFrame postfix.

With the HUD shown the step runs in the GUI postfix. With it hidden it runs in a Harmony prefix on
the private `Program.OnFrameCelestials`, the last un-guarded phase before `OnPreRender`. If that
patch cannot be installed, the OnFrame postfix steps instead, one render late.[^ksarmory-mod][^ksarmory-prerenderhook]

## How it works

| Piece | What it does |
|---|---|
| `FrameLatch` | A bool. `Claim()` returns true to the first caller after each `EndFrame()`, false after; `EndFrame()` clears it.[^ksarmory-framelatch] |
| `StepOnce(dt)` | `if (!_frame.Claim()) return;` then `KsaWorld.BeginFrame()`, consume a pending save-load reset, `StepSimulation` (only while `KsaWorld.InFlightScene`), `DriveCameras`, and world clicks when the UI did not draw |
| `OnAfterGui` | Calls `StepOnce` first, then draws the panel and overlays against the state it produced |
| `PreRenderHook.BeforeOnFrameCelestials` | Calls the stored `Action<double>` (`StepOnce`); a no-op on frames the GUI already claimed |
| `OnAfterFrame` | Calls `StepOnce` (normally a no-op), then `_frame.EndFrame()` in `finally` |
| `KsaWorld.UiDrawn` | `Program.DrawUI`; decides whether `StepOnce` services world clicks itself[^ksarmory-ksaworld] |

Installing the prefix (from `[StarMapAllModsLoaded]`):

1. `AccessTools.Method(typeof(Program), "OnFrameCelestials", [typeof(double)])`: the target is
   private, so it is found by name and a compiler cannot check it.
2. If null, record `Trouble`, log `stepping after the render on frames with no UI`, and return. The
   mod keeps working through the OnFrame postfix.
3. Otherwise patch with its own Harmony id (`com.kesslersystems.ksarmory.prerender`).
4. The prefix catches any exception, nulls the delegate (so it never throws twice from inside the
   engine) and logs once: "the frame postfix takes it from here".

A **prefix**, not a postfix, because `OnFrameCelestials` reads the camera to pick the nearby body
and run the planet renderer's `OnFrame`. Stepping after it would prepare terrain against a camera
the mod then moves (seen in play as the ground rendering as open water on F2 during a chase); with
the HUD shown the mod steps before it too, so only a prefix makes the two paths identical.[^ksarmory-prerenderhook]

## Lifecycle and timing

Which hook steps, per frame type (Program.cs, build 5554):[^ksa-program]

| Frame | `StepOnce` runs in | Position in `Program.OnFrame` |
|---|---|---|
| HUD drawn (`DrawUI` true) | `OnAfterGui` | postfix on `OnDrawUiViewports`, L2268, inside `if (DrawUI)` L2265-L2269 |
| HUD hidden: F2 (`ToggleUi`, Program.cs L1800-L1803, bound to F2 at Input.cs L202) or a HUD-less screenshot | prefix on `OnFrameCelestials` | L2320, after `ImGui.Render` (L2286) and before `OnPreRender` (L2323) |
| HUD hidden and the prefix refused or stood down | `OnAfterFrame` | postfix on `OnFrame`, after `PostRender` (L2331) and `FrameNumber++` (L2332) |
| `PrepareFrame` returned `Exit` (window closing, font rebuild, L2223-L2234) | `OnAfterFrame` | `OnFrame` returns at L2243-L2248; the postfix still runs |

`ScreenshotCapture.OnPrepareFrame` (called at Program.cs L2149) saves `DrawUI` and sets it false for a
capture without the HUD (ScreenshotCapture.cs L228-L232), restoring it at L425 or L450.[^ksa-screenshot]
F2 is handled in the GLFW key callback during `Glfw.PollEvents` (L2179), so the guard at L2265 sees
the new value in the same frame.[^ksa-input]

What is true at each stepping point:

| | GUI postfix (L2268) | `OnFrameCelestials` prefix (L2320) | OnFrame postfix |
|---|---|---|---|
| Physics applied this frame (`ApplyVehicleSolvers`, L2167) | yes | yes | yes |
| Next step already queued on workers (`ExecuteNextVehicleSolvers`, L2220) | yes | yes | yes |
| Gizmo instances reset (L2260), render still ahead | yes | yes | no: anything submitted is reset before the next render |
| ImGui frame open (`PrepareImGui` L2261 to `ImGui.Render` L2286) | yes | no | no |
| This frame's camera matrices built (`OnFrameViewports` L2263 -> `GameViewport.OnFrame` -> `Camera.OnFrame`) | yes | yes | yes |
| Subpart transforms written now are drawn this frame | yes | yes | no, next frame |

`OnFrameViewports` runs each viewport's controller and then `Camera.OnFrame`, which builds the
view and view-projection matrices (GameViewport.cs L116-L124, Camera.cs L487-L496), so a camera pose
the step writes in either pre-render hook is rendered from the next frame's matrices. Both paths
sit on the same side of that and of `OnFrameCelestials`, which is the point.[^ksa-gameviewport][^ksa-camera]
Between L2268 and L2320 nothing moves a vehicle or a celestial (`OnDrawUiThreadSafe`, gauges,
`OnFrameController` (cursor mode only, L2520-L2550), the light system); the exception is a save
load started by `ProfilerCapture.OnFrame` (L2295) or by the console `load` command, see
[save-load detection](./detect-save-load-ongameloaded.md).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FrameLatch.cs) | The latch |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L307-L355) | `StepOnce` and the reasoning for two hooks |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L509-L524) | `OnAfterGui` steps before drawing |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L268-L305) | `OnAfterFrame`: fallback step, latch release in `finally` |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | By-name prefix on `Program.OnFrameCelestials`, degrade path |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L150-L151) | `UiDrawn => Program.DrawUI` |

## Snippets

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L324-L355
    private void StepOnce(double dtPlayer)
    {
        if (_disabled || _roster is null) return;
        if (!_frame.Claim()) return;

        KsaWorld.BeginFrame();
        KsaWorld.AuditBodiesOnce();

        // Before anything integrates: the roster still holds the previous world's systems, and one
        // more step of them is one more step of rounds that no longer have a world to fly in.
        if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();

        // ...
        if (KsaWorld.InFlightScene)
        {
            using (_budget.Measure("sim", top: true)) StepSimulation(dtPlayer);
        }

        // ...
        using (_budget.Measure("cameras")) DriveCameras(dtPlayer);

        // The UI pass reads clicks when it runs; with the UI hidden it does not run at all.
        if (!KsaWorld.UiDrawn && KsaWorld.InFlight) UpdateWorldClicks();
    }
```

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L62-L91
        try
        {
            // Private, so by name: there is no way to write this that a compiler would check, which
            // is exactly why the failure has to degrade rather than break.
            MethodInfo? target = AccessTools.Method(typeof(Program), "OnFrameCelestials",
                                                    [typeof(double)]);

            if (target is null)
            {
                Trouble = "KSA has no Program.OnFrameCelestials(double) to hook";
                Log.Warn($"stepping after the render on frames with no UI: {Trouble}");
                return;
            }

            MethodInfo prefix = typeof(PreRenderHook).GetMethod(
                nameof(BeforeOnFrameCelestials), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Installed = true;
            Trouble = "";
            Log.Info("hidden-UI frames step before the render, via Program.OnFrameCelestials");
        }
        catch (Exception e)
        {
            Trouble = e.Message;
            Log.Warn($"could not hook the pre-render step; frames with no UI will be drawn one "
                     + $"frame late ({e.Message})");
        }
```

```csharp
// current/decomp/KSA/Program.cs L2260-L2275
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
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program.OnFrameCelestials(double)` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) | Prefixed; resolves the camera's nearby body and runs `PlanetRenderer.OnFrame` |
| `Program.DrawUI` (static property) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L539) | Read as `KsaWorld.UiDrawn`; the setter also clears timed alerts when hiding |
| `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176) | StarMap's `AfterGui` postfix target[^starmap-programpatcher] |
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | StarMap's `AfterOnFrame` postfix target |
| `ScreenshotCapture.OnPrepareFrame` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210-L240) | The non-F2 way `DrawUI` goes false |
| `GizmosRenderer.ResetInstances` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260) | Why drawing must happen between L2260 and the render |

## Pitfalls and version notes

- **The latch must be released on the hook that cannot be skipped.** A release on the GUI hook would
  leave the latch set on the first F2 frame and stop the mod for the session; `EndFrame` sits in the
  OnFrame postfix's `finally`, ahead of every early return.[^ksarmory-framelatch]
- **The hidden-UI paths cannot submit ImGui.** `ImGui.Render` (L2286) has run before L2320; that is
  why `StepOnce` only reads clicks there (`UpdateWorldClicks`) and draws nothing.
- **UI-pass work stops with F2.** `Ui.Draw` (inside `OnAfterGui`) is what calls `_roster.Sync`, so a
  craft spawned while the HUD is hidden is not crewed until it comes back.[^ksarmory-ui]
- **A refused prefix costs one frame of lag, not correctness**: a step after the render puts every
  round a step behind the craft it is drawn against on hidden-UI frames (about 4 m at 250 m/s).
- **`if (!IsLoaded) return;` (L2288) is ahead of `OnFrameCelestials`**, but `IsLoaded` is set at the
  end of the `Program` constructor (L1317) and never cleared in 5554, so the running game never takes it.

`docs/KSA-FRAME-ORDER.md` (build 2026.8.22.5348) against build 2026.10.10.5554, for this hook:[^ksarmory-frame-order]

| Doc claim | 5554 verdict |
|---|---|
| §1 frame table: gizmo reset, ImGui, viewports, GUI hooks, gauges, controller, celestials, render, `FrameNumber++`, then `AfterOnFrame` | Holds in order; every `Program.cs` line number moved (now L2238-L2334) |
| §1 #10 `Cursor.UpdateInputRay` after `LightSystem.OnFrame` | Changed: no `UpdateInputRay` exists in 5554; `Cursor.SetDesktopPosition` is in `PrepareImGui` (L2138), `Cursor.UpdateLastPosition` in `PostRender` (L2361) |
| §1 PrepareFrame table | Changed: `ExplosionFlashSystem.Update(GetNextSimStep().DeltaTime)` at the top (L2150); waits on `NearestOrbitAndPerformanceWorker` and `ClothSolvers` added (L2151-L2162); `ApplyClothSolvers` (L2170); `Glfw.PollEvents` now L2179, after the `Apply*Solvers` and before `ApplyInputEvents` (L2181), not after `Network.Tick`; `ExecuteNextClothSolvers` and `PartTree.FlushDirty*` before the vehicle dispatch (L2217-L2219) |
| §7 `BeforeGui`/`AfterGui` skipped when `DrawUI` is false | Holds (L2265-L2269) |
| §7 screenshot sets `DrawUI` false | Holds (ScreenshotCapture.cs L228-L232, restored L425/L450) |
| §7 "the debug key at Program.cs:1696 toggles it" | Holds as the `ToggleUi` action, F2 (Program.cs L1800-L1803, Input.cs L202) |
| §7 `PrepareFrame` `Exit` returns after `ApplyVehicleSolvers` and `ExecuteNext*Solvers` | Holds (L2223-L2234) |
| §7 early return "ahead of #9" | Present (L2288) but unreachable once loaded |
| §7 `FrameNumber` counts frames that reached the end of the render | Holds (field L301, increment L2332 past three returns) |
| §7 KSA particles stepped by `GetNextSimStep().DeltaTime` | Holds (L2409-L2417, GPU update L4850) and now also `ExplosionFlashSystem` (L2150) |
| §1 #11 `OnFrameCelestials` moves nothing | Holds (L2701-L2732) |
| §9 #3 mod update is a postfix on `OnDrawUiViewports`, after `OnFrameViewports` | Holds (ProgramPatcher.cs L26-L36; L2263 before L2268) |

## See also

- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Chase camera on a round](./chase-camera-on-a-round.md)
- [Round body draw hooks](./round-body-draw-hooks.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Manual AccessTools lookup with a degrade path when the method is gone](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-2.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature

[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-framelatch]: src/KSArmory/Sim/FrameLatch.cs
[^ksarmory-prerenderhook]: src/KSArmory/Ksa/PreRenderHook.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-ui]: src/KSArmory/Ksa/Ui/Ui.cs
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-screenshot]: KSA.ScreenshotCapture (decompiled)
[^ksa-input]: KSA.Input (decompiled)
[^ksa-gameviewport]: KSA.GameViewport (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
