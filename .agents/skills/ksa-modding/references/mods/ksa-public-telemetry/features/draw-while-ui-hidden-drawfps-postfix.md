---
type: Mod Feature
title: Keep drawing while the game UI is hidden
description: Patches the private Program.DrawFps with a Harmony postfix found and applied purely through reflection, because OnFrame calls it outside the DrawUI guard that silences StarMap's UI hooks on F2
mod: ksa-public-telemetry
category: patching
techniques: [harmony-postfix, reflection]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Program.DrawFps, reflection KSA.Program.DrawFps]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program]
tags: [harmony, reflection, postfix, f2, hidden-ui, drawui, optional-dependency]
---

Players hide the game UI with F2 to record clean footage, which is exactly when a broadcast overlay is
wanted. StarMap's UI hooks do not fire then, so the mod hangs its draw on another method. It does this
without referencing Harmony at compile time and without letting a failure take the mod down: if
anything is off, it logs why and the overlay simply hides with the rest of the UI.

## How it works

1. **Why `DrawFps`.** In `Program.OnFrame`, `OnDrawUiFrame`/`OnDrawUiViewports` (and with them
   StarMap's `StarMapBeforeGui`/`StarMapAfterGui`) and `OnDrawUiThreadSafe` sit inside `if (DrawUI)`.
   The next two calls, `DrawFps()` and then `OnDrawUiConsole(dtPlayer)`, are outside the guard, and
   `ImGui.Render` follows them ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2286)).[^ksa-program]
   `DrawFps` is therefore not the only UI-phase call that survives F2: `OnDrawUiConsole` runs right
   after it (unscience prefixes it to keep its per-frame work running on hidden-HUD frames), and `OnFrameEditor`/`OnFrameViewports`
   also run earlier in the frame regardless of `DrawUI`. The mod picked `DrawFps`, a
   `private static void DrawFps()` with no parameters
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3284)); its own body only draws when frame statistics are
   enabled, but a postfix runs either way, once per frame, with the ImGui frame open.
2. **Find Harmony at runtime.** `FindType` scans `AppDomain.CurrentDomain.GetAssemblies()` for
   `HarmonyLib.Harmony` and `HarmonyLib.HarmonyMethod` ([HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L98-L113)). StarMap loads
   `0Harmony.dll` into the default load context before starting the game
   ([StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18)), so the types are there whenever the mod runs under StarMap.[^starmap-program]
3. **Patch by reflection.** It gets the target with
   `typeof(Program).GetMethod("DrawFps", NonPublic | Static, Type.EmptyTypes)`, creates
   `new Harmony("com.elevatorctln.ksatelemetryoverlay")` and `new HarmonyMethod(postfix)` with
   `Activator.CreateInstance`, then calls `Harmony.Patch` by reflection, filling an argument array sized
   to the method's parameters and putting the `HarmonyMethod` into the slot whose parameter is named
   `postfix` ([HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L50-L96)). Matching by parameter name rather than position survives
   Harmony adding optional parameters.
4. **The postfix.** `AfterDrawFps` returns immediately when `Program.DrawUI` is true (the normal hook
   already drew) and otherwise invokes the stored draw delegate with the last `dt` recorded from
   `[StarMapAfterOnFrame]` ([HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L40-L48), [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L76)).
5. **Gated by a setting.** The delegate is `DrawWhileGameUiHidden`, which does nothing unless
   `ShowWhenGameUiHidden` is on, and passes `gameUiHidden: true` so the settings and tuning windows are
   not drawn over the clean view ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L88-L126)). If installation failed the
   settings UI prints the reason ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L124-L130)).

## Lifecycle and timing

- **Install in `startup:all-mods-loaded`** (`[StarMapAllModsLoaded]`, a postfix on `ModLibrary.LoadAll`
  inside the `Program` constructor) ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L50),
  [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)). Nothing calls `DrawFps` until frames start,
  so patching there is safe.[^starmap-modlibrarypatches]
- **Runs in `frame:draw-ui`**, after `OnDrawUiThreadSafe` would have run and before `OnDrawUiConsole` and
  `ImGui.Render`. ImGui calls are legal; the foreground draw list still composes above everything. The
  physics step dispatched in this frame's `PrepareFrame` is already running on the workers, as for every
  StarMap frame hook; the draw reads only the mod's own snapshot (plus the stock canvas flags it keeps
  suppressed).
