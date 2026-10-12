---
type: How-To
title: Hook the frame with StarMap lifecycle attributes
description: Run mod code at startup, each frame and at unload with StarMap lifecycle attributes, and know exactly where each one fires in KSA's frame
category: getting-started
group: lifecycle
topic: game-loop-and-lifecycle
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/starmap/features/frame-lifecycle-hooks
  - mods/starmap/features/unload-and-shutdown
  - mods/starmap/features/attribute-hook-discovery
  - mods/ksaero/features/patch-refusal-and-rollback
  - mods/ksacars/features/frame-step-latch-and-car-registry
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/gatos/features/frame-hooks-and-f2-fallback
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapBeforeMain
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - starmap StarMapUnload
lifecycle:
  - startup:starmap-before-main
  - startup:immediate-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:after-on-frame
game_types: [KSA.Program, KSA.Mod, KSA.ModLibrary, KSA.Universe]
tags: [starmap, lifecycle, frame, attributes, imgui, startup]
---

StarMap is the code loader for KSA. It finds one class per mod marked `[StarMapMod]`, creates it,
and calls its methods marked with lifecycle attributes at fixed points of the game's startup and
frame. This is the simplest way to run code in KSA and the one every code mod starts with. This
article lists each attribute, its required signature, the exact point it fires (verified against the
decompiled game and StarMap's Harmony patches), and what is and is not safe there.

Prerequisites: a StarMap install, a C# class library referencing the game assemblies and
`StarMap.API`, and a `mod.toml` whose `[StarMap] EntryAssembly` names your DLL, as KSAero does in
[KSAero/mod.toml](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/mod.toml). The table is optional: without it StarMap loads
`<manifest id>.dll`, the manifest id being the mod's folder name, which is what OhMyStars relies on
[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L49-L57). Demonstrated by KSAero, KSACars, KSArmory, KSA-MeasureTools, OhMyStars
and ShaderExtensions.

## Background: how the game does it

KSA has no mod code API of its own: `Program.Main` builds a `Program`, whose constructor loads every
manifest mod's content, and then `App.Run` calls `Program.OnFrame` in a loop
([overview](overview.md#process-start-and-loading-sequence)). StarMap adds code mods from outside:

1. Before the game's `Main`, `ModLoader.PrepareMods` creates an `AssemblyLoadContext` per enabled mod and
   `RuntimeMod.InitializeMod` instantiates the `[StarMapMod]` class, registers its attributed methods and
   runs its `[StarMapBeforeMain]` method [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162).
2. `StarMapCore.Init` then applies StarMap's own Harmony patches [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29):
   a prefix on `Mod.PrepareSystems`, a postfix on `ModLibrary.LoadAll`, and a prefix, a postfix and a
   postfix on `Program.OnDrawUiFrame`, `Program.OnDrawUiViewports` and `Program.OnFrame`.
3. Each patch looks up the registered methods for its attribute and calls them with
   `MethodInfo.Invoke` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L7-L48).

## Lifecycle and timing

| Attribute | Required signature (checked by `IsValidSignature`) | Fires | Implemented by | Lifecycle value |
|---|---|---|---|---|
| `StarMapMod` | class attribute; public parameterless constructor | Constructor runs once in `RuntimeMod.InitializeMod`, before the game's `Main` | `Activator.CreateInstance` [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L139) | `startup:starmap-before-main` |
| `StarMapBeforeMain` | `void M()` [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L38-L45) | Right after the constructor, before `Program.Main`, before StarMap's lifecycle patches exist (only its optional `-InstancePath` prefix is already in place) | direct invoke [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159) | `startup:starmap-before-main` |
| `StarMapImmediateLoad` | `void M(KSA.Mod definingMod)` [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L72-L80) | Once, when the game calls `Mod.PrepareSystems` for this mod inside `ModLibrary.PrepareAll` | prefix on `Mod.PrepareSystems` [StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L8-L22) | `startup:immediate-load` |
| `StarMapAllModsLoaded` | `void M()` [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L99-L106) | Once, after `ModLibrary.LoadAll` returns, before the default system is built | postfix on `ModLibrary.LoadAll` [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L14-L28) | `startup:all-mods-loaded` |
| `StarMapBeforeGui` | `void M(double dt)` [StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L30-L39) | Every frame with `Program.DrawUI` true, at the start of `OnDrawUiFrame` | prefix on `Program.OnDrawUiFrame` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L24) | `frame:draw-ui` |
| `StarMapAfterGui` | `void M(double dt)` [StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L66-L75) | Every frame with `DrawUI` true, at the end of `OnDrawUiViewports`, before `OnDrawUiThreadSafe` | postfix on `Program.OnDrawUiViewports` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36) | `frame:draw-ui` |
| `StarMapAfterOnFrame` | `void M(double currentPlayerTime, double dtPlayer)` [StarMap.API/OnFrameAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnFrameAttributes.cs#L35-L45) | Every frame, after `PostRender`, including frames that exit early | postfix on `Program.OnFrame` [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48) | `frame:after-on-frame` |
| `StarMapUnload` | `void M()` [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L125-L132) | Once, after the game's `Run` loop returned and StarMap's id-less `UnpatchAll()` removed every Harmony patch in the process (its own and every mod's) | direct invoke in `ModLoader.Dispose` [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151) | none |

Where those points sit in the game, with the decompiled call sites:

- `StarMapImmediateLoad`: `ModLibrary.PrepareAll` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447) calls
  `mod.PrepareSystems()` for each enabled manifest entry, in manifest order, before any asset bundle is
  loaded [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L901).
- `StarMapAllModsLoaded`: `ModLibrary.LoadAll` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) has loaded every mod's
  fonts, asset bundles, systems and loaders. The universe does not exist yet: `Universe.LoadDefaultSystem`
  runs later in the constructor [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1066-L1074), and the frame loop has not started.
