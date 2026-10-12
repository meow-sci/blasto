---
type: How-To
title: "Keep drawing when the HUD is hidden: Harmony postfix on Program.OnFrameEditor"
description: Hang a per-frame ImGui draw on the private Program.OnFrameEditor, which runs after NewFrame and outside the DrawUI gate, so it survives F2
category: ui
group: overlays
topic: draw-ui-while-the-hud-is-hidden
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/fullscreen-drawlist-hud]
techniques: [harmony-postfix, imgui-drawlist]
hooks: [harmony-postfix KSA.Program.OnFrameEditor, starmap StarMapImmediateLoad]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Program]
tags: [hud-hidden, f2, harmony, onframeeditor, overlay, hook]
---
The player hides the game UI with F2 to record clean footage, and every StarMap GUI hook (`[StarMapBeforeGui]`, `[StarMapAfterGui]`) stops, because the game calls their targets only inside `if (DrawUI)`. KSA_AircraftHUD wants its flight HUD
to stay up exactly then. It puts a Harmony postfix on the private `Program.OnFrameEditor(double)`, which `Program.OnFrame` calls right after `ImGui.NewFrame` and outside the `DrawUI` checks. This is the simplest way
to get an always-on ImGui hook; its cost is that the camera has not moved yet. Demonstrated by KSA_AircraftHUD. Prerequisites: StarMap and Harmony.

## Background: how the game does it

`Program.OnFrame` (build 2026.10.10.5554), UI section:

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

- `PrepareImGui` starts the ImGui frame, then `OnFrameEditor`, then `OnFrameViewports`; both of those and `DrawFps` and `OnDrawUiConsole` are outside the `if (DrawUI)` blocks. Only `OnDrawUiFrame`, `OnDrawUiViewports` and
  `OnDrawUiThreadSafe` are gated, and StarMap's GUI attributes are a prefix on the first and a postfix on the second.
- In flight `OnFrameEditor` is close to empty: it creates a `VehicleEditor` only when `EditorFlag` is set and none exists, then calls `Editor?.OnFrame`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578)[^ksa-program]
- If `PrepareFrame` returns `Exit` (window close, font rebuild) `OnFrame` returns before this section and the postfix does not run that frame.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2250)[^ksa-program]

## Lifecycle and timing

The postfix runs at `frame:viewports` (before `OnFrameViewports`, so cameras have not moved for this frame), inside `NewFrame`..`Render`:

| Order | Call | Relation |
|---|---|---|
| 1 | `PrepareFrame`: apply the solvers, poll input, dispatch the next physics step | Before |
| 2 | `GizmosRenderer.ResetInstances`, `PrepareImGui` | Before |
| 3 | `OnFrameEditor` **and the postfix** | `frame:viewports` |
| 4 | `OnFrameViewports` | After; cameras move |
| 5 | `OnDrawUiFrame`, `OnDrawUiViewports`, `OnDrawUiThreadSafe` | After; skipped with the HUD hidden |
| 6 | `DrawFps`, `OnDrawUiConsole`, `ImGui.Render` | After |

- ImGui calls are legal. Vehicle state is readable but not writable: it holds the step applied this frame, and the next step is already running on workers.
- The mode from `Program.GetCameraMode()` is already current; the camera pose is one frame behind, so a mod that projects world points should use a hook after `OnFrameViewports` instead.
- The call precedes the `IsLoaded` check, so the postfix also runs on the main menu and in the editor; AircraftHUD is safe because `ControlledVehicle` is null there.
- The window begun here is the first game window of the frame; with `NoBringToFrontOnFocus` and `NoInputs` it stays behind the menu bar and vehicle windows.

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Register the instance and patch from the `StarMapImmediateLoad` method**, before any frame runs:

```csharp
// AircraftHUDMain.cs L660-L669
[StarMapImmediateLoad]
unsafe public void Init(Mod definingMod)
{
    Console.WriteLine("AircraftHUD: Init");

    Instance = this;

    harmony = new Harmony("AircraftHUD");

    harmony.PatchAll(typeof(HUD).Assembly);
```

2. **Bind the private method by name.** The postfix parameter is named `dtPlayer` like the target's so Harmony passes the frame delta through:

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

3. **Gate inside the draw.** Return when the feature is disabled, the camera is in `Map` or `Free` mode, or there is no controlled vehicle:

```csharp
// AircraftHUDMain.cs L956-L967
unsafe public void DrawHud(double dt)
{
    //DefaultCategory.Log.Warning("AircraftHUD: DrawHud");

    if (!HUD.enabled) { return; }

    // HUD should only render in orbit mode
    CameraMode CamMode = Program.GetCameraMode();
    if (CamMode.Equals(CameraMode.Map) || CamMode.Equals(CameraMode.Free)) { return; }

    Vehicle controlledVehicle = Program.ControlledVehicle;
    if (controlledVehicle == null) { return; }
```

4. **Draw into a full-screen input-less window** ([approach 1 of the overlay topic](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md)).

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | Harmony postfix on `Program.OnFrameEditor` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L669) | `Init`: instance and `PatchAll` |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L967) | Per-frame gate in `DrawHud` |

## Variations and gotchas

- **Private target.** `OnFrameEditor` is an implementation detail; a rename means no HUD. The postfix is named `AfterOnFrameViewports` but targets `OnFrameEditor`; both methods exist in 2026.10.10.5554 and `OnFrameEditor` runs first.
- **Shutdown.** The mod has no `[StarMapUnload]`: `StarMapCore.DeInit` calls the id-less `Harmony.UnpatchAll()`, which removes every patch in the process.
- **Alternatives** (all outside the `DrawUI` gate): a postfix on `Program.OnFrameViewports` (cameras moved), `Program.DrawFps` ([approach 2](draw-ui-while-the-hud-is-hidden-2.md)), a prefix on `Program.OnDrawUiConsole` ([approach 3](draw-ui-while-the-hud-is-hidden-3.md)).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-ui-while-the-hud-is-hidden](overview.md)
- [Approach 2: reflection-installed postfix on Program.DrawFps](draw-ui-while-the-hud-is-hidden-2.md)
- [Full-screen overlay: NoInputs/NoBackground window](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md)
- [Patching private and overloaded game methods by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- Mod feature: [Fullscreen draw-list HUD](../../../../mods/ksa-aircrafthud/features/fullscreen-drawlist-hud.md),
  [Always-on OnFrameEditor hook](../../../../mods/ksa-aircrafthud/features/always-on-onframeeditor-hook.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
