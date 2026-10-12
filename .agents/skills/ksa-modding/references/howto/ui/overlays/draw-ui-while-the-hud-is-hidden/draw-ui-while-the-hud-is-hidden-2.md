---
type: How-To
title: "Keep drawing when the HUD is hidden: reflection-installed postfix on the private Program.DrawFps"
description: Hook the one UI-phase call that always runs without a compile-time Harmony reference, with graceful degradation and the failure shown in the settings UI
category: ui
group: overlays
topic: draw-ui-while-the-hud-is-hidden
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/draw-while-ui-hidden-drawfps-postfix]
techniques: [harmony-postfix, reflection]
hooks:
  - reflection Harmony.Patch postfix on KSA.Program.DrawFps
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterOnFrame
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [hud-hidden, f2, reflection, harmony, drawfps, graceful-degradation, no-reference]
---
KSA-Public-Telemetry's broadcast overlay is wanted exactly when players hide the UI to record clean footage. StarMap's GUI hooks do not fire then, so the mod hangs its draw on `Program.DrawFps`, a private static method that
the game calls every frame outside the `DrawUI` guard, and does it **without referencing Harmony at compile time** and without letting a failure take the mod down: if anything is off, it logs why and the overlay simply hides with the rest
of the UI. Use this when you want an always-on hook but do not want Harmony as a build dependency. Demonstrated by KSA-Public-Telemetry.

## Background: how the game does it

- In `Program.OnFrame`, `OnDrawUiFrame`, `OnDrawUiViewports` (and so StarMap's GUI hooks) and `OnDrawUiThreadSafe` sit inside `if (DrawUI)`. The next two calls, `DrawFps()` and `OnDrawUiConsole(dtPlayer)`, are outside it, and `ImGui.Render` follows.
  `DrawFps` is a `private static void` with no parameters.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2287)[^ksa-program][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3250)[^ksa-program]