- `StarMapBeforeGui` and `StarMapAfterGui`: both sit inside `if (DrawUI)` in `Program.OnFrame`
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2276), after `PrepareFrame` has dispatched the next physics step and inside
  the ImGui `NewFrame`...`Render` window.
- `StarMapAfterOnFrame`: after `Render` and `PostRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2322-L2333); the next
  frame's `GizmosRenderer.ResetInstances` clears anything submitted for drawing here before it is drawn.

What each point allows:

| Point | Safe | Not safe |
|---|---|---|
| Constructor, `StarMapBeforeMain` | Apply Harmony patches that must be in place before the game constructs anything (ShaderExtensions, OhMyStars); read static configuration | No `Program` instance, window, Vulkan device, `GameSettings`, ImGui context or asset data; `ModLibrary.Manifest` is already prepared |
| `StarMapImmediateLoad(Mod)` | Read `mod.DirectoryPath` and `mod.Id`; write files the asset load will read (KSArmory writes a shader include) | Assets, parts and systems are not loaded; other mods may not have run yet. With KittenExtensions installed, rewriting an XML file listed in `mod.toml` here is not seen by the asset, system and planet-mesh loaders: KittenExtensions imported every listed file into its patch document in a `PrepareAll` prefix, before this loop, and redirects those loaders to that copy [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L28-L54) |
| `StarMapAllModsLoaded` | Apply most Harmony patches, read part and asset templates, register with other mods' APIs, load settings | No `Universe.CurrentSystem`, no vehicles, no ImGui frame; part templates do not have their game data yet (`ModLibrary.AttachGameData` runs later in the constructor) |
| `StarMapBeforeGui` | ImGui windows that should be submitted before the game's own UI | Mutating simulated state (the physics step is in flight); skipped when the HUD is hidden. Not a way into the game's menu bar: that bar is its own window (`ImGui.Begin("Menu Bar")` + `BeginMenuBar` in `DrawMenuBar`, drawn later in `OnDrawUiThreadSafe`), so `BeginMainMenuBar` from here opens ImGui's separate main-menu-bar window instead; postfix `Program.DrawProgramMenusHook` for a menu inside the game's bar ([approach 2](game-loop-and-lifecycle-2.md)) |
| `StarMapAfterGui` | ImGui windows, draw lists for world overlays, gizmo instances, reading vehicle state (the step applied this frame; the worker writes only its own copy) | Same as above; it is not "after all game UI": the game's menu bar, gauges, `Universe.OnDrawUi` and editor UI come after it |
| `StarMapAfterOnFrame` | Per-frame bookkeeping that must run even with the HUD hidden; network or file polling | ImGui (outside the ImGui frame), drawing for this frame (already rendered), mutating simulated state (step in flight) |
| `StarMapUnload` | Dispose the mod's own native handles, stop its threads, flush files (its Harmony patches are already gone) | ImGui, Vulkan or game state that the shutdown already disposed; any path from a getter that a patch redirected. StarMap's own `-InstancePath`/`STARMAP_INSTANCE_PATH` prefix on `Constants.DocumentsFolderPath` is removed with everything else, so the getter returns the default documents folder here; resolve and keep such paths in `StarMapAllModsLoaded` [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L31) |

The `dt` passed to the GUI hooks and `dtPlayer` passed to `StarMapAfterOnFrame` are the player-time
frame delta, clamped to `1 / MinTargetFrameRate` [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L47-L69). They ignore pause and time
warp; integrate simulation with `Universe.GetLastSimStep().DeltaTime` instead
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2380-L2384).

## Steps

1. Declare the entry assembly in `mod.toml`:

```toml
# KSAero/mod.toml L1-L4
name = "KSAero"