- **Every frame that reaches the UI phase.** The postfix also fires while `DrawUI` is true and returns
  at once, so there is no double draw. It does not fire on the frames where `PrepareFrame` returns
  `Exit` (window close, font rebuild), which end `OnFrame` before the UI block
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2243-L2248)).
- **`dt` is one frame stale.** `DrawFps` takes no arguments, so the mod reuses the `dtPlayer` it recorded
  in `[StarMapAfterOnFrame]` at the end of the previous frame.

## Key code

| Path | Role |
|---|---|
| [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L19-L48) | Install guard and postfix body |
| [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L50-L96) | Reflection-only Harmony patch |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L44-L50) | Install at startup and log the outcome |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L88-L96) | The gated draw delegate |

## Snippets

```csharp
// HiddenUiPatch.cs L60-L93
        MethodInfo? target = typeof(Program).GetMethod(
            TargetMethod, BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

        if (target is null)
        {
            return $"KSA has no {TargetMethod}() to hook; the game has probably moved it";
        }

        MethodInfo postfix = typeof(HiddenUiPatch).GetMethod(
            nameof(AfterDrawFps), BindingFlags.Public | BindingFlags.Static)!;

        MethodInfo? patch = harmonyType.GetMethod("Patch");

        if (patch is null)
        {
            return "Harmony.Patch is not where it used to be";
        }

        object instance = Activator.CreateInstance(harmonyType, "com.elevatorctln.ksatelemetryoverlay")!;
        object wrapped = Activator.CreateInstance(harmonyMethodType, postfix)!;

        ParameterInfo[] parameters = patch.GetParameters();
        object?[] arguments = new object?[parameters.Length];
        arguments[0] = target;

        int slot = Array.FindIndex(parameters, p => p.Name == "postfix");

        if (slot < 0)
        {
            return "Harmony.Patch takes no postfix";
        }

        arguments[slot] = wrapped;
        patch.Invoke(instance, arguments);
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.DrawFps` (private static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248) | Patch target that runs even with the UI hidden |
| `Program.OnFrame` UI block | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2286) | Why `DrawFps` is the right place |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L535) | Skip when the normal hook drew |

## Pitfalls and version notes

- **Private target.** `DrawFps` is an implementation detail; a rename or a parameter added in a game
  update makes `GetMethod` return null and the mod reports "the game has probably moved it".
- **Screenshots without the HUD.** Hi-res screenshot capture sets `Program.DrawUI = false` for its
  capture frames unless the HUD is kept ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210-L232)), so the
  postfix draws the overlay on those frames too (with "Stay visible when F2 hides the UI" on). Whether
  it then appears in the saved image depends on where the capture reads the frame; not tested in
  game.[^ksa-screenshotcapture]
- **Unpatched at shutdown by StarMap.** `StarMapCore.DeInit` calls the id-less `UnpatchAll()` before
  mods' unload hooks run ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)), which removes this patch too; the
  mod has no unpatch of its own.[^starmap-starmapcore]
- **Reflection-only Harmony** keeps the DLL free of a Harmony reference, but you lose compile-time
  checking of `Harmony.Patch`'s signature; the name-based slot lookup is the mitigation.
- **Inlining.** A tiny private static method can be inlined by the JIT, which would bypass the patch;
  `DrawFps` has a sizeable body, so this is unlikely here, but it is the classic risk of patching small
  private methods.

## See also

- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Hide and restore the stock flight HUD](hide-and-restore-stock-flight-hud.md)
- Frame order reference: [Game loop and lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md);
  the `OnDrawUiConsole` alternative is in [Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)
- Other mods: [AircraftHUD always-on hook from an OnFrameEditor postfix](../../ksa-aircrafthud/features/always-on-onframeeditor-hook.md)
- [Keep drawing when the HUD is hidden: reflection-installed postfix on the private Program.DrawFps](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-2.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-program]: StarMap StarMap/Program.cs
[^starmap-modlibrarypatches]: StarMap StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-starmapcore]: StarMap StarMap.Core/StarMapCore.cs
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