- Its own body draws only when frame statistics are enabled (into the main viewport's background draw list), but a postfix runs either way, once per frame, with the ImGui frame open. `DrawFps` is not the only call that
  survives F2: `OnDrawUiConsole` runs right after it ([approach 3](draw-ui-while-the-hud-is-hidden-3.md)), and `OnFrameEditor`/`OnFrameViewports` run earlier regardless of `DrawUI` ([approach 1](draw-ui-while-the-hud-is-hidden-1.md)).
- StarMap loads `0Harmony.dll` into the default load context before starting the game, so the `HarmonyLib.Harmony` types are present in the app domain whenever the mod runs under StarMap.[StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L18)[^starmap-program]

## Lifecycle and timing

- **Install at `startup:all-mods-loaded`** (`[StarMapAllModsLoaded]`, a postfix on `ModLibrary.LoadAll` inside the `Program` constructor). Nothing calls `DrawFps` until frames start, so patching there is safe.
- **Runs in `frame:draw-ui`**, after the point `OnDrawUiThreadSafe` would have run and before `OnDrawUiConsole` and `ImGui.Render`. The foreground draw list still composes above everything. It also fires while `DrawUI` is true and returns at once,
  so there is no double draw. It does not fire on frames where `PrepareFrame` returns `Exit`.
- **`dt` is one frame stale.** `DrawFps` takes no arguments, so the mod reuses the `dtPlayer` it recorded in `[StarMapAfterOnFrame]` at the end of the previous frame.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install from startup, record the delta from the frame hook, and report the outcome:**

```csharp
// TelemetryOverlayMod.cs L39-L50
[StarMapAllModsLoaded]
public void OnAllModsLoaded()
{
    try
    {
        _config = ConfigStore.Load();
        TelemetrySampler.Missions.ApplyEpochs(_config.MissionEpochs, _config.MissionOrigins);
        HiddenUiPatch.Install(DrawWhileGameUiHidden);

        Console.WriteLine(LogPrefix + (HiddenUiPatch.Installed
            ? "overlay will stay up when the game UI is hidden."
            : $"overlay will hide with the game UI: {HiddenUiPatch.Failure}."));
```
```csharp
// TelemetryOverlayMod.cs L66-L77
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    if (_disabled || _config is null)
    {
        return;
    }

    try
    {
        TelemetrySampler.Sample(_snapshot, _config, dtPlayer);
        HiddenUiPatch.RecordDelta(dtPlayer);
        Succeeded();
```

2. **Keep the postfix tiny and idempotent.** It returns immediately when the normal hook already drew; otherwise it invokes the stored draw delegate:

```csharp
// HiddenUiPatch.cs L10-L17
public static class HiddenUiPatch
{
    private const string TargetMethod = "DrawFps";
    private static Action<double>? _draw;
    private static double _lastDelta;
    public static bool Installed { get; private set; }
    public static string? Failure { get; private set; }
    public static void RecordDelta(double dt) => _lastDelta = dt;
```
```csharp
// HiddenUiPatch.cs L40-L48
public static void AfterDrawFps()
{
    if (Program.DrawUI)
    {
        return;
    }

    _draw?.Invoke(_lastDelta);
}
```

3. **Find Harmony at run time, then patch by reflection.** `FindType` scans the loaded assemblies. `GetMethod(NonPublic | Static, Type.EmptyTypes)` finds the private target; the `Harmony` and `HarmonyMethod` are created with `Activator`; the
   argument array is sized to `Harmony.Patch`'s parameters and the `HarmonyMethod` goes in the slot whose parameter is named `postfix`. Matching by name rather than position survives Harmony adding optional parameters:

```csharp
// HiddenUiPatch.cs L50-L53,L60-L96
private static string? Patch()
{
    Type? harmonyType = FindType("HarmonyLib.Harmony");
    Type? harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
// ...
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

    return null;
}
```
```csharp
// HiddenUiPatch.cs L98-L113
private static Type? FindType(string fullName)
{
    Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();

    for (int i = 0; i < loaded.Length; i++)
    {
        Type? found = loaded[i].GetType(fullName, throwOnError: false);

        if (found is not null)
        {
            return found;
        }
    }

    return null;
}
```

4. **Gate by a setting and by the draw context.** The delegate passes `gameUiHidden: true` so settings and tuning windows are not drawn over the clean view; with the setting off it does nothing:

```csharp
// TelemetryOverlayMod.cs L85-L96
[StarMapAfterGui]
public void OnAfterGui(double dt) => DrawEverything(dt, gameUiHidden: false);

private void DrawWhileGameUiHidden(double dt)
{
    if (_config is null || !_config.ShowWhenGameUiHidden)
    {
        return;
    }

    DrawEverything(dt, gameUiHidden: true);
}
```

5. **Surface the failure.** If installation failed, the settings UI prints `HiddenUiPatch.Failure`; the mod continues and the overlay hides with the HUD.

## Complete example

| File | Role |
|---|---|
| [HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs) | Install guard, postfix body and the reflection-only patch |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L40-L96) | Install at startup and the gated draw delegate |

## Variations and gotchas

- **Private target.** A rename or an added parameter makes `GetMethod` return null; the mod reports "the game has probably moved it".
- **Screenshots without the HUD.** Hi-res screenshot capture sets `Program.DrawUI = false` for its capture frames, so the postfix draws on those frames too (with the setting on); whether it appears in the saved image depends on where the capture reads the frame, not tested in game.
- **Unpatched at shutdown by StarMap.** `StarMapCore.DeInit` calls the id-less `UnpatchAll()` before mods' unload hooks, which removes this patch too.
- **Inlining.** A tiny private static method can be inlined by the JIT and bypass the patch; `DrawFps` has a sizeable body.
- **You lose compile-time checking** of `Harmony.Patch`'s signature; the name-based slot lookup is the mitigation.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-ui-while-the-hud-is-hidden](overview.md)
- [Approach 1: Harmony postfix on Program.OnFrameEditor](draw-ui-while-the-hud-is-hidden-1.md)
- [Approach 3: replay StarMap GUI work from a prefix on OnDrawUiConsole](draw-ui-while-the-hud-is-hidden-3.md)
- [Overlay on the foreground draw list](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md)
- [Patching private and overloaded game methods by name](../../../patching/internals/patch-a-private-game-method-by-name/overview.md)
- [Build a KSA mod that references only StarMap.API and Harmony](../../../getting-started/build/build-a-mod-without-game-references/overview.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- Mod feature: [Draw while the UI is hidden: DrawFps postfix](../../../../mods/ksa-public-telemetry/features/draw-while-ui-hidden-drawfps-postfix.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-program]: StarMap: StarMap/Program.cs
