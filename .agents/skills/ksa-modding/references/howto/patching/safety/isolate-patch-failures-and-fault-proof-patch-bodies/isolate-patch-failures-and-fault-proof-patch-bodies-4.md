---
type: How-To
title: Reflection-only mod with parameter-name and field-type pre-checks
description: Compile against StarMap.API and 0Harmony only, reach every game member by name, verify parameter names and field types before each patch and wrap each subsystem in its own try so a game update costs one feature
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/reflection-only-mod-build]
techniques: [reflection, harmony-prefix, harmony-postfix]
hooks: [starmap StarMapBeforeMain, harmony-prefix KSA.AtmosphereRenderer.FillPlanetAtmosphereData]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.AtmosphereRenderer, KSA.Universe, KSA.Mod]
tags: [harmony, reflection, prechecks, parameter-names, isolation, no-game-references]
---

Most KSA mods reference `KSA.dll` and the `Brutal.*` assemblies at build time. Real Atmospheres references neither: its project
has two references, `StarMap.API` and `0Harmony`, both marked `Private=false`. Every game type, field and method is looked up
by name at runtime. That trades compile-time safety for a build that does not break when the game's assemblies change, and for a
patch set that can switch off piece by piece. Because Harmony binds injected arguments *by name* and throws at patch time when a
name is missing, the mod checks parameter names and field types first, and only then patches.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Real Atmospheres in
[Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md).

## Background: what can go wrong at patch time

A prefix parameter named like an original parameter (`body`, `atmoRef`) is injected by name. If the game renames the parameter,
`harmony.Patch` throws, which, unguarded, takes the rest of `Install` down with it. In build 2026.10.10.5554 the target is a
private instance method that returns a value struct:

```csharp
// current/decomp/KSA/AtmosphereRenderer.cs L1136-L1138
private AtmosphereData FillPlanetAtmosphereData(Celestial? inNearbyCelestial, AtmosphericBody body, AtmosphereReference atmoRef, float3 planetPosition, float packedStartCoordinates, int lutLayer)
{
	AtmosphereData result = new AtmosphereData
```

Source: [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1138)[^ksa-atmosphererenderer]

The returned `AtmosphereData` has the ring slots (`UseRingShadows`, `RingNormalAndInnerRadius`, `RingOuterRadius`) that the
mod's haze-turn postfix writes into ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1155-L1161)); if those fields change type, a boxed write would
fail at run time, so they are checked before patching.

## Lifecycle and timing

All lookups and all patches happen in `[StarMapBeforeMain]` (`startup:starmap-before-main`). StarMap has loaded `KSA.dll`
into its game load context and set the working directory, so types resolve, but no `Program`, renderer or universe exists: only
static lookups are safe, and anything that needs an instance (the atmosphere renderer) is captured later from inside a patch.
`BeforeMain` wraps `Install` in a `try`/`catch` that logs "game will run stock", because StarMap does not catch (see
[Let an anchor failure stop the launch (and why that can be right)](../refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reference only StarMap.API and 0Harmony.** The csproj marks both `Private=false` so they are not shipped:

```xml
<!-- RealAtmospheres.csproj L28-L38 -->
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

Source: [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L28-L38)[^realatmospheres-realatmospheres]

2. **Find types by name and handle lazily loaded assemblies.** `AccessTools.TypeByName("KSA.Mod")`; a type in an assembly that is
   not loaded yet is loaded explicitly into the game's load context first ([Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](../../harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-2.md)).

3. **Check the shape before `Patch`.** Parameter names and field types are verified first. The two checks that guard the
   atmosphere prefix and postfix:

```csharp
// ModMain.cs L285-L292
/// <summary>Whether FillPlanetAtmosphereData still takes the two arguments the prefix reads.
/// Harmony injects them by name and throws at patch time on a missing one, which would take
/// the rest of the mod down with it, so this is checked before patching.</summary>
public static bool CanInjectSimTime(MethodInfo fill)
{
    string?[] names = fill.GetParameters().Select(p => p.Name).ToArray();
    return names.Contains("body") && names.Contains("atmoRef");
}
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L285-L292)[^realatmospheres-modmain]

```csharp
// ModMain.cs L419-L437
/// <summary>Whether the data FillPlanetAtmosphereData returns still has the ring slots the haze's
/// turning is sent in, and the method the body argument. Checked before patching, like the clock.</summary>
public static bool CanInjectHazeTurn(MethodInfo fill) =>
    fill.GetParameters().Any(p => p.Name == "body") && TurnSlots(fill.ReturnType);

private static bool TurnSlots(Type data)
{
    if (_turnDataType != data)
    {
        _turnDataType = data;
        _useRings = data.GetField("UseRingShadows");
        _ringNormal = data.GetField("RingNormalAndInnerRadius");
        _ringOuter = data.GetField("RingOuterRadius");
        _ringNormalParts = _ringNormal == null ? null
            : new[] { "X", "Y", "Z", "W" }.Select(n => _ringNormal.FieldType.GetField(n)).ToArray();
    }
    return data.IsValueType && _useRings?.FieldType == typeof(int) && _ringOuter?.FieldType == typeof(float)
        && _ringNormalParts != null && _ringNormalParts.All(f => f?.FieldType == typeof(float));
}
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L419-L437)

4. **Patch each optional subsystem in its own `try` and log the consequence.** A failure writes one sentence about what the
   player loses ("haze animation is not sim-linked") and the install carries on:

```csharp
// ModMain.cs L101-L142
// Sim-linked haze animation: while the renderer builds a haze body's atmosphere data, its
// ozone extent reads as elapsed SIM seconds, which the haze march takes as its clock
// (freezes on pause, scales with time warp). Fail-soft: without the method the shader
// runs on the body's own extent or wall-clock time.
Type? atmoRenderer = AccessTools.TypeByName("KSA.AtmosphereRenderer");
MethodInfo? fillAtmo = atmoRenderer == null ? null : AccessTools.Method(atmoRenderer, "FillPlanetAtmosphereData");
if (fillAtmo != null && Patches.CanInjectSimTime(fillAtmo))
{
    // The haze's turning about the body's own pole goes out in the data the method returns, when
    // that still has the slots for it: see Patches.FillAtmosphereDataTurnPostfix.
    bool turn = Patches.CanInjectHazeTurn(fillAtmo);
    if (!turn)
        ShadowBuilder.Log("WARN: the atmosphere data has no free ring slots; the haze turns about fixed axes");
    try
    {
        harmony.Patch(fillAtmo,
            prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.FillAtmosphereDataPrefix)),
            postfix: new HarmonyMethod(typeof(Patches),
                turn ? nameof(Patches.FillAtmosphereDataTurnPostfix) : nameof(Patches.FillAtmosphereDataPostfix)));
    }
    catch (Exception e)
    {
        ShadowBuilder.Log($"WARN: could not hook FillPlanetAtmosphereData ({e.GetType().Name}); haze animation is not sim-linked");
    }
    // The renderer itself, for the tables it keeps: they are worked out again when the haze bands are
    // switched (Patches.RendererSeenPrefix). A hook of its own, so that nothing above depends on it.
    if (!fillAtmo.IsStatic && Patches.CanForgetTables(atmoRenderer!))
    {
        try
        {
            harmony.Patch(fillAtmo, prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.RendererSeenPrefix)));
        }
        catch (Exception e)
        {
            ShadowBuilder.Log($"WARN: the atmosphere renderer was not reached ({e.GetType().Name}); switching the haze bands shows fully after the next change of body");
        }
    }
    else
        ShadowBuilder.Log("WARN: the atmosphere renderer's kept tables were not found; switching the haze bands shows fully after the next change of body");
}
else
    ShadowBuilder.Log("WARN: AtmosphereRenderer.FillPlanetAtmosphereData(body, atmoRef) not found; haze animation is not sim-linked");
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L101-L142)