[StarMap]
EntryAssembly = "KSAero"
```

2. Create one public class marked `[StarMapMod]`. StarMap picks the first type in the entry assembly
   carrying an attribute named `StarMapModAttribute` and creates it with its parameterless constructor
   [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L131-L138). Keep the constructor empty; do work in
   the hooks.

3. Apply your Harmony patches in `StarMapAllModsLoaded`, wrapped so a failed patch degrades the mod
   instead of the game. KSAero applies a transpiler and unpatches on failure:

```csharp
// KSAero/Mod.cs L8-L36
[StarMapMod]
public sealed class Mod
{
    private const string TestedGameVersion = "v2026.10.7.5541";
    private const string HarmonyId = "ksaero";

    private static Harmony? _harmony;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");

        var harmony = new Harmony(HarmonyId);
        try
        {
            AirForcePatch.Apply(harmony);
            _harmony = harmony;
            DefaultCategory.Log.Info("[KSAero] Loaded. Atmospheric forces now use the body-of-revolution model.");
        }
        catch (Exception ex)
        {
            // The transpiler refuses rather than guesses, so a failure here leaves stock aero running.
            harmony.UnpatchAll(HarmonyId);
            DefaultCategory.Log.Warning($"[KSAero] Patching failed, stock aerodynamics stay in place: {ex}");
        }
    }
```

   Patches that must exist before the game constructs its renderer or loads assets go in
   `StarMapBeforeMain` instead, as ShaderExtensions does for its `Program` render patches:

```csharp
// ShaderExtensions/ShaderExtensions.cs L6-L14
[StarMapMod]
public class ShaderExtensions
{
    [StarMapBeforeMain]
    public void Setup()
    {
        var harmony = new Harmony("ShaderExtensions");
        harmony.PatchAll();
    }
```

4. Use `StarMapImmediateLoad` for per-mod setup that needs the `KSA.Mod` (its folder, its id) before the
   asset load. KSACars only logs the build it was made for:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L30-L35
[StarMapImmediateLoad]
public void OnImmediateLoad(Mod mod)
{
    Log.Info($"loading (mod id: {mod.Id})");
    Log.Info($"KSACars {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
}
```

5. Draw ImGui in `StarMapAfterGui` (or `StarMapBeforeGui` for windows that should come before the
   game's UI; a menu in the game's own bar needs a postfix on `Program.DrawProgramMenusHook`, see
   [approach 2](game-loop-and-lifecycle-2.md)). Catch everything: StarMap invokes the method without a try/catch, so an
   exception leaves `Program.OnFrame` and nothing in `App.Run` catches it
   [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36) [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L47-L70). KSA-MeasureTools logs
   the first failure of each type and keeps going:

```csharp
// MeasureTools/Mod.cs L57-L78
    // Prune first, so neither the window nor the overlay resolves an anchor whose
    // body is gone.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("Mod.Draw");
#endif
            MeasureState.Prune();
            if (!MeasureViewport.TryGetActive(out IGameViewport viewport))
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
        }
        catch (Exception ex)
        {
            // Per-frame path: first throw of each type logs a stack, then quiet.
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
        }
    }
```

