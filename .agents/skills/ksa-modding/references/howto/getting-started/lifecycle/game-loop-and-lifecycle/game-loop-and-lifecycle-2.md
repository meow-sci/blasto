---
type: How-To
title: Hook any frame point with Harmony patches on Program and Universe
description: Reach frame points StarMap does not expose (hidden HUD, pre-render, menu bar, key input, save loads, solver results, camera, render) with Harmony patches on KSA.Program and KSA.Universe
category: getting-started
group: lifecycle
topic: game-loop-and-lifecycle
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/lib-hidden-ui-frame-hook
  - mods/unscience/features/lib-hotkey-guard
  - mods/ksa-public-telemetry/features/draw-while-ui-hidden-drawfps-postfix
  - mods/ksa-aircrafthud/features/always-on-onframeeditor-hook
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
  - mods/ksarmory/features/dual-hook-simulation-step
  - mods/ksarmory/features/detect-save-load-ongameloaded
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/purrtty/features/gate-game-input-while-terminal-focused
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
techniques: [harmony-prefix, harmony-postfix, harmony-transpiler, reflection, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Program.OnDrawUiConsole
  - harmony-postfix KSA.Program.DrawFps
  - harmony-postfix KSA.Program.OnFrameEditor
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - harmony-prefix KSA.GameSettings.OnKeyAll
  - harmony-prefix KSA.Program.OnKey
  - harmony-postfix KSA.Program.OnGameLoaded
  - harmony-postfix KSA.Universe.DeserializeSave
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-prefix KSA.GameViewport.OnFrame
  - harmony-transpiler KSA.Program.RenderGame
lifecycle:
  - frame:input
  - frame:apply-solvers
  - frame:viewports
  - frame:draw-ui
  - frame:celestials
  - frame:render
  - startup:game-loaded
  - event:key
  - event:save
game_types: [KSA.Program, KSA.Universe, KSA.GameSettings, KSA.GameViewport, KSA.InputEvents]
tags: [harmony, frame, program, universe, hidden-hud, input, save-load, menu-bar]
---

StarMap exposes three per-frame points, two of which vanish when the player hides the HUD and one of
which lands after the render. Everything else in `Program.OnFrame` is reachable with Harmony, which
StarMap ships. This article lists the frame points mods actually patch in build 2026.10.10.5554, why
each one, and how the reference mods write the patch so that a game update degrades the mod instead
of crashing the game.

Prerequisites: approach 1 (a `[StarMapMod]` class that applies patches in `StarMapAllModsLoaded`) and
Harmony 2 (`HarmonyLib`, loaded by StarMap). Demonstrated by unscience (`ksa-abstractions.lib`),
KSArmory, KSA-Public-Telemetry, KSA-MeasureTools, purrtty, KSA-AdvancedFlightComputer, gatOS and
ShaderExtensions. Changing simulated state from a patch is covered separately in approach 3.

## Background: how the game does it

`Program.OnFrame` is a fixed sequence of private and public methods on the main thread
([overview](overview.md#per-frame-sequence)). None of them raises events for mods; the only
deliberate extension point in the frame is `Program.DrawProgramMenusHook`, an empty public method the
menu bar calls after the game's own menus [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020).
Every other hook is a Harmony patch on a method the game calls at the right moment.

## Lifecycle and timing

| Frame point (lifecycle value) | Target | Kind | Runs | Used by |
|---|---|---|---|---|
| Key input (`frame:input`, `event:key`) | `GameSettings.OnKeyAll` [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435), `Program.OnKey` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754), `Program.DispatchKeyEvent` (private) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756) | prefix returning `false` to swallow | Inside `Glfw.PollEvents`, physics settled | unscience `HotkeyGuard`, purrtty |
| Solver results (`frame:apply-solvers`) | `Universe.ApplyVehicleSolvers` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | prefix (state before results), postfix (fresh results) | `PrepareFrame`, physics joined | KSA-AdvancedFlightComputer |
| Camera (`frame:viewports`) | `GameViewport.OnFrame` [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L119) | prefix / postfix | Before / after the controller and `Camera.OnFrame` build this frame's matrices | gatOS |
| Menu bar (`frame:draw-ui`) | `Program.DrawProgramMenusHook` | postfix | Inside `Program.DrawMenuBar`, only when `DrawUI` is true and the bar is not auto-hidden. The main bar is drawn from `OnDrawUiThreadSafe` when the main viewport is hovered (after `StarMapAfterGui`) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189); a hovered secondary game viewport draws its own bar from `GameViewport.DrawImGui` inside `OnDrawUiViewports` (before `StarMapAfterGui`) [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257) | KSA-MeasureTools, KSA-DeltaVMap, KSA-AdvancedFlightComputer, purrtty, unscience |
| Hidden HUD UI (`frame:draw-ui`) | `Program.OnDrawUiConsole` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3119-L3129) or `Program.DrawFps` (private static) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248) | prefix / postfix, guarded by `!Program.DrawUI` | Every frame inside the ImGui frame, also when the HUD is hidden. A postfix on the private `Program.OnFrameEditor` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) also survives a hidden HUD but submits before every game window (`frame:viewports`) | unscience `HiddenUiFrameHook`, KSA-Public-Telemetry; AircraftHUD (`OnFrameEditor`) |
| Pre-render step (`frame:celestials`) | `Program.OnFrameCelestials` (private) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) | prefix | Every frame that gets past `PrepareFrame` (not on the early-exit frames a window close or font rebuild cause, which only `StarMapAfterOnFrame` sees), after `ImGui.Render`, before `OnPreRender`; the last point outside the `DrawUI` guard before rendering | KSArmory, KSAGolf, KSAStructures |
| Render passes (`frame:render`) | `Program.RenderGame` (private) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) or the sub-renderer it calls | transpiler, or postfix on the sub-renderer | Command recording | ShaderExtensions, gatOS |
| Save loaded (`startup:game-loaded`, `event:save`) | `Program.OnGameLoaded` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606), `Universe.DeserializeSave` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430) | postfix | Wherever the load was triggered (usually UI); after the new world exists | KSArmory, KSA-MeasureTools |