5. **Install the independent hooks last, each with its own shape check.** `FlightAtmospheres.Install` and `NearbyBody.Install` run
   their own pre-checks; for example `RangeArgument` finds the `nearbyDistance` slot by name and type and returns -1 when
   absent:

```csharp
// NearbyBody.cs L117-L123
/// <summary>The argument slot of the method's nearbyDistance, or -1 if it has none.</summary>
private static int RangeArgument(MethodBase method)
{
    ParameterInfo[] parameters = method.GetParameters();
    int at = Array.FindIndex(parameters, p => p.Name == "nearbyDistance" && p.ParameterType == typeof(double));
    return at < 0 ? -1 : at + (method.IsStatic ? 0 : 1);
}
```

Source: [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L123)[^realatmospheres-nearbybody]

6. **Know what the outer `try` does not cover.** The wrapped `BeforeMain` catches only a throw *before* the first `Patch`. The
   lookups that throw for `Mod.LoadSystems` and `PrepareSystems` run after the asset-list prefixes are installed, and the one for
   `ShaderModuleUtils.FromFile` runs after all four XML prefixes, so a game that drops the four-parameter `FromFile` would load the
   patched XML with stock shaders and skip the flight, nearby-body and atmosphere patches
   ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L72-L99)).

## Complete example

| File | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L148) | `BeforeMain`, `Install`, lookups, per-subsystem `try` |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L285-L292) | `CanInjectSimTime` name check |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L419-L437) | `CanInjectHazeTurn` field-type check |
| [NearbyBody.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/NearbyBody.cs#L117-L123) | Argument slot by name and type |

## Variations and gotchas

- **No compiler help.** A misspelled member name or wrong cast is a runtime log line, not a build error; test each subsystem after
  a game update by reading the log.
- **Renames handled by trying both names.** The sim clock was `GetElapsedSimTime` and is now `Universe.GetElapsedTime`; the
  mod tries both, because the fail-soft lookup had once silently fallen back to wall-clock time.
- **Ref structs need IL.** `ImString` is a `ref struct` and cannot be boxed for `MethodInfo.Invoke`, so ImGui calls are emitted
  as `DynamicMethod`s.
- **Boxing costs.** Reflection on hot paths allocates; keep it out of per-substep prefixes.
- **Harmony still binds to real signatures.** Patch parameter types are `object`, but names must match the game's parameter names;
  check them in the decompile.

## See also

- [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md), [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md), [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md), [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md)
- [Isolating patch failures and keeping patch bodies from throwing](overview.md)
- [Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md)
- [Validating patch targets and detecting game-update breakage](../validate-patch-targets-and-detect-game-update-breakage/overview.md) and [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md)
- Related topic: build-a-mod-without-game-references

[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
[^realatmospheres-realatmospheres]: RealAtmospheres/RealAtmospheres.csproj
[^realatmospheres-modmain]: RealAtmospheres/ModMain.cs
[^realatmospheres-nearbybody]: RealAtmospheres/NearbyBody.cs
