---
type: Mod Feature
title: Reflection-only mod build
description: Compiles against StarMap.API and 0Harmony only, reaches every game member by name through AccessTools, checks each target's shape before patching, and degrades one subsystem at a time when the game changes
mod: realatmospheres
category: getting-started
techniques:
  - starmap-lifecycle
  - reflection
  - harmony-prefix
  - harmony-postfix
  - harmony-transpiler
  - fail-soft-patching
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main]
game_types:
  - KSA.Mod
  - RenderCore.ShaderModuleUtils
  - KSA.AtmosphereRenderer
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.PhysicalAtmosphereReference
tags: [getting-started, build, reflection, harmony, fail-soft, versioning, starmap]
---

Most KSA mods reference `KSA.dll` and the `Brutal.*` assemblies at build time. Real Atmospheres
references neither: its project has two references, `StarMap.API` and `0Harmony`, both from the
StarMap folder and both marked `Private=false` so they are not shipped
([RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L28-L37)). Every game type, field and method is
looked up by name at runtime. The trade is compile-time safety for a build that does not break when
the game's assemblies change, and for a patch set that can switch off piece by piece.[^ra-realatmospheres-csproj]

## How it works

- **Types by name.** `AccessTools.TypeByName("KSA.Mod")`, `"KSA.AtmosphereRenderer"`,
  `"KSA.CelestialSystem"`, `"KSA.Universe"`, `"Brutal.ImGuiApi.ImGui"` and so on
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L41-L57), [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L31-L32),
  [Menu.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Menu.cs#L96-L101)). Patch methods take `object __instance` and `object` arguments and use
  cached `FieldInfo`/`PropertyInfo` to read game objects ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L155-L175)).
- **The install root from a game type.** The game folder is the directory of the assembly that
  defines `KSA.Mod`; `Content/Core` is found from there ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L39-L60)).
- **Lazy assemblies.** A type in an assembly the game has not loaded yet (`Planet.Render.Core.dll`)
  is loaded explicitly into the game's load context first; see
  [Shader file redirect](shader-file-redirect.md).
- **Shape checks before `Patch`.** Harmony binds injected parameters by name and throws at patch
  time if one is missing, which would take the rest of `Install` down. So the mod checks parameter
  names (`body`, `atmoRef`, `altitudeInMeters`, `nearbyDistance`) and field types (`bool[]`
  `_planetLutsValid`, the ring slots of the returned struct) first, and only then patches
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L285-L292), [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L419-L437),
  [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L36-L42), [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L123)).
- **One try per subsystem.** Each optional patch is wrapped on its own and logs a sentence about the
  player-visible consequence ("haze animation is not sim-linked", "flight stays stock"), so a game
  update costs one feature, not the mod ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L101-L145)). The whole
  `Install` is also wrapped in `BeforeMain`, and the catch logs "game will run stock"
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L35)).
  That holds only for a throw before the first `Patch`. The lookups that throw for `Mod.LoadSystems`
  and `PrepareSystems` run after the asset-list prefixes are installed, and the one for
  `ShaderModuleUtils.FromFile` runs after all four XML prefixes. A game that drops the
  four-parameter `FromFile` would therefore load the patched XML with stock shaders, and the flight,
  nearby-body and atmosphere patches would not be installed
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L72-L99)).
- **Renames handled by trying both names.** The sim clock was `GetElapsedSimTime` and is
  `GetElapsedTime` now; the mod tries both, because the fail-soft lookup had once silently fallen back
  to wall-clock time ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L260-L283)). In build 2026.10.10.5554 the method is
  `Universe.GetElapsedTime`, returning a `UniverseTime` whose `Minutes` property the mod reads
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2398-L2402),
  [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L40)).[^ksa-universe]
- **Ref structs need IL.** `ImString` is a `ref struct` and cannot be boxed for
  `MethodInfo.Invoke`, so the ImGui calls are emitted as `DynamicMethod`s (see
  [ModMenu page and TOML settings](modmenu-page-and-toml-settings.md)).
- **Logging without the game's logger.** `ShadowBuilder.Log` writes `RealAtmospheres - ...` to the
  console ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L1093)).

## Lifecycle and timing

- **`startup:starmap-before-main`.** All lookups and all patches happen in `BeforeMain`. At that point
  StarMap has loaded `KSA.dll` into its game load context and set the working directory to the game
  folder ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L49)), so types resolve, but no
  `Program`, renderer or universe exists. Only static lookups are safe; anything that needs an instance
  (the atmosphere renderer) is captured later from inside a patch (`RendererSeenPrefix`,
  [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L367-L368)).

## Key code

| Path | Role |
|---|---|
| [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L3-L37) | `net10.0`, `EnableDynamicLoading`, two references, StarMap folder detection |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L37-L148) | `Install`: lookups, shape checks, per-subsystem patching |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L260-L292) | Sim clock lookup with both names; parameter-name check |
| [FlightAtmospheres.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/FlightAtmospheres.cs#L21-L54) | All-members-present check before patching |

## Snippets

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

```csharp
// ModMain.cs L268-L292
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

    /// <summary>Whether FillPlanetAtmosphereData still takes the two arguments the prefix reads.
    /// Harmony injects them by name and throws at patch time on a missing one, which would take
    /// the rest of the mod down with it, so this is checked before patching.</summary>
    public static bool CanInjectSimTime(MethodInfo fill)
    {
        string?[] names = fill.GetParameters().Select(p => p.Name).ToArray();
        return names.Contains("body") && names.Contains("atmoRef");
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `KSA.Mod` (type, assembly location) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L82) | Anchor for the install root and the XML redirect |
| `Universe.GetElapsedTime`, `UniverseTime.Minutes` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2398-L2402) | Sim clock read by name |
| `AtmosphereRenderer.FillPlanetAtmosphereData(..., AtmosphericBody body, AtmosphereReference atmoRef, ...)` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136) | Parameter names checked before patching |

## Pitfalls and version notes

- **No compiler help.** A misspelled member name or a wrong cast is a runtime log line, not a build
  error. The mod mitigates this with explicit shape checks and a log line per failure; test each
  subsystem after a game update by reading the log.
- **Boxing costs.** Reflection on hot paths allocates (`FieldInfo.GetValue` on value types); the mod
  keeps reflection out of the per-substep flight prefixes, which use a typed weak table instead.
- **Harmony still binds to real signatures.** The prefix parameter types are `object`, but names must
  match the game's parameter names; a decompiler's names are the real metadata names, so check them in
  the decomp.

## See also

- [Shader file redirect into a patched shadow tree](shader-file-redirect.md)
- [Measured flight atmospheres](measured-flight-atmospheres.md)
- [ModMenu page and TOML settings](modmenu-page-and-toml-settings.md)
- [Release packaging and deploy scripts](release-packaging-and-deploy.md)
- [Build a reflection-only mod that references just StarMap.API and Harmony](../../../howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-1.md) - how-to article demonstrating this feature
- [StarMapBeforeMain: install patches on code that runs during startup](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md) - how-to article demonstrating this feature
- [Reflection-only mod with parameter-name and field-type pre-checks](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-4.md) - how-to article demonstrating this feature

[^ra-realatmospheres-csproj]: RealAtmospheres.csproj
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
