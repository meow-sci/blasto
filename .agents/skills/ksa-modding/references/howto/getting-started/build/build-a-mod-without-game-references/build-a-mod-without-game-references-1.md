---
type: How-To
title: Build a reflection-only mod that references just StarMap.API and Harmony
description: Compile with no KSA reference and reach every game type by name with AccessTools, checking parameter names and field types before each patch and logging one consequence sentence per failed lookup
category: getting-started
group: build
topic: build-a-mod-without-game-references
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/reflection-only-mod-build]
techniques: [reflection, harmony-prefix]
hooks: [starmap StarMapBeforeMain, reflection HarmonyLib.AccessTools.TypeByName]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Program, KSA.ModLibrary, KSA.Universe]
tags: [reflection, accesstools, no-game-references, fail-soft, realatmospheres, resilience]
---

Most KSA mods reference `KSA.dll` and the `Brutal.*` assemblies at build time. Real Atmospheres references neither: its
project has two references, `StarMap.API` and `0Harmony`, both taken from the StarMap folder and both marked
`Private=false` so they are not shipped. Every game type, field and method is looked up by name at run time. The trade
is compile-time safety for a build that needs no game install, does not break when the game's assemblies change, and a
patch set that switches off piece by piece. The compile-gated alternative is
[Game-free half and compile-gated Game half](./build-a-mod-without-game-references-2.md); thin provider classes are [Static provider library](./build-a-mod-without-game-references-3.md).

## Background: how the game does it

Because the mod holds no reference to `KSA.dll`, nothing in its code can name a game type at compile time, and so the JIT
never needs one before the mod's own `BeforeMain` runs. At that point StarMap has loaded `KSA.dll` into its game load
context and set the working directory to the game folder, so `AccessTools.TypeByName` finds types, but no `Program`,
renderer or universe exists yet.[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L20-L57) The sample reads, for example, `Universe.GetElapsedTime`, which in build
2026.10.10.5554 returns a `UniverseTime` whose `Minutes` property the mod reads (the method was `GetElapsedSimTime`
before the rename).[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2398-L2402)[current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L40)

## Lifecycle and timing

`startup:starmap-before-main`: all lookups and all patches happen in `BeforeMain`. Only static lookups are safe;
anything that needs an instance (the atmosphere renderer) is captured later from inside a patch (a prefix that sees the
instance the first time it runs). Patches installed here run during asset loading, some on thread-pool threads
([Preload into the core load context from StarMapBeforeMain](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md), [StarMapBeforeMain - patch code that runs during startup](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md)).

## Steps

1. **Reference only StarMap.API and Harmony**, both non-private:[RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L28-L37)

   ```xml
   <!-- RealAtmospheres.csproj L28-L37 -->
     <ItemGroup>
       <Reference Include="StarMap.API">
         <HintPath>$(StarMapDir)\StarMap.API.dll</HintPath>
         <Private>false</Private>
       </Reference>
       <Reference Include="0Harmony">
         <HintPath>$(StarMapDir)\0Harmony.dll</HintPath>
         <Private>false</Private>
       </Reference>
     </ItemGroup>
   ```

2. **Look types up by name**, derive the install root from a game type's assembly, and fail with a clear exception
   inside a guarded `Install`:[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L20-L44)

   ```csharp
   // RealAtmospheres/ModMain.cs L38-L44
           string modDir = Path.GetDirectoryName(typeof(ModMain).Assembly.Location)!;

           Type modType = AccessTools.TypeByName("KSA.Mod")
               ?? throw new InvalidOperationException("KSA.Mod not found");

           string installRoot = Path.GetDirectoryName(modType.Assembly.Location)!;
   ```

3. **Patch methods take `object __instance` and `object` arguments**, and read game objects through cached
   `FieldInfo`/`PropertyInfo`. Harmony binds injected parameters by **name**, so check the shape first:[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L285-L292)

   ```csharp
   // RealAtmospheres/ModMain.cs L288-L292
       public static bool CanInjectSimTime(MethodInfo fill)
       {
           string?[] names = fill.GetParameters().Select(p => p.Name).ToArray();
           return names.Contains("body") && names.Contains("atmoRef");
       }
   ```

   Harmony throws at patch time on a missing injected parameter, which would take the rest of `Install` down. Also
   check field types (a `bool[]` field, the ring slots of a returned struct) before reading.
4. **Handle renames by trying both names**, and say what degrades when none exists. The sim clock was
   `GetElapsedSimTime` and is `GetElapsedTime` now; a silent fail-soft lookup once fell back to wall-clock time:[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L268-L283)

   ```csharp
   // RealAtmospheres/ModMain.cs L268-L283
       private static MethodInfo? SimClock()
       {
           if (_simClockLookedUp) return _getElapsedSimTime;
           _simClockLookedUp = true;

           _universeType ??= AccessTools.TypeByName("KSA.Universe");
           if (_universeType != null)
               foreach (string name in new[] { "GetElapsedTime", "GetElapsedSimTime" })
               {
                   _getElapsedSimTime = AccessTools.Method(_universeType, name);
                   if (_getElapsedSimTime != null) return _getElapsedSimTime;
               }

           ShadowBuilder.Log("WARN: no simulation clock found; haze animation falls back to wall-clock time");
           return null;
       }
   ```

5. **One try per subsystem, one consequence sentence per failure** ("haze animation is not sim-linked", "flight stays
   stock"), so a game update costs one feature, not the mod; also wrap the whole `Install` in `BeforeMain` so an
   unexpected failure leaves the game stock.
6. **Log without game types**: `Console.WriteLine`-based `ShadowBuilder.Log` ("RealAtmospheres - ...").
7. **ImGui and `ref struct`s need IL.** `ImString` is a `ref struct` and cannot be boxed for `MethodInfo.Invoke`, so the
   mod emits `DynamicMethod`s for ImGui calls (see its ModMenu feature page).

## Complete example

[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) is the whole entry: types by name, shape checks, the clock lookup, subsystem guards; [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj) the
two references. The shader-redirect half of the mod, including preloading a lazily loaded game assembly into the core
context, is [Preload into the core load context from StarMapBeforeMain](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md).

## Variations and gotchas

- **No compiler help.** A misspelled member or a wrong cast is a runtime log line, not a build error; test each
  subsystem after a game update by reading the log.
- **Not total.** In RealAtmospheres the lookups for `Mod.LoadSystems` and `PrepareSystems` run after the asset-list
  prefixes are already installed, so a throw there leaves a half-patched game. Order installs so the riskiest lookups
  come first.
- **Boxing costs.** Reflection on hot paths allocates; keep it out of per-substep prefixes (RealAtmospheres uses a typed
  weak table there).
- **Harmony binds to real signatures.** Prefix parameters can be `object`, but their **names** must match the game's
  parameter names, which a decompiler shows.
- **Build needs no game install**, so CI is trivial; see [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md) for the other
  routes.

## See also

- [Game-free half and compile-gated Game half](./build-a-mod-without-game-references-2.md), [Static provider library](./build-a-mod-without-game-references-3.md), [Build a KSA mod that references only StarMap.API and Harmony](./overview.md)
- [Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md), [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md)
- [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md)