Facts that decide the choice:

- `OnDrawUiFrame`, `OnDrawUiViewports` and `OnDrawUiThreadSafe` are behind `if (DrawUI)`;
  `DrawFps` and `OnDrawUiConsole` are not [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2276). `DrawUI` changes only in key
  handling inside `PrepareFrame` and in screenshot capture, which sets it in `OnPrepareFrame` and
  restores it in `OnPostRender` [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210-L232), so it is stable across one
  frame's UI phase.
- `ImGui.Render` runs before `OnFrameCelestials` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2320):
  a `frame:celestials` prefix may step simulation or submit gizmo instances, but must not call ImGui.
- Any patch on a method in `Program.OnFrame` runs after the physics step was dispatched, except those
  on `PrepareFrame` itself and the methods it calls up to `ExecuteNextVehicleSolvers`.

## Steps

1. Pick the target from the table and confirm it in the decompiled `Program.cs` of the build you
   target. Private methods have to be patched by name; prefer public ones, which the compiler checks.

2. Apply each patch on its own, inside a try/catch, so a renamed target disables one feature and not
   the mod. KSA-MeasureTools processes one patch class at a time:

```csharp
// MeasureTools/Mod.cs L32-L55
    _harmony = new Harmony("com.maxi.measuretools");
    // Apply each patch on its own so a future game change to one target does not
    // stop the other from being patched.
    ApplyPatch(typeof(Patch_MenuBar), "Measure menu");
    ApplyPatch(typeof(Patch_MouseButton), "mouse intercept");
    ApplyPatch(typeof(Patch_LoadSave), "save load reset");

    DefaultCategory.Log.Info("[MeasureTools] Loaded.");
}

private static void ApplyPatch(Type patchClass, string description)
{
    try
    {
        _harmony!.CreateClassProcessor(patchClass).Patch();
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Applied {description} patch ({patchClass.Name}).");
    }
    catch (Exception ex)
    {
        // A missing hook should not unload the mod or block the other patch.
        LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[MeasureTools] Failed to apply {description} patch: {ex}");
    }
}
```

   For a private target, resolve it with `AccessTools.Method` and degrade when it is missing, as
   KSArmory does for `OnFrameCelestials`:

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L62-L92
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
}
```

3. Draw while the HUD is hidden. Prefix `OnDrawUiConsole` and run only when `DrawUI` is false, so the
   work never runs twice in a frame. unscience replays its per-frame work there:

```csharp
// ksa-abstractions.lib/HiddenUiFrameHook.cs L42-L71
public static void Patch(Harmony harmony)
{
    _original = AccessTools.Method(typeof(Program), TargetMethodName)
        ?? throw new MissingMethodException(typeof(Program).FullName, TargetMethodName);
    _prefix = typeof(HiddenUiFrameHook).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
    harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
    Console.WriteLine("ksa-abstractions: HiddenUiFrameHook patch applied");
}

