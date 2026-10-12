---
type: How-To
title: Feature-tagged handle registry with a Validate per feature
description: Resolve every private game member once in one static class, tag each handle with the features that use it, validate per feature at load and skip a feature whose handle did not resolve while the others still load
category: patching
group: safety
topic: validate-patch-targets-and-detect-game-update-breakage
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/validated-reflection-handles]
techniques: [reflection, accesstools-fieldref]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - reflection KSA.TransferPlanner._sourceBody
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.Vehicle, KSA.FlightComputer, KSA.GameSettings]
tags: [reflection, validation, game-update, drift, feature-gating, fieldref]
---

KSA-AdvancedFlightComputer reaches into roughly forty private game members: static fields of the Transfer Planner, the
vehicle's manual control inputs, private methods such as `FlightComputer.UpdateAttitudeTarget`, and IL anchors for transpilers.
`GameReflection` resolves all of them once when the class initialises, tags each handle with the features that use it, and
exposes one `ValidateX()` per feature. A feature whose handle did not resolve is skipped at load with a log line that
names the missing member, and the other features still load.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md).

## Background: what drifts

The members are private or internal, so there is no compile-time reference to break: a rename shows up at run time as a
null `FieldInfo`. The Transfer Planner's state is a set of `private static` fields ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1603)[^ksa-transferplanner] shows
several in use), and the private `FlightComputer.UpdateAttitudeTarget` is called by `ComputeControl`
([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1277)[^ksa-flightcomputer]). Method lookups always pass an explicit parameter list so a new overload with the same
name cannot be picked by accident.

## Lifecycle and timing

The static initialiser runs the first time `GameReflection` is touched. The first touch is in `[StarMapImmediateLoad]`
(`startup:immediate-load`, a prefix on `KSA.Mod.PrepareSystems`), where `AutoStageFeature.InjectGaugeEnumAtLoad` calls
`ValidateAutoStage`; all game types are loadable by then because `KSA.dll` is running. The per-feature `Validate` calls
happen in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) before each feature's Harmony block. After that the handles are read
only on the main thread (planner, settings and gauge draws in `frame:draw-ui`, the staging execution, the guidance step in the
`PrepareWorker` prefix). No worker-side postfix touches `GameReflection`. The handles are `static readonly`, so no locking is
needed. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Tag features with a `[Flags]` enum and an attribute.** Each handle carries `[UsedBy(Feature.A | Feature.B)]`; handles that
   control one optional patch are tagged `SoftAnchor` and ignored by the validators:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L20-L45
internal static class GameReflection
{
    [Flags]
    private enum Feature
    {
        HyperbolicTargets = 1,
        ManeuverTools = 2,
        MultiPass = 4,
        RcsTranslation = 8,
        Core = 16,
        PlanWindow = 32,

        /// <summary>Handles that control one optional patch. Each patch checks its own handle.</summary>
        SoftAnchor = 64,
        GuidanceDiagnostics = 128,
        Guidance = 256,
        AutoStage = 512,
        AutoRemove = 1024,
        SettingsPage = 2048,
    }

    [AttributeUsage(AttributeTargets.Field)]
    private sealed class UsedByAttribute(Feature features) : Attribute
    {
        public Feature Features { get; } = features;
    }
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L20-L45)[^ksa-advancedflightcomputer-gamereflection]

2. **Declare handles as static readonly fields.** Method lookups pass explicit parameter lists, with `MakeByRefType()` for by-ref
   and `out` parameters:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L47-L59
#region TransferPlanner

[UsedBy(Feature.HyperbolicTargets | Feature.ManeuverTools | Feature.PlanWindow)]
public static readonly FieldInfo? TransferPlanner_sourceBody =
    AccessTools.Field(typeof(TransferPlanner), "_sourceBody");

[UsedBy(Feature.HyperbolicTargets | Feature.PlanWindow)]
public static readonly FieldInfo? TransferPlanner_transferInfo =
    AccessTools.Field(typeof(TransferPlanner), "_transferInfo");

[UsedBy(Feature.ManeuverTools | Feature.PlanWindow)]
public static readonly FieldInfo? TransferPlanner_selectedEntry =
    AccessTools.Field(typeof(TransferPlanner), "_selectedEntry");
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L47-L59)

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L176-L187
// This guard prevents the stock search from using a second body with a NaN Period.
// The last parameter is the out searchedUntil, which reflection sees as a by-ref type.
[UsedBy(Feature.SoftAnchor)]
public static readonly MethodInfo? PatchedConic_FindClosestApproaches =
    AccessTools.Method(typeof(PatchedConic), "FindClosestApproaches", new[]
    {
        typeof(Span<Encounter>),
        typeof(int).MakeByRefType(),
        typeof(IOrbiter),
        typeof(UniverseTime).MakeByRefType(),
        typeof(UniverseTime).MakeByRefType(),
    });
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L176-L187)

3. **Validate by self-reflection.** `Validate(feature)` enumerates the class's own public static fields, skips handles not tagged for
   that feature, and fails when a tagged value is null. A handle with no `UsedBy` fails every validation, so a key cannot be added
   without being covered. The message names the member in readable form:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L373-L395