   OhMyStars uses both GUI hooks, windows before the game's UI and its sky renderer after:

```csharp
// src/OhMyStarsEntryPoint.cs L44-L54
[StarMapBeforeGui]
public static void OnBeforeGui(double dt) {
    OhMyStarsWindow.Draw();
    StarsEditWindow.Draw();
    OhMyStarsWindow.PersistWindowOpenStates();
}

[StarMapAfterGui]
public static void OnAfterUi(double dt) {
    StellariumRenderer.Draw();
}
```

6. If the work must also run while the HUD is hidden (the player pressed the `ToggleUi` key, or a
   screenshot is being taken), run it from both `StarMapAfterGui` and `StarMapAfterOnFrame` and let
   whichever comes first claim the frame. KSACars uses a latch that `StarMapAfterOnFrame` always
   releases in a `finally`:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L59-L82, L84-L93, L112-L114
[StarMapAfterOnFrame]
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    try
    {
        if (_disabled) return;

        // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
        StepOnce();

        // After the step, so a command sees this frame's world.
        _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? KsaWorld.SimStepSeconds : 0.0);
    }
    catch (Exception e)
    {
        Fault("frame", e);
    }
    finally
    {
        // Unconditionally: this is the only hook KSA always calls, so a release skipped by an early
        // return or an exception would stop the mod for the rest of the session.
        _frame.EndFrame();
    }
}
// ...
// The GUI pass is the preferred hook because it runs before the render, so the wheels are drawn
// where this frame put them; F2 hides the UI and skips that pass, and the frame postfix then
// poses them a frame late rather than not at all.
[StarMapAfterGui]
public void OnAfterGui(double dt)
{
    try
    {
        if (_disabled) return;
        StepOnce();
// ...
private void StepOnce()
{
    if (!_frame.Claim()) return;
```

   The latch itself is two methods [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs#L15-L36) (KSArmory ships the same class). On frames with the
   HUD hidden the work then runs after the render, one frame late; approach 2 shows how KSArmory moves it
   before the render.

7. Release resources in `StarMapUnload` [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L45-L51). StarMap has
   already removed every Harmony patch by then, so KSAero's `UnpatchAll(HarmonyId)` there is a
   harmless no-op.

8. Build, copy the DLL and `mod.toml` into a folder named after the mod id under the game's local mods
   folder (`ModLibrary.LocalModsFolderPath`, `<Documents>/mods` [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178); the StarMap
   README gives `Documents/My Games/Kitten Space Agency/mods/<ModName>/` [README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/README.md#L16-L23)) and
   start the game through StarMap. A newly found mod triggers the game's mod confirmation and StarMap's
   restart prompt, because StarMap instantiates code mods before the game's `Main` and needs a restart to
   pick up a newly enabled one [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L76). The console
   then shows `StarMap - Loaded mod: <id> from manifest` for each mod StarMap instantiated
   [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L86).

## Complete example

- KSAero: [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) (all-mods-loaded patching, `StarMapAfterGui` readout, unload).
- KSACars: [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) (every attribute except `StarMapBeforeGui`,
  frame latch, fault counter).
- KSArmory: [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L200) (immediate load writes a shader include;
  all-mods-loaded installs a dozen hooks) and [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L486-L530)
  (`StarMapBeforeGui` opens `BeginMainMenuBar` for a "Mods" entry, which lands in ImGui's own bar,
  not beside the game's File and Universe menus as its comment says; `StarMapAfterGui` steps the
  simulation, then draws).
- KSA-MeasureTools: [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs).
- OhMyStars: [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs) (static hook methods, `StarMapBeforeMain` patches).

## Variations and gotchas

- Only one `StarMapBeforeMain` and one `StarMapImmediateLoad` method per mod run: `ModRegistry` keeps a
  single `MethodInfo` for each and the last one registered wins
  [StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L46-L50). Frame attributes may appear on several
  methods.
- The XML docs say "instance method", but the signature checks do not test it and StarMap scans
  `ModType.GetMethods()`, which includes public static methods; OhMyStars' hooks are static. Non-public
  methods are never found.
- Attribute matching is by type name, so a mod compiled against its own copy of `StarMap.API` still
  works [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L131)
  [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L145).
- A method whose signature does not match is skipped silently
  [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L150): check `double` parameters on frame hooks.
- Dispatch is reflection: each frame allocates the argument array and boxes the doubles. Keep the
  attributed methods thin and fan out from them.
- `StarMapAfterGui` is not after the game's whole UI. If a window must overlay the stock menu bar or the
  gauges, draw it with a Harmony postfix on `Program.OnDrawUiThreadSafe` (approach 2).
- StarMap has no hook for "game loaded" or "save loaded". Patch `Universe.DeserializeSave` or
  `Program.OnGameLoaded` (approach 2), or detect the change in a frame hook.
- None of the frame hooks is a safe place to change positions, velocities, parts or vehicles: they all
  run after the physics step was dispatched. Use approach 3.
- Mods load in manifest order; a mod waiting on `[StarMap] ModDependencies` is initialized after its
  dependency, so its constructor and `StarMapBeforeMain` run later than the others
  [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L74-L101).

## See also

- [Game loop and mod lifecycle overview](overview.md)
- [Approach 2: Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md)
- [Approach 3: mutate simulation state safely](game-loop-and-lifecycle-3.md)
- [Approach 4: run code inside the physics step](game-loop-and-lifecycle-4.md)