public static void Unpatch(Harmony harmony)
{
    if (_original != null && _prefix != null)
        harmony.Unpatch(_original, _prefix);
    _original = null;
    _prefix = null;
    BeforeGui = null;
    AfterGui = null;
    Console.WriteLine("ksa-abstractions: HiddenUiFrameHook patch removed");
}

private static void Prefix(double dt)
{
    if (Program.DrawUI) return; // HUD visible: StarMap already fired the real hooks this frame

    try { BeforeGui?.Invoke(dt); }
    catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook BeforeGui error: {ex.Message}"); }

    try { AfterGui?.Invoke(dt); }
    catch (Exception ex) { Console.WriteLine($"ksa-abstractions: HiddenUiFrameHook AfterGui error: {ex.Message}"); }
}
```

   The unscience host wires only its non-UI work to it, so mod windows still respect the hidden HUD
   [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L132-L136). KSA-Public-Telemetry wants its broadcast overlay drawn with the
   HUD hidden, so it postfixes the private `DrawFps`, which runs one call earlier
   [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L40-L48) [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L60-L61).

4. Step simulation before the render on hidden-HUD frames. KSArmory's per-frame step normally runs in
   `StarMapAfterGui`; with the HUD hidden it would fall back to `StarMapAfterOnFrame`, after the render,
   and every round would be drawn one frame late. A prefix on `OnFrameCelestials`, guarded by the same
   frame latch as approach 1, runs it before the planet renderer reads the camera:

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L111-L131
// A no-op on every frame the GUI pass already claimed, which is every frame with a UI. The
// latch is what makes that true rather than a condition here: asking whether the UI drew would
// be a second answer to a question one place already owns.
private static void BeforeOnFrameCelestials(double deltaTime)
{
    try
    {
        _step?.Invoke(deltaTime);
    }
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the engine's loop. The
        // frame postfix has the mod's own fault handling, and it is one hook later -- so
        // giving up here is the same degradation a refused patch gives, arrived at differently.
        _step = null;

        if (_complained) return;
        _complained = true;
        Log.Error("the pre-render step failed; the frame postfix takes it from here", e);
    }
}
```

5. Add a menu to the main menu bar through the empty hook the game provides:

```csharp
// MeasureTools/Patches/Patch_MenuBar.cs L14-L30
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Measure"u8))
            return;

        bool shown = MeasureWindow.IsOpen;
        if (ImGui.MenuItem("Show Window"u8, default(ImString), shown))
        {
            if (shown)
                MeasureWindow.Instance.Close();
            else
                MeasureWindow.Instance.Open();
        }
```

6. Gate or swallow keys. `Program.OnKey` asks `GameSettings.OnKeyAll` first, and a `true` result stops
   all game key handling [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1750). unscience blocks game hotkeys while any ImGui text
   field has focus:

```csharp
// ksa-abstractions.lib/HotkeyGuard.cs L19-L44
public static void Patch(Harmony harmony)
{
    _original = AccessTools.Method(typeof(GameSettings), nameof(GameSettings.OnKeyAll));
    _prefix = typeof(HotkeyGuard).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
    harmony.Patch(_original, prefix: new HarmonyMethod(_prefix));
    Console.WriteLine("ksa-abstractions: HotkeyGuard patch applied");
}

public static void Unpatch(Harmony harmony)
{
    if (_original != null && _prefix != null)
        harmony.Unpatch(_original, _prefix);
    _original = null;
    _prefix = null;
    Console.WriteLine("ksa-abstractions: HotkeyGuard patch removed");
}

private static bool Prefix(ref bool __result)
{
    if (!Program.ConsoleWindow.IsOpen && ImGui.GetIO().WantTextInput)
    {
        __result = true;
        return false;
    }
    return true;
}
```

   Mouse buttons bound to game actions go through `OnMouseButton` -> `DispatchKeyEvent`
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2020) and bypass `OnKeyAll`; purrtty prefixes `Program.OnKey` and the private
   `DispatchKeyEvent` to cover both [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L154-L217)
   [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L247-L294).

