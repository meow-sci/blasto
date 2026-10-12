---
type: How-To
title: Typed FieldRefAccess, boxed private enum members and by-ref lookups
description: Use AccessTools.StaticFieldRefAccess for static fields and FieldRefAccess<T,F> for instance fields of reference types, compare a private nested enum member as a boxed value resolved once from the field's FieldType, and express by-ref parameters with MakeByRefType
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/validated-reflection-handles]
techniques: [accesstools-fieldref, reflection]
hooks:
  - reflection KSA.TransferPlanner._transferCalculated
  - reflection KSA.GameSettings._openTab
  - reflection KSA.Vehicle._manualControlInputs
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.TransferPlanner, KSA.GameSettings, KSA.Vehicle, KSA.FlightComputer]
tags: [reflection, accesstools, fieldref, private-enum, boxed, makebyreftype]
---

KSA-AdvancedFlightComputer reaches roughly forty private game members. For hot reads and writes it turns each `FieldInfo` into a typed accessor once and calls it as a delegate with no boxing; it
compares a private nested enum (`GameSettings.SettingsTab`) by resolving a boxed member once; and it expresses by-ref parameters with `MakeByRefType()` so overloaded lookups are exact. This article is about
those reflection forms; the validation scheme built on top is in [Feature-tagged handle registry with a Validate per feature](../../safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-1.md).

Prerequisites: a StarMap mod referencing `0Harmony`. Demonstrated by AFC in [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md).

## Background: how the game does it

The members are private. `GameSettings.SettingsTab` is a private nested enum, so mod code cannot name it, and `_openTab` holds one; `Vehicle._manualControlInputs` is a private instance field of a struct type;
the planner's state is `private static`:

```csharp
// current/decomp/KSA/GameSettings.cs L150-L160, L1104
private enum SettingsTab
{
	Controls,
	Simulation,
	Console,
	Mods,
	Display,
	Graphics,
	Audio,
	Interface
}
// ...
private static SettingsTab _openTab = SettingsTab.Display;
```

Source: [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150-L1104)[^ksa-gamesettings]

```csharp
// current/decomp/KSA/Vehicle.cs L239
private ManualControlInputs _manualControlInputs = new ManualControlInputs();
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L239)[^ksa-vehicle]

## Lifecycle and timing

The static initialiser of `GameReflection` runs on first touch (`[StarMapImmediateLoad]`, `startup:immediate-load`, via the gauge-enum injection); validation runs in `[StarMapAllModsLoaded]`
(`startup:all-mods-loaded`); the accessors are then read on the main thread (planner, settings and gauge draws in `frame:draw-ui`, the guidance step in the `PrepareWorker` prefix). The handles are
`static readonly`, so no locking is needed. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Static fields: `StaticFieldRefAccess<F>` behind a helper that returns null on a mismatch.** The helper catches the exception that a type mismatch causes, logs which member is not the expected type,
   and returns null so the validation reports it like any other missing handle:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L414-L428
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
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L414-L428)[^ksa-advancedflightcomputer-gamereflection]

   Each accessor is a `static readonly` field, so a read is a delegate call that returns a `ref`:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L150-L158
    StaticFieldRef<TransferType>(TransferPlanner_transferType);

[UsedBy(Feature.ManeuverTools | Feature.PlanWindow)]
public static readonly AccessTools.FieldRef<bool>? TransferPlanner_transferCalculatedRef =
    StaticFieldRef<bool>(TransferPlanner_transferCalculated);

[UsedBy(Feature.ManeuverTools | Feature.PlanWindow)]
public static readonly AccessTools.FieldRef<bool>? TransferPlanner_transferBeingCalculatedRef =
    StaticFieldRef<bool>(TransferPlanner_transferBeingCalculated);
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L150-L158)

2. **Instance fields: `FieldRefAccess<T, F>`, with `T` a reference type.** `where T : class` is enforced by the helper:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L430-L444
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

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L430-L444)

   `Vehicle_manualControlInputsRef` is built from the `FieldInfo` of `Vehicle._manualControlInputs`:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L331-L338
// The engine master switch and throttle that the player's ignite and shutdown actions write.
[UsedBy(Feature.Guidance)]
public static readonly FieldInfo? Vehicle_manualControlInputs =
    AccessTools.Field(typeof(Vehicle), "_manualControlInputs");

[UsedBy(Feature.Guidance)]
public static readonly AccessTools.FieldRef<Vehicle, ManualControlInputs>? Vehicle_manualControlInputsRef =
    InstanceFieldRef<Vehicle, ManualControlInputs>(Vehicle_manualControlInputs);
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L331-L338)

3. **A private nested enum: resolve the member once, compare boxed.** The enum cannot be named. Resolve the `FieldInfo`, check `FieldType.IsEnum`, `Enum.TryParse(fieldType, "Mods", out object)` once, keep the
   boxed value, and compare with `Equals` against the field's current value:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L266-L275
// Which settings page the nav rail has open, read by the shared Mods settings section host.
// The enum is private to GameSettings, so its Mods member is resolved as a boxed value once and compared by equality.
// Its own feature, so a settings-window rename costs the sections only.
[UsedBy(Feature.SettingsPage)]
public static readonly FieldInfo? GameSettings_openTab =
    AccessTools.Field(typeof(GameSettings), "_openTab");

[UsedBy(Feature.SettingsPage)]
public static readonly object? GameSettings_openTab_Mods =
    GameSettings_openTab?.FieldType is { IsEnum: true } tab && Enum.TryParse(tab, "Mods", out object? mods) ? mods : null;
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L266-L275)

   The call site: `mods.Equals(GameReflection.GameSettings_openTab!.GetValue(null))` ([AdvancedFlightComputer/Core/ModSettingsPage.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/ModSettingsPage.cs#L71-L73)[^ksa-advancedflightcomputer-modsettingspage]).

4. **By-ref and `out` parameters need `MakeByRefType()` in the lookup.** In metadata `in`, `ref` and `out` are all by-reference types, so the explicit parameter array must say so, or the lookup returns null.
   The same list also stops a later overload with the same name from being picked:

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

5. **Tag the handle with the features that use it** (`[UsedBy(...)]`) so a null handle disables exactly those features.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs) | All handles, typed accessors and validators |
| [AdvancedFlightComputer/Core/StockPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockPlanner.cs#L22-L66)[^ksa-advancedflightcomputer-stockplanner] | Property wrappers that hide the accessors from callers |

## Variations and gotchas

- **Static `FieldRef` delegates are only valid for static fields,** and `FieldRefAccess<T, F>` needs a reference type `T`; a struct owner needs a different approach.
- **A type change is reported by the same validation as a missing field,** because the helper returns null for both.
- **A method handle keeps its name; a typed twin of a field handle ends in `Ref`.** `DisplayName` turns `TransferPlanner_sourceBodyRef` into `TransferPlanner._sourceBody` so the warning names the game member.
- **Boxing happens once, at resolve time,** not per access.
- **`AggressiveInlining` can silence a patch on a method you resolved fine** ([Postfix the single non-inlined outer method instead of a per-vehicle callee](../patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md)).

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [Delegates over private editor helpers reused in a replacement drag](call-private-game-members-through-reflection-and-delegates-5.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
- [Feature-tagged handle registry with a Validate per feature](../../safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-1.md)

[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
[^ksa-advancedflightcomputer-modsettingspage]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/ModSettingsPage.cs
[^ksa-advancedflightcomputer-stockplanner]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/StockPlanner.cs
