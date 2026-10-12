---
type: How-To
title: HarmonyPatch attribute with a string method name, applied by PatchAll
description: Hook the private Program.OnFrameEditor, which runs every flight frame even with the HUD hidden, with a [HarmonyPatch("OnFrameEditor")] postfix on a [HarmonyPatch(typeof(Program))] class whose parameter is named like the target's so Harmony passes the frame delta
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/always-on-onframeeditor-hook]
techniques: [harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:immediate-load, frame:viewports]
game_types: [KSA.Program]
tags: [harmony, postfix, private-method, string-name, patchall, hud-hidden]
---

A private method cannot be written as `typeof(Program).OnFrameEditor` or `nameof(Program.OnFrameEditor)` from outside the class, so the attribute names it with a string. KSA_AircraftHUD uses the simplest
form: a `[HarmonyPatch(typeof(Program))]` class with a postfix marked `[HarmonyPatch("OnFrameEditor")]`, applied with `PatchAll`. The target matters as much as the technique: `OnFrameEditor` runs on every
frame right after `ImGui.NewFrame`, outside the `DrawUI` guards, so a HUD drawn from it keeps drawing when the player hides the game UI.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSA_AircraftHUD in [Always-on ImGui hook from a Program.OnFrameEditor postfix](../../../../mods/ksa-aircrafthud/features/always-on-onframeeditor-hook.md).

## Background: how the game does it

`Program.OnFrame` calls `OnFrameEditor` right after `PrepareImGui` and before `OnFrameViewports`, and outside the `if (DrawUI)` blocks that wrap the StarMap GUI hooks:

```csharp
// current/decomp/KSA/Program.cs L2260-L2273
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
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2273)[^ksa-program]

In flight the method is nearly empty: it creates a `VehicleEditor` only when `EditorFlag` is set and none exists, then calls `Editor?.OnFrame`:

```csharp
// current/decomp/KSA/Program.cs L2567-L2578
private void OnFrameEditor(double dtPlayer)
{
	if (EditorFlag && Editor == null)
	{
		Editor = new VehicleEditor(ControlledVehicle, newVehicle: false);
		ControlledVehicle = null;
		Universe.SetSimulationSpeed(0.0);
	}
	Profiler.MainThread.Begin(ProfilerOnFrameTag);
	Editor?.OnFrame(MainViewport, dtPlayer);
	Profiler.MainThread.End();
}
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578)

## Lifecycle and timing

Install: `[StarMapImmediateLoad]` (`startup:immediate-load`), which StarMap runs from its prefix on `Mod.PrepareSystems`, inside the `Program` constructor and long before the first `OnFrame`. Run: every frame
in `frame:viewports`, between `PrepareImGui` and `OnFrameViewports`. From the lifecycle reference ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)):

| Property | At the postfix |
|---|---|
| ImGui calls | legal (`NewFrame` has run, `Render` has not) |
| HUD hidden (`DrawUI` false) | still runs; `OnFrameEditor` is outside both `if (DrawUI)` blocks |
| Vehicle state | readable, not writable (the next physics step is already on the workers) |
| Camera | one frame behind: cameras move in `OnFrameViewports`, after this method |
| Outside flight | also runs on the main menu and in the editor |
| If `PrepareFrame` returns `Exit` | `OnFrame` returns and the postfix does not run that frame |

## Steps

1. **Declare a patch class for the type and a postfix with the method name as a string.** The class-level `[HarmonyPatch(typeof(Program))]` gives the type; the method-level `[HarmonyPatch("OnFrameEditor")]`
   gives the name; `[HarmonyPostfix]` the kind. Name the postfix parameter like the target's (`dtPlayer`) so Harmony passes the frame's player delta through:

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

Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341)[^ksa-aircrafthud-aircrafthudmain]

   (The method's own name, `AfterOnFrameViewports`, is irrelevant to Harmony; it is a leftover from an earlier target.)

2. **Apply from `StarMapImmediateLoad`.** The mod stores the instance, creates the Harmony instance and calls `PatchAll` on its assembly:

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

Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L669)

3. **Forward to your code and let it gate itself.** `DrawHud` returns when the HUD is disabled, when the camera is `Map` or `Free`, or when there is no controlled vehicle:

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

Source: [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L967)

4. **Know the failure mode.** A name that matches nothing makes Harmony throw at patch time. A local test with a prefix on `Controller.OnFrame` named a parameter that does not exist:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
parameter named doesNotExist (no such parameter): Exception / Parameter "doesNotExist" not found in method System.Void Controller::OnFrame(System.Double dt)
```

   With `PatchAll`, that exception stops the remaining patch classes from being applied ([CreateClassProcessor per patch class, required versus optional](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)); neither StarMap's `PrepareSystems` prefix nor
   `ModLibrary.PrepareAll` catches it, so it surfaces at startup.

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L660-L669) | Install |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | The postfix |

## Variations and gotchas

- **Alternatives for UI that must survive a hidden HUD.** A postfix on `Program.OnFrameViewports` runs with the UI hidden too and sees the camera after it moved; `[StarMapBeforeGui]` and `[StarMapAfterGui]` do not run
  with the UI hidden; a hook on `OnDrawUiConsole` runs after every game window; `[StarMapAfterOnFrame]` fires after `ImGui.Render`.
- **Window order.** The HUD window is the first game window begun in the frame; with `NoBringToFrontOnFocus` and `NoInputs` it stays behind the menu bar and vehicle windows and never takes the mouse.
- **Shutdown.** The mod has no `[StarMapUnload]`; StarMap's id-less `UnpatchAll()` removes the postfix ([Removing Harmony patches safely at runtime and at exit](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/overview.md)).
- **A renamed method fails loudly at startup** with `PatchAll`; use the manual lookup in [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md) if the hook is optional.
- **Camera consumers.** A mod that projects world points to the screen would use last frame's camera here and should hook after `OnFrameViewports`.

## See also

- [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [Attribute argument types that bind by assignability](patch-a-private-game-method-by-name-4.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [Always-on ImGui hook from a Program.OnFrameEditor postfix](../../../../mods/ksa-aircrafthud/features/always-on-onframeeditor-hook.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) for Harmony patches on `Program` and `Universe`
- Related topic: draw-ui-while-the-hud-is-hidden

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-aircrafthud-aircrafthudmain]: KSA_AircraftHUD/AircraftHUDMain.cs