7. React to a save load. StarMap has no load hook. `Program.OnGameLoaded` has one caller, right after
   `Universe.DeserializeSave` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81). KSArmory latches a flag in a postfix
   and resets in its next frame step rather than tearing state down inside the loader
   [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs#L42-L98). KSA-MeasureTools postfixes
   `DeserializeSave` directly:

```csharp
// MeasureTools/Patches/Patch_LoadSave.cs L12-L29
[HarmonyPatch(typeof(Universe), nameof(Universe.DeserializeSave))]
internal static class Patch_LoadSave
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        // A postfix throws into the loader.
        try
        {
            MeasureState.ClearAll();
            MeasureState.SetReferenceOverride(null);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("loadsave-" + ex.GetType().Name,
                $"[MeasureTools] Clearing measurements on save load failed: {ex}");
        }
    }
```

8. Read solver results the moment they land. KSA-AdvancedFlightComputer samples staging state in a
   prefix on `ApplyVehicleSolvers` (before results overwrite it) and ticks its per-vehicle logic in the
   postfix:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L108-L120
// Program.PrepareFrame joins the workers before this call, then drains input events, then lets FlightComputer.RaisePendingAlerts end the burns the workers or the RCS executor finished or stopped.
// The prefix runs before the worker results overwrite the burn state the staging detector compares against.
[HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), new Type[0])]
private static class ApplySolversPatch
{
    static void Prefix()
    {
        if (AutoStageEnabled)
            StagingDetector.Sample();
    }

    static void Postfix() => TickVehicles(LoadedVehicles.All);
}
```

9. Run per-frame work in phase with the camera. gatOS prefixes `GameViewport.OnFrame` and filters to
   the main viewport, so its work runs right before the controller and camera build this frame's
   matrices [gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs#L36-L65).

10. Record into the render. `RenderGame` has no extension point. ShaderExtensions replaces the
    swapchain `BeginRenderPass` call and inserts a call after `EndRenderPass` with a transpiler
    [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194); gatOS inserts a call before the final
    command-buffer `End` [gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs#L63-L75). Prefer a postfix on
    the sub-renderer that records the pass you need ([overview](overview.md#render-sequence)).

## Complete example

- unscience `ksa-abstractions.lib`: [ksa-abstractions.lib/HiddenUiFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HiddenUiFrameHook.cs),
  [ksa-abstractions.lib/HotkeyGuard.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HotkeyGuard.cs), applied with per-patch try/catch in
  [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L56-L60).
- KSArmory: [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) and
  [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs), installed from `StarMapAllModsLoaded`
  [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L149-L200).
- KSA-MeasureTools: [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs),
  [MeasureTools/Patches/Patch_LoadSave.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_LoadSave.cs).
- KSA-Public-Telemetry: [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs) (Harmony called through reflection so
  the mod still loads without it).

## Variations and gotchas

- Wrap every patch body in try/catch. Nothing between `App.Run` and your patch catches exceptions
  [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L47-L70); an exception in a frame patch ends the game.
- Patch private targets by name and check for `null`; check the signature too when the name is generic
  (`AccessTools.Method(type, name, argTypes)`).
- Several mods already patch `DrawProgramMenusHook`, `OnKeyAll` and `ExecuteNextVehicleSolvers`. Harmony
  chains them; use `Priority` only when order matters and do not return `false` from a prefix unless you
  mean to stop every later prefix and the original.
- `StarMapCore.DeInit` calls `Harmony.UnpatchAll()` with no id before it invokes `StarMapUnload`
  methods [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35); under Harmony's documented semantics that
  removes every owner's patches, so an `UnpatchAll(yourId)` in `StarMapUnload` finds nothing left.
  Unpatch anyway; it costs nothing and keeps the mod correct under a loader that unloads mid-session.
- Prefixing a small method can miss call sites the JIT inlined into the caller. unscience rewrites the
  call site in `PrepareFrame` with a transpiler for that reason
  [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L73-L104), and KSAero rewrites the `ComputeDrag`
  call site because that method is `AggressiveInlining` (approach 4).
- Transpilers that match IL shapes must refuse to patch when the shape changes. unscience's seven
  ordered seams throw and leave the game unpatched. ShaderExtensions throws only when its anchor calls
  (`RenderDrawData`, the preceding `BeginRenderPass`, the last `EndRenderPass`) are missing; the
  `ldloc.1`/`ldloc.0` it inserts for the command buffer and frame resources are hard-coded local slots
  that it never checks, so a reordering of `RenderGame`'s locals would be patched silently with the
  wrong values
  [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L194).
  A missing anchor does not leave the game unpatched either: the exception comes out of `PatchAll` in
  `StarMapBeforeMain`, which StarMap invokes without a `try`/`catch`
  [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159),
  so the launch aborts.
- Every patch in this article except the input and `ApplyVehicleSolvers` ones runs while the next
  physics step is in flight. Read vehicle state freely; write it only as approach 3 describes.

## See also

- [Game loop and mod lifecycle overview](overview.md)
- [Approach 1: StarMap lifecycle attributes](game-loop-and-lifecycle-1.md)
- [Approach 3: mutate simulation state safely](game-loop-and-lifecycle-3.md)
- [Approach 4: run code inside the physics step](game-loop-and-lifecycle-4.md)