private static bool Validate(Feature feature)
{
    bool allOk = true;
    HashSet<string>? reported = null;
    foreach (FieldInfo handle in typeof(GameReflection).GetFields(BindingFlags.Public | BindingFlags.Static))
    {
        if (handle.GetCustomAttribute<UsedByAttribute>() is not { } tag)
        {
            DefaultCategory.Log.Error($"[AFC] {handle.Name} carries no UsedBy tag, so no feature validates it.");
            allOk = false;
            continue;
        }
        if ((tag.Features & feature) == 0 || handle.GetValue(null) != null)
            continue;

        // A handle and its typed twin name the same field, so both get one line.
        string name = DisplayName(handle);
        if ((reported ??= new HashSet<string>()).Add(name))
            DefaultCategory.Log.Error($"[AFC] {feature}: {name} not found - game version may have changed.");
        allOk = false;
    }
    return allOk;
}
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L373-L395)

4. **Build typed accessors that return null on a type mismatch.** Hot fields get an `AccessTools.FieldRef` delegate (no boxing per
   access); the helper catches the exception that a type change causes, so a changed type is reported by the same validation as a
   missing field:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L414-L444
private static AccessTools.FieldRef<F>? StaticFieldRef<F>(FieldInfo? field)
{
    if (field == null)
        return null;
    try
    {
        return AccessTools.StaticFieldRefAccess<F>(field);
    }
    catch (Exception ex)
    {
        DefaultCategory.Log.Error(
            $"[AFC] {field.DeclaringType?.Name}.{field.Name} is not a {typeof(F).Name}: {ex.Message}");
        return null;
    }
}

private static AccessTools.FieldRef<T, F>? InstanceFieldRef<T, F>(FieldInfo? field) where T : class
{
    if (field == null)
        return null;
    try
    {
        return AccessTools.FieldRefAccess<T, F>(field);
    }
    catch (Exception ex)
    {
        DefaultCategory.Log.Error(
            $"[AFC] {field.DeclaringType?.Name}.{field.Name} is not a {typeof(F).Name}: {ex.Message}");
        return null;
    }
}
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L414-L444)

5. **Gate each feature on its validator.** `OnFullyLoaded` calls `Validated(name, GameReflection.ValidateX)` before `TryApply`
   (see [One Harmony owner per feature with TryApply, validated handles and runtime flags](../refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md)):

```csharp
// AdvancedFlightComputer/Mod.cs L106-L112
private static bool Validated(string feature, Func<bool> validate)
{
    if (validate())
        return true;
    DefaultCategory.Log.Warning($"[AFC] {feature} disabled - reflection targets not found.");
    return false;
}
```

Source: [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L106-L112)[^ksa-advancedflightcomputer-mod]

6. **Test the drift headlessly.** The headless test `afc-reflection-targets` runs every validator, every transpiler anchor probe and a
   read/write round trip through the typed accessors against the live game, so a rename fails in CI rather than at a player's
   load ([tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs)[^ksa-advancedflightcomputer-reflectiontargetstest]; the
   topic test-harmony-patches-without-the-game in the testing-and-tooling category covers the harness).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs) | Handles, tags, validators, typed accessors |
| [AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs#L22-L66)[^ksa-advancedflightcomputer-stockplanner] | Thin wrappers over the planner handles |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L112) | Validate, then patch |

## Variations and gotchas

- **A private method that a game update marks `AggressiveInlining` still resolves by reflection, but a Harmony patch on it goes
  silent.** Only a behavioural test catches it ([Postfix the single non-inlined outer method instead of a per-vehicle callee](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md)).
- **By-ref and `out` parameters need `MakeByRefType()`** in the type list, or the lookup returns null.
- **A private nested enum cannot be named from mod code.** Resolve the field, check `FieldType.IsEnum`, `Enum.TryParse` the member
  once and compare boxed values.
- **Static `FieldRef` delegates are only valid for static fields;** `FieldRefAccess<T, F>` is needed for instance fields, with `T` a
  reference type.
- **Soft anchors.** Handles tagged `SoftAnchor` (for example `PatchedConic.FindClosestApproaches`) control only their own optional
  patch, which checks the handle itself and logs when it is absent.

## See also

- [Non-throwing static constructor with fatal and degraded lists](validate-patch-targets-and-detect-game-update-breakage-2.md), [Pin patched members with a never-called method that really calls them](validate-patch-targets-and-detect-game-update-breakage-3.md)
- [Validating patch targets and detecting game-update breakage](overview.md)
- [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
- [Typed FieldRefAccess, boxed private enum members and by-ref lookups](../../internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-3.md) for the field-ref forms
- Related topics: upgrade-a-mod-to-a-new-ksa-build, pin-the-game-build-with-a-hashed-lock

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
[^ksa-advancedflightcomputer-mod]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Mod.cs
[^ksa-advancedflightcomputer-reflectiontargetstest]: KSA-AdvancedFlightComputer/tests/AdvancedFlightComputer.HarnessTests/Features/Core/ReflectionTargetsTest.cs
[^ksa-advancedflightcomputer-stockplanner]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/StockPlanner.cs
