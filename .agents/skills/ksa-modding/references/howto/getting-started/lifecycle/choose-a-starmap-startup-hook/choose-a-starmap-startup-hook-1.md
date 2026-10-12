---
type: How-To
title: "StarMapBeforeMain: install patches on code that runs during startup"
description: Use StarMapBeforeMain for loader-level work only - Harmony patches on code that runs before AllModsLoaded, looked up by name, with a fail-soft install
category: getting-started
group: lifecycle
topic: choose-a-starmap-startup-hook
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/realatmospheres/features/reflection-only-mod-build
  - mods/shaderextensions/features/duck-typed-attribute-api
techniques: [starmap-lifecycle, harmony-prefix, reflection]
hooks: [starmap StarMapBeforeMain, reflection KSA.Mod]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.Program, KSA.ModLibrary, KSA.Mod]
tags: [starmap, before-main, harmony, startup, reflection, fail-soft]
---

`StarMapBeforeMain` is the earliest hook: it runs before KSA's `Main`, so it is the only place to patch code that
executes during the `Program` constructor before a mod's `AllModsLoaded` hook (shader compiles, asset-list
reads, `Program.Main` itself). Nothing of the game exists yet, so the rule is: **patch, do not touch state**.
RealAtmospheres and ShaderExtensions are the two mods that need it. Choose it only if a later hook is too late
([StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md), [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md)).

## Background: how the game does it

StarMap invokes `BeforeMainAction` at the end of `RuntimeMod.InitializeMod`, directly (no Harmony), as soon as the
mod's hooks are bound.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162) That happens inside `StarMapCore.Init`, before StarMap
applies its own patches; `KSA.dll` is loaded in the core context and the working directory is the game folder, but
the `Program` constructor has not run.[StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29)

State at this point:

| Exists | Does not exist |
|---|---|
| `KSA.dll` and dependencies in the core load context; working directory = KSA folder; `ModLibrary.Manifest` (StarMap prepared it); the instance-path override; mods initialized before yours | `Program` instance, GLFW window, Vulkan device, ImGui context, the player's settings (`GameSettings.Current` is built-in defaults until `LoadFromFile`), KSA log sinks, any asset; lazily loaded game assemblies such as `Planet.Render.Core.dll` |

So logging here is `Console.WriteLine` (or your own file), not `DefaultCategory.Log`.

## Lifecycle and timing

`startup:starmap-before-main`; the patches you install here fire later, during the `Program` constructor
(`startup:asset-load`, e.g. shader compiles in `ModLibrary.LoadAll`) or in `Program.Main`. The constructor's order
is `ModLibrary.PrepareAll` (L899), `LoadAll` (L942), `ModLibrary.Bind` (L978), `AttachGameData` (L985),
`IsLoaded = true` (L1317).[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L943) Patches installed in `BeforeMain` are in place before StarMap's own
(which are applied after every mod's `BeforeMain`), so Harmony priority rules, not installation order alone, decide
who runs first on a method both patch. See [StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) for the full table.

## Steps

1. **Declare the hook.** Public, parameterless, on the `[StarMapMod]` class:

   ```csharp
   // RealAtmospheres/ModMain.cs L20-L35
   [StarMapMod]
   public class ModMain
   {
       [StarMapBeforeMain]
       public void BeforeMain()
       {
           try
           {
               Install();
           }
           catch (Exception ex)
           {
               ShadowBuilder.Log("FAILED to install (game will run stock): " + ex);
           }
       }
   ```

   The `try`/`catch` is the important part: an exception here escapes `ModLoader.Init`, StarMap invokes the hook
   with no handler, and the launch aborts.
2. **Find types by name.** You usually have no compile-time reference to the target (or it may not be loaded).
   `AccessTools.TypeByName` searches every context:

   ```csharp
   // RealAtmospheres/ModMain.cs L38-L44
           string modDir = Path.GetDirectoryName(typeof(ModMain).Assembly.Location)!;

           Type modType = AccessTools.TypeByName("KSA.Mod")
               ?? throw new InvalidOperationException("KSA.Mod not found");

           string installRoot = Path.GetDirectoryName(modType.Assembly.Location)!;
   ```

   For a game assembly that is not loaded yet, preload it into the core context first
   ([Preload into the core load context from StarMapBeforeMain](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md)).
3. **Check the shape, then patch.** Verify parameter names and field types before `Patch`, because Harmony
   throws at patch time on a missing injected parameter and that would take the rest of `Install` down:

   ```csharp
   // RealAtmospheres/ModMain.cs L288-L292
       public static bool CanInjectSimTime(MethodInfo fill)
       {
           string?[] names = fill.GetParameters().Select(p => p.Name).ToArray();
           return names.Contains("body") && names.Contains("atmoRef");
       }
   ```

4. **Patch only what must be early.** Whole-assembly attribute patching is the simplest form: ShaderExtensions
   does nothing else in `BeforeMain`:[ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs)

   ```csharp
   // ShaderExtensions/ShaderExtensions.cs L6-L16
       [StarMapMod]
       public class ShaderExtensions
       {
           [StarMapBeforeMain]
           public void Setup()
           {
               var harmony = new Harmony("ShaderExtensions");
               harmony.PatchAll();
           }
       }
   ```

   Its patches include a transpiler on `Program.RenderGame` and a `Program.Main` prefix that scans other mods'
   assemblies for look-alike attributes (see [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](../../dependencies/share-types-between-mods/share-types-between-mods-3.md)).
5. **Log the outcome in one line per subsystem**, with the player-visible consequence, so a game update costs one
   feature rather than the mod ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).
6. **Leave everything that needs game state to later hooks.** Anything that needs an instance (the renderer) is
   captured from inside a patch the first time it runs, not looked up here.

## Complete example

[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) (`Install()` builds a shadow shader tree, patches the XML and shader seams), [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs) (attribute
patches only). RealAtmospheres has no `KSA.dll` reference at all ([Reflection-only mod with visible breakage](../../build/build-a-mod-without-game-references/build-a-mod-without-game-references-1.md)).

## Variations and gotchas

- **An anchor miss aborts the launch.** If `PatchAll` or a transpiler throws inside `BeforeMain` and the mod does
  not catch it (ShaderExtensions does not), the exception leaves `RuntimeMod.InitializeMod`, which has no
  `try`/`catch`, so the game never starts. Add your own try/catch unless failing loudly is the intent.
- **Try/catch is not total.** In RealAtmospheres the lookups for `Mod.LoadSystems` and `PrepareSystems` run after
  the asset-list prefixes are already installed, so a throw there leaves a half-patched game.
- **Prefer `StarMapAllModsLoaded` for ordinary patches**, e.g. UI and flight code that first runs much later.
- **Thread-safety.** Prefixes on loader paths (shader compiles) run on thread-pool threads during
  `ModLibrary.LoadAll`.
- **Missing signatures are silent.** If the hook never prints, check [Signature checklist against StarMap's binding rules](./choose-a-starmap-startup-hook-4.md).

## See also

- [StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md), [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md), [Signature checklist against StarMap's binding rules](./choose-a-starmap-startup-hook-4.md), [Choose and use the right StarMap lifecycle hook](./overview.md)
- [Startup lifecycle hooks (BeforeMain, ImmediateLoad, AllModsLoaded)](../../../../mods/starmap/features/startup-lifecycle-hooks.md), [Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md),
  [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md)
- [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)
